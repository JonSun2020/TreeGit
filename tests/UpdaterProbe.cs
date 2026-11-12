using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit
{
    [JsonSerializable(typeof(Models.Version))]
    internal partial class JsonCodeGen : JsonSerializerContext { }
}

namespace SourceGit.Models
{
    internal static class DateTimeFormat
    {
        public static string Format(DateTime date, bool _) => date.ToString("O");
    }
}

namespace SourceGit.Tests
{
    internal static class UpdaterProbe
    {
        private static readonly List<string> Checks = [];
        private static string _out;

        private static void Check(string name, bool pass)
        {
            if (!pass) throw new Exception(name);
            Checks.Add(name);
            Console.WriteLine("PASS " + name);
        }

        private static string NewRoot()
        {
            var root = Path.Combine(_out, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "TreeGit.exe"), "old program");
            File.WriteAllText(Path.Combine(root, "build-info.json"), "{\"product\":\"TreeGit\",\"version\":\"2026.23.10\",\"integrationTests\":false}");
            Directory.CreateDirectory(Path.Combine(root, "data"));
            Directory.CreateDirectory(Path.Combine(root, "log"));
            File.WriteAllText(Path.Combine(root, "data/user.txt"), "keep user settings");
            File.WriteAllText(Path.Combine(root, "log/user.txt"), "keep logs");
            File.WriteAllText(Path.Combine(root, "mcp-readwrite.json"), "keep custom MCP configuration");
            return root;
        }

