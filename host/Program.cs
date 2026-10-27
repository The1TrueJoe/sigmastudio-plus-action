// ssplus-script: run a SigmaStudio+ scripting session headless, in-process.
//
// SigmaStudio+ scripts through a Thrift server that only its GUI hosts. Every
// call that server answers is a plugin class in ScriptingAPISS+.dll (AddShapeV2
// is AddShapeV2Class), built on the same SDK facade the console app
// (SStudioPlusConsole.exe) runs without a GUI. This host makes that facade the
// way the console app does and calls those classes directly: the calls, their
// arguments and their results are exactly the documented scripting API's, with
// no server, no GUI and nothing of ours in between.
//
//   ssplus-script.exe script.json [log-folder]
//
// The script is a JSON array of calls, run in order:
//
//   ["CreateNewProject", "my-project"]
//   {"call": "AddShapeV2", "args": ["schematic_0", "Input0to15", [0, 0]], "as": "in"}
//   {"call": "AddConnectionV2", "args": ["${in}", "Ch0_out", "${out}", "Input"]}
//
// An array is the call's name then its arguments; an object adds "as" (keep the
// UID the call reports, its last message's last word, as ${name}) and "allow-fail".
// ${env:NAME} in a string is an environment variable.
// Arguments follow the call's parameter types: a [x, y] pair is a Position or
// Size, an array of numbers a list, anything else its string form. A call that
// reports failure stops the script with exit code 1.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using AnalogDevices.Components.ComponentManagerInternals;
using AnalogDevices.Components.Sdk.SdkFacadeInternals;
using AnalogDevices.TypeDefinitions.Events;
using AnalogDevices.TypeDefinitions.Sdk;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Events;

static class Program
{
    static int Main(string[] argv)
    {
        if (argv.Length < 1)
        {
            Console.Error.WriteLine("usage: ssplus-script script.json [log-folder]");
            return 2;
        }
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        var logs = argv.Length > 1 ? argv[1] : Path.GetTempPath();
        Directory.CreateDirectory(logs);

        // As SStudioPlusConsole.exe sets itself up.
        var cm = ComponentManager.Instance;
        cm.Register<ILogger>(new LoggerConfiguration()
            .WriteTo.File(Path.Combine(logs, "sigmastudio.log"), LogEventLevel.Verbose)
            .CreateLogger());
        cm.Register<ISdkFacade>(typeof(SdkFacade), true);
        var facade = cm.Create<ISdkFacade>();
        facade.OutputMessagesEvent += (s, e) =>
        {
            foreach (var m in e.Messages) Console.WriteLine("  | " + m.Text);
        };

        var api = LoadScriptingApi(facade);
        var vars = new Dictionary<string, string>();
        var script = JArray.Parse(File.ReadAllText(argv[0]));
        foreach (var step in script)
        {
            var (name, args, keep, allowFail) = Parse(step);
            if (!api.TryGetValue(name, out var target))
            {
                Console.Error.WriteLine($"{name}: no such scripting call");
                return 1;
            }
            var call = target.Method.GetParameters().Length == args.Count
                ? target.Method
                : target.Type.GetMethods().FirstOrDefault(m => m.Name == target.Method.Name && m.GetParameters().Length == args.Count);
            if (call == null)
            {
                Console.Error.WriteLine($"{name}: takes {target.Method.GetParameters().Length} arguments, got {args.Count}");
                return 1;
            }
            var values = call.GetParameters().Select((p, i) => Convert(args[i], p.ParameterType, vars)).ToArray();
            Console.WriteLine($"> {name}({string.Join(", ", values.Select(Show))})");
            object result;
            try
            {
                result = call.Invoke(target.Instance, values);
            }
            catch (TargetInvocationException e)
            {
                Console.Error.WriteLine($"{name}: {Inner(e).Message}");
                if (allowFail) continue;
                return 1;
            }
            var (ok, lines) = Report(result);
            foreach (var l in lines) Console.WriteLine("  " + l);
            if (!ok && !allowFail)
            {
                Console.Error.WriteLine($"{name}: failed");
                return 1;
            }
            if (keep != null)
            {
                var last = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
                vars[keep] = Regex.Match(last, @"(\S+)\s*$").Groups[1].Value.TrimEnd('.');
                Console.WriteLine($"  ${{{keep}}} = {vars[keep]}");
            }
        }
        return 0;
    }

    sealed class Target
    {
        public Type Type;
        public object Instance;
        public MethodInfo Method;
    }

