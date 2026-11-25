using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using SourceGit.Native;

internal static class GitDiscoveryProbe
{
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--measure-settings")
        {
            var timer = Stopwatch.StartNew();
            var version = GitExecutableLocator.ReadBundledVersion(args[1]);
            var settingsMilliseconds = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var verified = GitRuntimeArchive.VerifyForUpdate(args[1], GitRuntimeArchive.ReadUpdateIdentity(args[1]));
            Console.WriteLine(JsonSerializer.Serialize(new { version, settingsMilliseconds, verified, verificationMilliseconds = timer.Elapsed.TotalMilliseconds }));
            return version != null && verified ? 0 : 1;
        }
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
        CheckSettingsAvailability(root, Check);
        if (args.Length > 1) Check("the real bundled Git is runnable", GitExecutableLocator.IsUsable(Path.GetFullPath(args[1])));
        if (args.Length > 2)
        {
            var app = Path.Combine(root, "on-demand");
            Directory.CreateDirectory(app);
            try
            {
                using (var zip = ZipFile.OpenRead(args[2]))
                    foreach (var relative in new[] { "cmd/git.exe", "runtime.zip", "payload-manifest.json", "runtime-manifest.json" })
                    {
                        var destination = Path.Combine(app, "git", relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        zip.GetEntry("TreeGit/git/" + relative)!.ExtractToFile(destination);
                    }
                var gitDirectory = Path.Combine(app, "git");
                var inner = Path.Combine(gitDirectory, "runtime.zip");
                var payloadManifest = Path.Combine(gitDirectory, "payload-manifest.json");
                var originalManifest = File.ReadAllBytes(payloadManifest);
                Check("failed validation retains the compressed payload",
                    GitRuntimeArchive.FindOrExtract(app, null, _ => false) == null && File.Exists(inner) && File.Exists(payloadManifest));
                var cached = GitRuntimeArchive.FindOrExtract(app, null, GitExecutableLocator.IsUsable);
                Check("a compact release expands to a working offline Git", GitExecutableLocator.IsUsable(cached));
                var stamp = File.GetLastWriteTimeUtc(cached);
                Check("activation releases the archive and restores the original manifest",
                    !File.Exists(inner) && !File.Exists(payloadManifest) && originalManifest.AsSpan().SequenceEqual(
                        File.ReadAllBytes(Path.Combine(gitDirectory, "runtime-manifest.json"))));
                Check("the verified runtime is reused in place without another extraction",
                    GitRuntimeArchive.FindOrExtract(app, null) == cached && File.GetLastWriteTimeUtc(cached) == stamp &&
                    !Directory.Exists(Path.Combine(app, GitRuntimeArchive.CacheDirectory)));
            }
            finally { Directory.Delete(app, true); }
        }
        File.WriteAllText(Path.Combine(root, "git-discovery-results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static void CheckSettingsAvailability(string root, Action<string, bool> check)
    {
        const string version = "2.56.0.windows.4";
        var expanded = Path.Combine(root, "settings-expanded");
        var compact = Path.Combine(root, "settings-compact");
        var payload = new[]
        {
            "cmd/git.exe", "ucrt64/bin/git.exe", "usr/bin/sh.exe", "usr/bin/ssh.exe", "usr/bin/ssh-keygen.exe",
            "ucrt64/etc/ssl/certs/ca-bundle.crt", "ucrt64/libexec/git-core/git-flow.exe",
            "ucrt64/libexec/git-core/git-lfs.exe", "etc/gitconfig", "other-component.dat",
        };

        static string Write(string git, string name, string value)
        {
            var path = Path.Combine(git, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
            return path;
        }

        void Manifest(string git, string name, string[] files) => Write(git, name, JsonSerializer.Serialize(new
        {
            gitVersion = version,
            files = files.Select(path => new { path, sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(git, path)))) }),
        }));

        var expandedGit = Path.Combine(expanded, "git");
        foreach (var file in payload) Write(expandedGit, file, file);
        Manifest(expandedGit, "runtime-manifest.json", payload);
        var identity = GitRuntimeArchive.ReadUpdateIdentity(expanded);
        check("expanded settings use the shipped version and see the key components", GitExecutableLocator.ReadBundledVersion(expanded) == version);
        check("expanded activation verifies all shipped files", GitRuntimeArchive.VerifyForUpdate(expanded, identity));
        Write(expandedGit, "other-component.dat", "modified");
        check("opening settings does not hash every expanded file", GitExecutableLocator.ReadBundledVersion(expanded) == version);
        check("expanded integrity verification still rejects an altered component", !GitRuntimeArchive.VerifyForUpdate(expanded, identity));
        File.Delete(Path.Combine(expandedGit, "usr/bin/ssh.exe"));
        check("settings reject an expanded bundle missing a key file", GitExecutableLocator.ReadBundledVersion(expanded) == null);

        var compactGit = Path.Combine(compact, "git");
        var packaged = new[] { "cmd/git.exe", "runtime.zip", "payload-manifest.json", "COMPONENTS.md", "LICENSE.txt", "licenses/Git-LFS-LICENSE.md", "licenses/Git-Flow-LICENSE" };
        foreach (var file in packaged.Where(file => file is not ("runtime.zip" or "payload-manifest.json"))) Write(compactGit, file, file);
        File.Copy(Path.Combine(expandedGit, "runtime-manifest.json"), Path.Combine(compactGit, "payload-manifest.json"));
        var archive = Path.Combine(compactGit, "runtime.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            foreach (var file in payload)
                using (var writer = new StreamWriter(zip.CreateEntry(file).Open())) writer.Write(file);
        Manifest(compactGit, "runtime-manifest.json", packaged);
        check("compact settings use the packaged version without extracting", GitExecutableLocator.ReadBundledVersion(compact) == version && !Directory.Exists(Path.Combine(compactGit, "ucrt64")));
        check("compact integrity verification covers the original archive", GitRuntimeArchive.VerifyForUpdate(compact, identity));
        File.AppendAllText(archive, "tampered");
        check("opening settings does not hash the compact archive", GitExecutableLocator.ReadBundledVersion(compact) == version);
        check("compact integrity verification still rejects an altered archive", !GitRuntimeArchive.VerifyForUpdate(compact, identity));
        File.Delete(archive);
        check("settings reject a compact bundle missing its archive", GitExecutableLocator.ReadBundledVersion(compact) == null);
    }
}
