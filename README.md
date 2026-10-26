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

| Output | |
|---|---|
| `console` | full path of `SStudioPlusConsole.exe` |
| `home` | the SigmaStudio+ install folder |

Both are also exported to later steps as `SSPLUS_CONSOLE` and `SSPLUS_HOME`.

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
