# sigmastudio-plus-action

Build Analog Devices SigmaDSP programs in GitHub Actions. This composite action
installs [SigmaStudio+](https://www.analog.com/sigmastudio-plus) silently on a
Windows runner, from **your own privately staged copy** of the installer, and
hands your workflow its headless console app (`SStudioPlusConsole.exe`) to
build and export projects without the GUI.

There is no open toolchain for SigmaDSP parts (ADAU145x/146x and friends): the
instruction set is not public. SigmaStudio+ is the only compiler, and it is a
Windows GUI app, which also ships a console app and a Thrift scripting API.
This action is the glue that runs those in CI.

## This action contains no Analog Devices software

SigmaStudio+ is ADI software, downloadable only after signing in to myAnalog and
accepting ADI's software license. **You** download it, read and accept that
license, and stage the installer somewhere private that your workflow can read.
The action never fetches it from ADI and never accepts a license on your behalf.
Check that ADI's license allows the use you put it to, including what you do
with exported programs: per its terms as quoted by ADI staff, generated code may
be loaded onto ADI parts in your products but not distributed in other forms.

## Staging the installer

1. Download the installer from ADI (`adi_sigmastudioplus-rel<version>.exe`).
2. Create a **private** repository and upload it there as a release asset.
3. Create a fine-grained token with **Contents: read** on that one repository.
4. In the repository that runs the workflow, add two secrets:
   - `SIGMASTUDIO_INSTALLER_URL`: the asset's API URL,
     `https://api.github.com/repos/<owner>/<private-repo>/releases/assets/<id>`
     (`gh api repos/<owner>/<private-repo>/releases/tags/<tag> --jq '.assets[].url'`)
   - `SIGMASTUDIO_TOKEN`: the token.

## Usage

```yaml
jobs:
  dsp:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: The1TrueJoe/sigmastudio-plus-action@v1
        with:
          installer-url: ${{ secrets.SIGMASTUDIO_INSTALLER_URL }}
          token: ${{ secrets.SIGMASTUDIO_TOKEN }}
          installer-sha256: <sha256 of your installer>
          run: |
            & $env:SSPLUS_CONSOLE --help
```

| Input | |
|---|---|
| `installer-url` | where your installer is staged (required) |
| `token` | bearer token for the download |
| `installer-sha256` | expected hash; the download is refused on a mismatch |
| `run` | PowerShell to run afterwards, with `$env:SSPLUS_CONSOLE` and `$env:SSPLUS_HOME` set |
| `install-timeout-minutes` | per-attempt limit for the silent install (default 10) |
| `script-host` | `'true'` also builds the scripting host (below) and sets `$env:SSPLUS_SCRIPT` |

| Output | |
|---|---|
| `console` | full path of `SStudioPlusConsole.exe` |
| `home` | the SigmaStudio+ install folder |
| `script` | full path of `ssplus-script.exe`, with `script-host: 'true'` |

They are also exported to later steps as `SSPLUS_CONSOLE`, `SSPLUS_HOME` and
`SSPLUS_SCRIPT`.

## Scripting a design headless

SigmaStudio+'s scripting API (AddShape, AddConnection, UpdateProperties,
LinkCompileDownload, Export, ...) is served over Thrift by the GUI only. With
`script-host: 'true'` the action builds a small host, `ssplus-script.exe`, on
the runner against the installed SigmaStudio+. It makes the same SDK facade the
console app makes, without a GUI, and calls the scripting API's own classes in
process. The calls, their arguments and their results are the documented API's.
Nothing of SigmaStudio+ is in this repository; the host is compiled against the
copy you installed.

```powershell
& $env:SSPLUS_SCRIPT design.json logs   # logs: where sigmastudio.log goes
```

The design is a JSON array of calls, run in order; the first that reports
failure stops it with exit code 1:

```json
[
  ["OpenExistingProject", "${env:TEMPLATE}"],
  ["MigrateTarget", "ADAU1452_0", "ADAU1451", true],
  {"call": "AddShapeV2", "args": ["schematic", "AnalogDevices.Plugins.Algorithms.IO.Sigma300.Sample.ASRCInput_v1.0.0.0", [100, 100]], "as": "in"},
  ["AddConnectionV2", "${in}", "Ch0_out", "MultipleVolumeControl_1", "Input0"],
  ["LinkCompileConnect"],
  ["Export", "my-dsp", "${env:OUT}\\export"]
]
```

* An array is a call's name, then its arguments. An object adds `"as"` (keep the
  UID the call reports as `${name}`) and `"allow-fail"`.
* `${env:NAME}` is an environment variable.
* `[x, y]` is a Position (or a Size); arguments otherwise follow the call's
  parameter types.
* `LinkCompileConnect` compiles without hardware; no USBi is needed.
* A plugin's short name works when it is unique; otherwise give its full name
  (`GetAllPlugins` lists them).

What the API cannot do, learned the hard way:

* **It cannot add a PC (controller) shape**, and the compiler refuses a system
  whose processor has no host link. Start from a project that has one:
  SigmaStudio+'s own examples (`ProgramData\Analog Devices\SigmaStudio <ver>\
  Plugins\Templates`) do. Open one, `MigrateTarget` its processor to your part,
  and replace its algorithms.
* **A shape with links cannot be removed**: `RemoveConnectionV2` its pins first.
* `CreateNewProject` creates the project and then fails reporting it (no shapes
  yet); mark it `allow-fail` if you use it.

## How the install works

The installer's technology is read off the binary. SigmaStudio+ 3.x is a
classic InstallShield `setup.exe` around an MSI (`NGSS_<version>.msi`): it
relaunches from temp, checks its .NET 4.7.2 prerequisite and runs `msiexec /i`.
Its silent form is `/s /v"/qn"`; without `/qn` the MSI shows its full UI and
waits at a dialog nobody can see on a runner. Every attempt is bounded, killed
if it hangs, and the next one tried, so the job fails with the installer's own
logs rather than sitting out the six-hour job limit.

## License

MIT, for this action. SigmaStudio+ is under Analog Devices' own license.
