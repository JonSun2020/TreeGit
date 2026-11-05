using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Native
{
    internal sealed class TreeGitUpdater : IDisposable
    {
        internal const string LatestUrl = "https://api.github.com/repos/JonSun2020/TreeGit/releases/latest";
        internal const string DownloadPage = "https://github.com/JonSun2020/TreeGit/releases/latest";
        private readonly HttpClient _client;
        private readonly string _root;

        public TreeGitUpdater(string root, HttpClient client = null)
        {
            _root = Path.GetFullPath(root);
            _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            _client.Timeout = Timeout.InfiniteTimeSpan;
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("TreeGit/" + Models.TreeGitRelease.Version);
            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }

        public async Task<Models.Version> CheckAsync(CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await GetAsync(new Uri(LatestUrl), null, timeout.Token).ConfigureAwait(false);
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var metadata = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (metadata.Length + count > 2 * 1024 * 1024) throw new Models.UpdateProblem("invalid");
                metadata.Write(buffer, 0, count);
            }
            var release = JsonSerializer.Deserialize(metadata.GetBuffer().AsSpan(0, (int)metadata.Length), JsonCodeGen.Default.Version);
            if (release == null || release.Draft || release.Prerelease || !TryVersion(release.TagName, out _))
                throw new Models.UpdateProblem("invalid");
            return release;
        }

        public async Task<Models.UpdatePlan> PrepareAsync(Models.Version release, IProgress<Models.UpdateProgress> progress, CancellationToken token)
        {
            if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
                throw new Models.UpdateProblem("platform");
            if (!TryVersion(release.TagName, out var version)) throw new Models.UpdateProblem("invalid");
            var before = ReadInstalledVersion(_root);
            if (new System.Version(version) <= new System.Version(before)) throw new Models.UpdateProblem("not-newer");
            var name = "TreeGit-" + version + "-Windows-x64.zip";
            var asset = release.Assets.SingleOrDefault(a => a.Name == name);
            if (asset == null || asset.Size <= 0 || asset.Size > 512L * 1024 * 1024) throw new Models.UpdateProblem("invalid");
            var uri = ValidateAssetUrl(asset.DownloadUrl, release.TagName, name);
            var expected = asset.Digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? asset.Digest[7..].ToLowerInvariant() : null;
            if (!IsHash(expected)) throw new Models.UpdateProblem("invalid");
            EnsureWritable(_root);
            var downloads = SafeChild(_root, ".treegit-update/downloads");
            CreateSafeDirectory(_root, downloads);
            var archive = SafeChild(downloads, version + "-" + expected[..12] + ".zip");
            if (!File.Exists(archive) || new FileInfo(archive).Length != asset.Size || Hash(archive) != expected)
                await DownloadAsync(uri, archive, asset.Size, expected, progress, token).ConfigureAwait(false);
            progress?.Report(new("verify"));
            token.ThrowIfCancellationRequested();
            var plan = new Models.UpdatePlan { InstallRoot = _root, BeforeVersion = before, TargetVersion = version, BeforeExeSha256 = Hash(Path.Combine(_root, "TreeGit.exe")), PackageSha256 = expected };
            var job = SafeChild(_root, ".treegit-update/jobs/" + plan.Id);
            CreateSafeDirectory(_root, job);
            TreeGitUpdateInstaller.WriteOwner(plan);
            var prepared = false;
            try
            {
                var stage = Path.Combine(job, "stage");
                Directory.CreateDirectory(stage);
                using (var zip = ZipFile.OpenRead(archive))
                {
                    if (zip.Entries.Count > 6000) throw new Models.UpdateProblem("invalid");
                    var total = zip.Entries.Sum(entry => entry.Length);
                    if (total > 2L * 1024 * 1024 * 1024) throw new Models.UpdateProblem("invalid");
                    EnsureSpace(_root, total * 2 + 50L * 1024 * 1024);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in zip.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        var path = entry.FullName.Replace('\\', '/');
                        if (!path.StartsWith("TreeGit/", StringComparison.Ordinal) || path.Contains(':') || path.Split('/').Any(part => part is "." or "..") ||
                            ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || !seen.Add(path))
                            throw new Models.UpdateProblem("invalid");
                        if (path.EndsWith('/')) continue;
                        var relative = path[8..];
                        if (!AllowedFile(relative)) throw new Models.UpdateProblem("invalid");
                        var destination = SafeChild(stage, path);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await using (var input = entry.Open())
                        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            await input.CopyToAsync(output, token).ConfigureAwait(false);
                        if (new FileInfo(destination).Length != entry.Length) throw new Models.UpdateProblem("invalid");
                    }
                }
                var payload = Path.Combine(stage, "TreeGit");
                ValidatePayload(payload, version);
                foreach (var source in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(payload, source).Replace('\\', '/');
                    var target = SafeChild(_root, relative);
                    if ((relative is "mcp-readonly.json" or "mcp-readwrite.json") && File.Exists(target)) continue;
                    if (IsCustomGitConfiguration(relative, target)) continue;
                    var hash = Hash(source);
                    var old = File.Exists(target) ? Hash(target) : null;
                    if (hash == old) continue;
                    plan.Files.Add(new() { Path = relative, Sha256 = hash, BeforeSha256 = old });
                }
                plan.Files = plan.Files.OrderBy(file => file.Path.Equals("TreeGit.exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToList();
                TreeGitUpdateInstaller.SavePlan(plan);
                prepared = true;
                return plan;
            }
            finally
            {
                if (!prepared)
                    try { TreeGitUpdateInstaller.DeleteOwnedJob(plan); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private async Task DownloadAsync(Uri uri, string archive, long size, string hash, IProgress<Models.UpdateProgress> progress, CancellationToken token)
        {
            var partial = SafeChild(Path.GetDirectoryName(archive)!, Path.GetFileName(archive) + ".partial");
            if (File.Exists(partial) && new FileInfo(partial).Length > size) File.Delete(partial);
            var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            EnsureSpace(_root, size - offset + 50L * 1024 * 1024);
            if (offset < size)
            {
                using var response = await GetAsync(uri, offset > 0 ? offset : null, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    if (response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange?.Length != size)
                        throw new Models.UpdateProblem("invalid");
                }
                else offset = 0;
                await using var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None);
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var buffer = new byte[128 * 1024];
                var completed = offset;
                var lastReport = Stopwatch.StartNew();
                progress?.Report(new("download", completed, size));
                while (true)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(40));
                    int count;
                    try { count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
                    catch (IOException error) { throw new Models.UpdateProblem("interrupted", error); }
                    if (count == 0) break;
                    completed += count;
                    if (completed > size) throw new Models.UpdateProblem("invalid");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    if (lastReport.ElapsedMilliseconds >= 100)
                    {
                        progress?.Report(new("download", completed, size));
                        lastReport.Restart();
                    }
                }
                await output.FlushAsync(token).ConfigureAwait(false);
                if (completed != size) throw new Models.UpdateProblem("interrupted");
            }
            progress?.Report(new("verify"));
            token.ThrowIfCancellationRequested();
            if (Hash(partial) != hash)
            {
                File.Delete(partial);
                throw new Models.UpdateProblem("damaged");
            }
            File.Move(partial, archive, true);
        }

        internal Models.UpdatePlan FindPrepared(Models.Version release)
        {
            if (!TryVersion(release.TagName, out var version)) return null;
            var asset = release.Assets.FirstOrDefault(asset => asset.Name == "TreeGit-" + version + "-Windows-x64.zip");
            var expected = asset?.Digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? asset.Digest[7..].ToLowerInvariant() : null;
            if (!IsHash(expected)) return null;
            var jobs = SafeChild(_root, ".treegit-update/jobs");
            if (!Directory.Exists(jobs)) return null;
            var current = Hash(Path.Combine(_root, "TreeGit.exe"));
            foreach (var folder in Directory.EnumerateDirectories(jobs).OrderByDescending(Directory.GetCreationTimeUtc).Take(20))
            {
                try
                {
                    var plan = TreeGitUpdateInstaller.LoadPlan(SafeChild(folder, "plan.json"));
                    if (plan.State != "Prepared" || plan.TargetVersion != version || plan.BeforeExeSha256 != current || plan.PackageSha256 != expected) continue;
                    var exe = SafeChild(TreeGitUpdateInstaller.JobRoot(plan), "stage/TreeGit/TreeGit.exe");
                    if (File.Exists(exe) && Hash(exe) == plan.Files.Single(file => file.Path == "TreeGit.exe").Sha256) return plan;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or Models.UpdateProblem or InvalidOperationException) { }
            }
            return null;
        }

        private async Task<HttpResponseMessage> GetAsync(Uri uri, long? offset, CancellationToken token)
        {
            for (var redirect = 0; redirect < 6; redirect++)
            {
                if (uri.Scheme != "https" || !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                    throw new Models.UpdateProblem("invalid");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (offset.HasValue) request.Headers.Range = new RangeHeaderValue(offset, null);
                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                headerTimeout.CancelAfter(TimeSpan.FromSeconds(40));
                var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    response.Dispose();
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var code = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests ? "busy-service" : "network";
                    response.Dispose();
                    throw new Models.UpdateProblem(code);
                }
                return response;
            }
            throw new Models.UpdateProblem("network");
        }

        internal static void ValidatePayload(string root, string version)
        {
            if (ReadInstalledVersion(root) != version) throw new Models.UpdateProblem("invalid");
            foreach (var file in new[] { "av_libglesv2.dll", "libHarfBuzzSharp.dll", "libonigwrap.dll", "libSkiaSharp.dll", "git/cmd/git.exe" })
                if (!File.Exists(Path.Combine(root, file))) throw new Models.UpdateProblem("invalid");
            var exe = FileVersionInfo.GetVersionInfo(Path.Combine(root, "TreeGit.exe"));
            var expected = new System.Version(version);
            if (exe.FileMajorPart != expected.Major || exe.FileMinorPart != expected.Minor || exe.FileBuildPart != expected.Build)
                throw new Models.UpdateProblem("invalid");
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "git/runtime-manifest.json")));
            var entries = document.RootElement.GetProperty("files");
            if (entries.GetArrayLength() is < 1 or > 5000) throw new Models.UpdateProblem("invalid");
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "runtime-manifest.json" };
            foreach (var entry in entries.EnumerateArray())
            {
                var relative = entry.GetProperty("path").GetString()!;
                if (!files.Add(relative.Replace('\\', '/'))) throw new Models.UpdateProblem("invalid");
                var path = SafeChild(Path.Combine(root, "git"), relative);
                if (!File.Exists(path) || Hash(path) != entry.GetProperty("sha256").GetString()?.ToLowerInvariant()) throw new Models.UpdateProblem("invalid");
            }
            if (!files.SetEquals(Directory.EnumerateFiles(Path.Combine(root, "git"), "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(Path.Combine(root, "git"), path).Replace('\\', '/'))))
                throw new Models.UpdateProblem("invalid");
        }

        internal static bool TryVersion(string tag, out string version)
        {
            version = tag?.StartsWith("treegit-v", StringComparison.Ordinal) == true ? tag[9..] : null;
            return version != null && Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+\z") && System.Version.TryParse(version, out _);
        }

        internal static Uri ValidateAssetUrl(string value, string tag, string name)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
                Uri.UnescapeDataString(uri.AbsolutePath) != "/JonSun2020/TreeGit/releases/download/" + tag + "/" + name || !string.IsNullOrEmpty(uri.UserInfo))
                throw new Models.UpdateProblem("invalid");
            return uri;
        }

        internal static string ReadInstalledVersion(string root)
        {
            var file = Path.Combine(root, "build-info.json");
            if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024) throw new Models.UpdateProblem("invalid");
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var info = document.RootElement;
            if (info.GetProperty("product").GetString() != "TreeGit" || info.GetProperty("integrationTests").GetBoolean() || !File.Exists(Path.Combine(root, "TreeGit.exe")))
                throw new Models.UpdateProblem("invalid");
            var version = info.GetProperty("version").GetString();
            if (!TryVersion("treegit-v" + version, out _)) throw new Models.UpdateProblem("invalid");
            return version;
        }

        internal static bool AllowedFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') ||
                path.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' '))) return false;
            if (path.StartsWith("git/", StringComparison.Ordinal)) return true;
            if (path.Contains('/')) return false;
            return path is "TreeGit.exe" or "LICENSE" or "THIRD-PARTY-LICENSES.md" or "使用说明.md" or "MCP-使用说明.md" or "mcp-readonly.json" or "mcp-readwrite.json" or "build-info.json" || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        }

        internal static string SafeChild(string root, string relative)
        {
            var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new Models.UpdateProblem("invalid");
            for (var current = path; current.Length >= prefix.Length; current = Path.GetDirectoryName(current)!)
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new Models.UpdateProblem("invalid");
            return path;
        }

        internal static void CreateSafeDirectory(string root, string path)
        {
            _ = SafeChild(root, Path.GetRelativePath(root, path));
            Directory.CreateDirectory(path);
            var cache = SafeChild(root, ".treegit-update");
            if (Directory.Exists(cache)) File.SetAttributes(cache, File.GetAttributes(cache) | FileAttributes.Hidden);
        }

        internal static string Hash(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private bool IsCustomGitConfiguration(string relative, string target)
        {
            if (relative is not ("git/etc/gitconfig" or "git/ucrt64/etc/gitconfig" or "git/mingw64/etc/gitconfig") || !File.Exists(target)) return false;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "git/runtime-manifest.json")));
                var entry = document.RootElement.GetProperty("files").EnumerateArray().FirstOrDefault(file => file.GetProperty("path").GetString()?.Replace('\\', '/') == relative[4..]);
                return entry.ValueKind == JsonValueKind.Undefined || Hash(target) != entry.GetProperty("sha256").GetString()?.ToLowerInvariant();
            }
            catch { return true; }
        }

        internal static bool IsHash(string hash) => hash?.Length == 64 && hash.All(Uri.IsHexDigit);

        internal static void EnsureWritable(string root)
        {
            var probe = SafeChild(root, ".treegit-write-" + Guid.NewGuid().ToString("N"));
            try { using (File.Create(probe)) { } }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new Models.UpdateProblem("permission", error); }
            finally { if (File.Exists(probe)) File.Delete(probe); }
        }

        internal static void EnsureSpace(string root, long bytes)
        {
            if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < bytes) throw new Models.UpdateProblem("space", requiredBytes: bytes);
        }

        public void Dispose() => _client.Dispose();
    }
}