    // Each call is <Call>Class, made with the facade; its one public method that
    // is not Thrift plumbing is the call.
    static Dictionary<string, Target> LoadScriptingApi(ISdkFacade facade)
    {
        var dll = facade.GetPluginFolders()
            .Select(d => Path.Combine(d, "ScriptingAPISS+.dll"))
            .FirstOrDefault(File.Exists)
            ?? Directory.GetFiles(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                  "ScriptingAPISS+.dll", SearchOption.AllDirectories).FirstOrDefault();
        if (dll == null) throw new FileNotFoundException("ScriptingAPISS+.dll is not in any SigmaStudio+ plugin folder");
        var plumbing = new HashSet<string> { "ProcessFunction", "RegisterProcess", "MsgToAdd" };
        var api = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Assembly.LoadFrom(dll).GetExportedTypes())
        {
            if (!t.IsClass || t.IsAbstract || !t.Name.EndsWith("Class") && !t.Name.EndsWith("Register")) continue;
            var ctor = t.GetConstructor(new[] { typeof(ISdkFacade) });
            if (ctor == null) continue;
            var method = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => !m.IsSpecialName && !plumbing.Contains(m.Name));
            if (method == null) continue;
            var name = t.Name.EndsWith("Class") ? t.Name.Substring(0, t.Name.Length - 5) : t.Name;
            api[name] = new Target { Type = t, Instance = ctor.Invoke(new object[] { facade }), Method = method };
        }
        return api;
    }

    static (string, List<JToken>, string, bool) Parse(JToken step)
    {
        if (step is JArray a)
            return ((string)a[0], a.Skip(1).ToList(), null, false);
        var o = (JObject)step;
        return ((string)o["call"], (o["args"] as JArray)?.ToList() ?? new List<JToken>(),
                (string)o["as"], (bool?)o["allow-fail"] ?? false);
    }

    // ${name} is a kept UID, ${env:NAME} an environment variable.
    static string Expand(string s, Dictionary<string, string> vars) =>
        Regex.Replace(s, @"\$\{([^}]+)\}", m =>
        {
            var k = m.Groups[1].Value;
            var v = k.StartsWith("env:") ? Environment.GetEnvironmentVariable(k.Substring(4)) : vars.TryGetValue(k, out var u) ? u : null;
            return v ?? throw new KeyNotFoundException("${" + k + "} is not set");
        });

    static object Convert(JToken v, Type t, Dictionary<string, string> vars)
    {
        if (t == typeof(string))
            return v.Type == JTokenType.String ? Expand((string)v, vars) : v.ToString(Newtonsoft.Json.Formatting.None);
        if (t == typeof(bool)) return v.Type == JTokenType.String ? bool.Parse(Expand((string)v, vars)) : (bool)v;
        if (t == typeof(int)) return (int)v;
        if (t == typeof(double)) return (double)v;
        if (typeof(IList).IsAssignableFrom(t) && t.IsGenericType)
        {
            var list = (IList)Activator.CreateInstance(t);
            var item = t.GetGenericArguments()[0];
            foreach (var x in (JArray)v) list.Add(Convert(x, item, vars));
            return list;
        }
        // Thrift structs: Position (X, Y) and Size (Height, Width) from [a, b].
        var o = Activator.CreateInstance(t);
        var pair = (JArray)v;
        var props = t.Name == "Size" ? new[] { "Width", "Height" } : new[] { "X", "Y" };
        for (int i = 0; i < 2; i++) t.GetProperty(props[i]).SetValue(o, (double)pair[i]);
        return o;
    }

    static string Show(object o)
    {
        if (o is string s) return "\"" + s + "\"";
        if (o is IEnumerable e) return "[" + string.Join(", ", e.Cast<object>()) + "]";
        var x = o.GetType().GetProperty("X");
        if (x != null) return $"({x.GetValue(o)}, {o.GetType().GetProperty("Y").GetValue(o)})";
        return o.ToString();
    }

    // SSPResult is IsSuccess + Message; Get* calls return plain lists and maps.
    static (bool, List<string>) Report(object r)
    {
        if (r == null) return (true, new List<string>());
        var t = r.GetType();
        var ok = t.GetProperty("IsSuccess");
        if (ok != null)
            return ((bool)ok.GetValue(r), ((IEnumerable)t.GetProperty("Message").GetValue(r))?.Cast<object>().Select(x => x.ToString()).ToList() ?? new List<string>());
        if (r is IDictionary d)
            return (true, d.Keys.Cast<object>().Select(k => $"{k} = {Describe(d[k])}").ToList());
        if (r is IEnumerable e && !(r is string))
            return (true, e.Cast<object>().Select(x => x.ToString()).ToList());
        return (true, new List<string> { Describe(r) });
    }

    static string Describe(object o) =>
        o == null ? "null" : string.Join(", ", o.GetType().GetProperties()
            .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "__isset")
            .Select(p => $"{p.Name}: {p.GetValue(o)}"));

    static Exception Inner(Exception e) => e.InnerException == null ? e : Inner(e.InnerException);
}