        private static Models.UpdatePlan SmallPlan(string root)
        {
            var plan = new Models.UpdatePlan { InstallRoot = root, BeforeVersion = "2026.23.10", TargetVersion = "2026.23.11", BeforeExeSha256 = Native.TreeGitUpdater.Hash(Path.Combine(root, "TreeGit.exe")) };
            var stage = Path.Combine(Native.TreeGitUpdateInstaller.JobRoot(plan), "stage/TreeGit");
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(root, "old.dll"), "old library");
            foreach (var file in new[] { "old.dll", "new.dll", "TreeGit.exe" })
            {
                var source = Path.Combine(stage, file);
                File.WriteAllText(source, "new " + file);
                var target = Path.Combine(root, file);
                plan.Files.Add(new() { Path = file, Sha256 = Native.TreeGitUpdater.Hash(source), BeforeSha256 = File.Exists(target) ? Native.TreeGitUpdater.Hash(target) : null });
            }
            Native.TreeGitUpdateInstaller.SavePlan(plan);
            return plan;
        }

        private static async Task Reject(string name, Func<Task> action, string code = "invalid")
        {
            try { await action(); throw new Exception("Expected rejection: " + name); }
            catch (Models.UpdateProblem error) { Check(name, error.Code == code); }
        }

        private static async Task CheckMetadataAsync()
        {
            Check("path traversal and user-data writes are not program files", !Native.TreeGitUpdater.AllowedFile("../TreeGit.exe") && !Native.TreeGitUpdater.AllowedFile("data/preference.json") && !Native.TreeGitUpdater.AllowedFile("log/a.txt") && !Native.TreeGitUpdater.AllowedFile("git/../../data/a") && !Native.TreeGitUpdater.AllowedFile("data\\private.dll") && !Native.TreeGitUpdater.AllowedFile("git/../data/a.dll"));
            Check("version tags are exact and do not accept paths", Native.TreeGitUpdater.TryVersion("treegit-v2026.23.12", out _) && !Native.TreeGitUpdater.TryVersion("treegit-v2026.23.12\n", out _) && !Native.TreeGitUpdater.TryVersion("treegit-v../../escape", out _));
            await Reject("release URLs must belong to this repository", () => { Native.TreeGitUpdater.ValidateAssetUrl("https://github.com/other/repo/releases/download/treegit-v2026.23.11/TreeGit-2026.23.11-Windows-x64.zip", "treegit-v2026.23.11", "TreeGit-2026.23.11-Windows-x64.zip"); return Task.CompletedTask; });

            using (var updater = new Native.TreeGitUpdater(NewRoot(), new HttpClient(new MetadataHandler(HttpStatusCode.TooManyRequests, ""))))
                await Reject("rate limiting has a distinct retryable result", () => updater.CheckAsync(CancellationToken.None), "busy-service");
            using (var updater = new Native.TreeGitUpdater(NewRoot(), new HttpClient(new MetadataHandler(HttpStatusCode.OK, new string(' ', 2 * 1024 * 1024 + 1)))))
                await Reject("oversized metadata is bounded while reading", () => updater.CheckAsync(CancellationToken.None));
            using (var updater = new Native.TreeGitUpdater(NewRoot(), new HttpClient(new MetadataHandler(HttpStatusCode.OK, "{\"tag_name\":\"treegit-v2026.23.12\",\"prerelease\":true}"))))
                await Reject("pre-releases cannot replace a stable installation", () => updater.CheckAsync(CancellationToken.None));
        }

        public static async Task<int> Main(string[] args)
        {
            if (args.Length >= 4 && args[0] == "--prepare")
            {
                var root = Path.GetFullPath(args[1]);
                var archive = Path.GetFullPath(args[2]);
                using var zip = ZipFile.OpenRead(archive);
                using var info = JsonDocument.Parse(zip.GetEntry("TreeGit/build-info.json")!.Open());
                var version = info.RootElement.GetProperty("version").GetString();
                var release = new Models.Version { TagName = "treegit-v" + version, Assets = [new() { Name = "TreeGit-" + version + "-Windows-x64.zip", DownloadUrl = "https://github.com/JonSun2020/TreeGit/releases/download/treegit-v" + version + "/TreeGit-" + version + "-Windows-x64.zip", Size = new FileInfo(archive).Length, Digest = "sha256:" + Native.TreeGitUpdater.Hash(archive) }] };
                using var updater = new Native.TreeGitUpdater(root, new HttpClient(new FixtureHandler(archive, release)));
                var plan = await updater.PrepareAsync(release, null, CancellationToken.None);
                File.WriteAllText(args[3], JsonSerializer.Serialize(new { planPath = Native.TreeGitUpdateInstaller.PlanPath(plan), id = plan.Id }));
                return 0;
            }
            if (args.Length == 4 && args[0] == "--launch")
            {
                var plan = Native.TreeGitUpdateInstaller.LoadPlan(args[1]);
                Native.TreeGitUpdateInstaller.Launch(plan, args[2], parentId: int.Parse(args[3]));
                return 0;
            }
            _out = Path.GetFullPath(args[0] == "--metadata-only" ? args[1] : args[0]);
            Directory.CreateDirectory(_out);
            string failure = null;
            try
            {
                if (args[0] == "--metadata-only")
                {
                    await CheckMetadataAsync();
                    return 0;
                }
                var root = NewRoot();
                var plan = SmallPlan(root);
                var settings = Native.TreeGitUpdater.Hash(Path.Combine(root, "data/user.txt"));
                Native.TreeGitUpdateInstaller.Apply(plan);
                Check("program files are replaced", plan.Files.All(file => Native.TreeGitUpdater.Hash(Path.Combine(root, file.Path)) == file.Sha256));
                Check("settings logs and MCP paths are preserved", Native.TreeGitUpdater.Hash(Path.Combine(root, "data/user.txt")) == settings && File.ReadAllText(Path.Combine(root, "mcp-readwrite.json")) == "keep custom MCP configuration" && File.ReadAllText(Path.Combine(root, "log/user.txt")) == "keep logs");
                Native.TreeGitUpdateInstaller.Restore(plan);
                Check("rollback restores every original file and removes only added program files", File.ReadAllText(Path.Combine(root, "TreeGit.exe")) == "old program" && File.ReadAllText(Path.Combine(root, "old.dll")) == "old library" && !File.Exists(Path.Combine(root, "new.dll")));
                Native.TreeGitUpdateInstaller.Restore(plan);
                Check("recovery can be repeated safely", File.ReadAllText(Path.Combine(root, "TreeGit.exe")) == "old program");

                root = NewRoot(); plan = SmallPlan(root);
                Native.TreeGitUpdateInstaller.AfterReplacement = count => { if (count == 2) throw new IOException("Test interruption after two replacements"); };
                try
                {
                    try { Native.TreeGitUpdateInstaller.Apply(plan); throw new Exception("Expected interrupted installation"); }
                    catch (IOException) { }
                }
                finally { Native.TreeGitUpdateInstaller.AfterReplacement = null; }
                var recovery = Native.TreeGitUpdateInstaller.LoadPlan(Native.TreeGitUpdateInstaller.PlanPath(plan));
                Check("an interrupted transaction is recorded before replacement", recovery.State == "Applying");
                Native.TreeGitUpdateInstaller.Restore(recovery);
                Check("recovery restores an interrupted partially replaced installation", File.ReadAllText(Path.Combine(root, "TreeGit.exe")) == "old program" && File.ReadAllText(Path.Combine(root, "old.dll")) == "old library" && !File.Exists(Path.Combine(root, "new.dll")));

                root = NewRoot();
                plan = SmallPlan(root);
                using (var held = new FileStream(Path.Combine(root, "old.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { Native.TreeGitUpdateInstaller.Apply(plan); throw new Exception("Locked file was changed"); }
                    catch (Models.UpdateProblem error) { Check("locked files are detected before any replacement", error.Code == "locked" && File.ReadAllText(Path.Combine(root, "TreeGit.exe")) == "old program" && !File.Exists(Path.Combine(root, "new.dll"))); }
                }
                Native.TreeGitUpdateInstaller.Restore(plan);
                Check("a preflight failure leaves the original program usable", File.ReadAllText(Path.Combine(root, "TreeGit.exe")) == "old program");

                root = NewRoot(); plan = SmallPlan(root);
                File.SetAttributes(Path.Combine(root, "old.dll"), FileAttributes.ReadOnly);
                try
                {
                    try { Native.TreeGitUpdateInstaller.Apply(plan); throw new Exception("Read-only file was changed"); }
                    catch (Models.UpdateProblem error) { Check("write permissions are checked before replacing files", error.Code == "permission" && File.ReadAllText(Path.Combine(root, "TreeGit.exe")) == "old program"); }
                }
                finally { File.SetAttributes(Path.Combine(root, "old.dll"), FileAttributes.Normal); }
                Native.TreeGitUpdateInstaller.Restore(plan);

                root = NewRoot(); plan = SmallPlan(root);
                File.WriteAllText(Path.Combine(root, "TreeGit.exe"), "changed elsewhere");
                try { Native.TreeGitUpdateInstaller.Apply(plan); throw new Exception("Changed installation was overwritten"); }
                catch (Models.UpdateProblem error) { Check("a changed installation is preserved", error.Code == "changed"); }
                Native.TreeGitUpdateInstaller.Restore(plan);
                Check("preflight rollback does not overwrite external changes", File.ReadAllText(Path.Combine(root, "TreeGit.exe")) == "changed elsewhere");

                await CheckMetadataAsync();

                foreach (var (entryName, symlink) in new[] { ("TreeGit/../../escape.dll", false), ("TreeGit/data/private.dll", false), ("TreeGit/link.dll", true) })
                {
                    var badArchive = Path.Combine(_out, Guid.NewGuid().ToString("N") + ".zip");
                    using (var zip = ZipFile.Open(badArchive, ZipArchiveMode.Create))
                    {
                        var entry = zip.CreateEntry(entryName);
                        if (symlink) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                        using var writer = new StreamWriter(entry.Open()); writer.Write("untrusted entry");
                    }
                    var badRelease = FixtureRelease(badArchive, "2026.23.12");
                    root = NewRoot();
                    using var updater = new Native.TreeGitUpdater(root, new HttpClient(new FixtureHandler(badArchive, badRelease)));
                    await Reject("unsafe archive rejected: " + entryName, () => updater.PrepareAsync(badRelease, null, CancellationToken.None));
                    Check("unsafe archive cannot change the installed program or personal data", File.ReadAllText(Path.Combine(root, "TreeGit.exe")) == "old program" && !File.Exists(Path.Combine(root, "data/private.dll")));
                }

                if (args.Length > 1)
                {
                    var archive = Path.GetFullPath(args[1]);
                    string fixtureVersion;
                    using (var zip = ZipFile.OpenRead(archive))
                    using (var info = JsonDocument.Parse(zip.GetEntry("TreeGit/build-info.json")!.Open())) fixtureVersion = info.RootElement.GetProperty("version").GetString();
                    var hash = Native.TreeGitUpdater.Hash(archive);
                    var release = new Models.Version { TagName = "treegit-v" + fixtureVersion, Assets = [new() { Name = "TreeGit-" + fixtureVersion + "-Windows-x64.zip", DownloadUrl = "https://github.com/JonSun2020/TreeGit/releases/download/treegit-v" + fixtureVersion + "/TreeGit-" + fixtureVersion + "-Windows-x64.zip", Size = new FileInfo(archive).Length, Digest = "sha256:" + hash }] };
                    root = NewRoot();
                    using (var updater = new Native.TreeGitUpdater(root, new HttpClient(new FixtureHandler(archive, release))))
                    {
                        var metadata = await updater.CheckAsync(CancellationToken.None);
                        Check("GitHub release metadata and asset digests are understood", metadata.TagName == release.TagName && metadata.Assets[0].Digest == release.Assets[0].Digest);
                        var prepared = await updater.PrepareAsync(release, null, CancellationToken.None);
                        Check("the actual release ZIP is validated and staged", prepared.TargetVersion == fixtureVersion && prepared.Files.Count > 300);
                        Check("staging never includes personal settings or custom MCP configuration", !prepared.Files.Any(file => file.Path.StartsWith("data/") || file.Path.StartsWith("log/") || file.Path == "mcp-readwrite.json"));
                        Check("download and staging do not change the installed program", File.ReadAllText(Path.Combine(root, "TreeGit.exe")) == "old program");
                    }

                    root = NewRoot();
                    var downloads = Path.Combine(root, ".treegit-update/downloads"); Directory.CreateDirectory(downloads);
                    var partial = Path.Combine(downloads, fixtureVersion + "-" + hash[..12] + ".zip.partial");
                    using (var input = File.OpenRead(archive)) using (var output = File.Create(partial)) { var bytes = new byte[1024 * 1024]; input.ReadExactly(bytes); output.Write(bytes); }
                    var resume = new FixtureHandler(archive, release);
                    using (var updater = new Native.TreeGitUpdater(root, new HttpClient(resume))) await updater.PrepareAsync(release, null, CancellationToken.None);
                    Check("an interrupted download resumes from its saved offset", resume.RequestedOffset == 1024 * 1024);

                    root = NewRoot();
                    downloads = Path.Combine(root, ".treegit-update/downloads"); Directory.CreateDirectory(downloads);
                    partial = Path.Combine(downloads, fixtureVersion + "-" + hash[..12] + ".zip.partial");
                    File.WriteAllBytes(partial, new byte[4096]);
                    var noRange = new FixtureHandler(archive, release) { IgnoreRange = true };
                    using (var updater = new Native.TreeGitUpdater(root, new HttpClient(noRange))) await updater.PrepareAsync(release, null, CancellationToken.None);
                    Check("a server ignoring Range safely restarts the download", noRange.RequestedOffset == 4096 && File.ReadAllText(Path.Combine(root, "TreeGit.exe")) == "old program");

                    var damaged = FixtureRelease(archive, fixtureVersion);
                    damaged.Assets[0].Digest = "sha256:" + new string('0', 64);
                    root = NewRoot();
                    using (var updater = new Native.TreeGitUpdater(root, new HttpClient(new FixtureHandler(archive, damaged))))
                        await Reject("a mismatched package hash prevents installation", () => updater.PrepareAsync(damaged, null, CancellationToken.None), "damaged");
                    Check("damaged downloads are removed without touching the installed program", !Directory.EnumerateFiles(Path.Combine(root, ".treegit-update/downloads"), "*.partial").Any() && File.ReadAllText(Path.Combine(root, "TreeGit.exe")) == "old program");
                }
                Console.WriteLine("Updater checks passed: " + Checks.Count);
                return 0;
            }
            catch (Exception error) { failure = error.ToString(); Console.Error.WriteLine(failure); return 1; }
            finally { File.WriteAllText(Path.Combine(_out, "results.json"), JsonSerializer.Serialize(new { passed = Checks.Count, checks = Checks, failure })); }
        }

        private static Models.Version FixtureRelease(string archive, string version) => new()
        {
            TagName = "treegit-v" + version,
            Assets = [new() { Name = "TreeGit-" + version + "-Windows-x64.zip", DownloadUrl = "https://github.com/JonSun2020/TreeGit/releases/download/treegit-v" + version + "/TreeGit-" + version + "-Windows-x64.zip", Size = new FileInfo(archive).Length, Digest = "sha256:" + Native.TreeGitUpdater.Hash(archive) }],
        };

        private sealed class MetadataHandler(HttpStatusCode status, string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
                Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        private sealed class FixtureHandler(string archive, Models.Version release) : HttpMessageHandler
        {
            public long? RequestedOffset { get; private set; }
            public bool IgnoreRange { get; init; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                if (request.RequestUri!.AbsoluteUri == Native.TreeGitUpdater.LatestUrl)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(release), Encoding.UTF8, "application/json") });
                var stream = File.OpenRead(archive);
                var offset = request.Headers.Range?.Ranges.Single().From;
                RequestedOffset = offset;
                if (IgnoreRange) offset = null;
                if (offset.HasValue) stream.Position = offset.Value;
                var content = new StreamContent(stream);
                content.Headers.ContentLength = stream.Length - stream.Position;
                if (offset.HasValue) content.Headers.ContentRange = new ContentRangeHeaderValue(offset.Value, stream.Length - 1, stream.Length);
                return Task.FromResult(new HttpResponseMessage(offset.HasValue ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content });
            }
        }
    }
}
