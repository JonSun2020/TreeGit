using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using SourceGit.Native;

internal static class GitDiscoveryProbe
{
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--version")
        {
            var folder = AppContext.BaseDirectory;
            if (folder.Contains("_hang")) Thread.Sleep(60000);
            if (folder.Contains("_broken")) { Console.Error.WriteLine("broken installation"); return 2; }
            Console.WriteLine(folder.Contains("_old") ? "git version 2.20.0" : "git version 2.56.0.windows.4");
            if (folder.Contains("_verbose"))
                for (var i = 0; i < 2000; i++) { Console.WriteLine(new string('x', 256)); Console.Error.WriteLine(new string('e', 256)); }
            return 0;
        }
        var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Check(string label, bool condition) { if (!condition) throw new Exception(label); checks.Add(label); Console.WriteLine("PASS " + label); }
        string Fixture(string label)
        {
            var runtime = Path.Combine(root, label);
            var cmd = Path.Combine(runtime, "cmd"); Directory.CreateDirectory(cmd);
            foreach (var file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(cmd, Path.GetFileName(file)), true);
            File.Copy(Environment.ProcessPath!, Path.Combine(cmd, "git.exe"), true);
            Directory.CreateDirectory(Path.Combine(runtime, "ucrt64", "bin"));
            Directory.CreateDirectory(Path.Combine(runtime, "usr", "bin"));
            File.WriteAllText(Path.Combine(runtime, "usr", "bin", "ssh.exe"), "not executed; path fixture");
            return Path.Combine(cmd, "git.exe");
        }
        var installed = Fixture("_system"); var bundled = Fixture("_bundle");
        var broken = Fixture("_broken"); var old = Fixture("_old"); var hang = Fixture("_hang");
        var missing = Path.Combine(root, "missing", "git.exe");
        Check("an installed Git wins over a valid bundled Git", GitExecutableLocator.Resolve([installed], bundled) == installed);
        Check("an unavailable installed path is skipped", GitExecutableLocator.Resolve([missing, installed], bundled) == installed);
        Check("a broken executable is skipped", GitExecutableLocator.Resolve([broken, installed], bundled) == installed);
        Check("an unsupported installed version falls back", GitExecutableLocator.Resolve([old], bundled) == bundled);
        Check("no installation uses the bundled executable", GitExecutableLocator.Resolve([], bundled) == bundled);
        Check("missing both installations returns no Git", GitExecutableLocator.Resolve([missing], missing) == null);
        Check("an invalid bundled executable is not accepted", GitExecutableLocator.Resolve([], broken) == null);
        var corrupt = Path.Combine(root, "corrupt.exe"); File.WriteAllText(corrupt, "not an executable");
        Check("malformed executable files are rejected before native process creation", GitExecutableLocator.Resolve([corrupt], bundled) == bundled);
        Check("a bundled PATH entry cannot hide an installed Git", GitExecutableLocator.Resolve([bundled, installed], bundled) == installed);
        Check("invalid candidate names do not stop discovery", GitExecutableLocator.Resolve(["\0", "", installed], bundled) == installed);
        var stopwatch = Stopwatch.StartNew();
        Check("a hanging executable times out and is tried only once", GitExecutableLocator.Resolve([hang, hang], bundled) == bundled && stopwatch.Elapsed.TotalSeconds < 5.5);
        Check("both output pipes are drained without deadlock", GitExecutableLocator.Resolve([Fixture("_verbose")], bundled) != bundled);
        var start = new ProcessStartInfo(); start.Environment["PATH"] = "original-path";
        Check("SSH and PATH follow the selected runtime", GitExecutableLocator.ConfigureProcess(start, installed) == Path.Combine(root, "_system", "usr", "bin", "ssh.exe") &&
            start.Environment["PATH"].StartsWith(Path.Combine(root, "_system", "cmd")) && !start.Environment["PATH"].Contains("_bundle") && start.Environment["PATH"].EndsWith("original-path"));
        if (args.Length > 1) Check("the real bundled Git is runnable", GitExecutableLocator.IsUsable(Path.GetFullPath(args[1])));
        File.WriteAllText(Path.Combine(root, "git-discovery-results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
