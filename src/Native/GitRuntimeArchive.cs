using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace SourceGit.Native
{
    internal static class GitRuntimeArchive
    {
        internal const string CacheDirectory = ".treegit-runtime";
        private static readonly string[] RequiredFiles = [
            "cmd/git.exe", "ucrt64/bin/git.exe", "usr/bin/sh.exe", "usr/bin/ssh.exe", "usr/bin/ssh-keygen.exe",
            "ucrt64/etc/ssl/certs/ca-bundle.crt", "ucrt64/libexec/git-core/git-flow.exe",
            "ucrt64/libexec/git-core/git-lfs.exe", "etc/gitconfig"
        ];
        private static readonly string[] CompactFiles = [
            "cmd/git.exe", "runtime.zip", "payload-manifest.json", "COMPONENTS.md", "LICENSE.txt",
            "licenses/Git-LFS-LICENSE.md", "licenses/Git-Flow-LICENSE",
        ];

        internal static bool HasExpandedFiles(string app)
        {
            var root = CheckedUpdatePath(app, "git");
            return RequiredFiles.Skip(1).Any(name => File.Exists(CheckedUpdatePath(root, name)));
        }

        internal static string ReadUpdateIdentity(string app) => ReadUpdateRuntime(app).Identity;

        internal static (string Identity, string Version) ReadUpdateRuntime(string app, bool requirePayloadPresence = false)
        {
            var identity = InspectUpdateRuntime(app, null, default, out var version, requirePayloadPresence);
            return (identity, version);
        }

        internal static bool VerifyForUpdate(string app, string identity, CancellationToken token = default)
            => identity != null && InspectUpdateRuntime(app, identity, token, out _) == identity;

        private static string InspectUpdateRuntime(string app, string expected, CancellationToken token, out string runtimeVersion,
            bool requirePayloadPresence = false)
        {
            runtimeVersion = null;
            try
            {
                var root = CheckedUpdatePath(app, "git");
                var payload = CheckedUpdatePath(root, "payload-manifest.json");
                var manifest = CheckedUpdatePath(root, "runtime-manifest.json");
                var compact = File.Exists(payload);
                var files = ReadManifest(compact ? payload : manifest, out var version);
                if (string.IsNullOrWhiteSpace(version) || version.Length > 64 || version.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.') ||
                    !RequiredFiles.All(files.ContainsKey)) return null;

                // Ignore JSON formatting and component enumeration order across builds.
                var entries = files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "\t" + pair.Value);
                var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(version + "\n" + string.Join('\n', entries))));
                if (expected == null)
                {
                    if (requirePayloadPresence)
                    {
                        var filesNeeded = compact ? CompactFiles : RequiredFiles;
                        if (!filesNeeded.All(name => File.Exists(CheckedUpdatePath(root, name))) || !File.Exists(manifest)) return null;
                    }
                    runtimeVersion = version;
                    return identity;
                }
                if (identity != expected) return null;

                if (!compact) return Complete(root, files, verifyConfig: true, token: token) ? identity : null;
                var wrapped = ReadManifest(manifest);
                if (wrapped.Count != CompactFiles.Length || !CompactFiles.All(wrapped.ContainsKey) ||
                    wrapped["cmd/git.exe"] != files["cmd/git.exe"] ||
                    !Complete(root, wrapped, verifyConfig: true, token: token)) return null;

                // A partially expanded layout may still have entries beside its archive.
                // Check them too, while ignoring paths outside the shipped manifest.
                foreach (var (name, hash) in files)
                {
                    token.ThrowIfCancellationRequested();
                    var path = CheckedUpdatePath(root, name);
                    if ((File.Exists(path) || Directory.Exists(path)) && !Matches(path, hash)) return null;
                }

                using var archive = ZipFile.OpenRead(CheckedUpdatePath(root, "runtime.zip"));
                if (archive.Entries.Count != files.Count || archive.Entries.Sum(entry => entry.Length) > 1024L * 1024 * 1024) return null;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (!seen.Add(entry.FullName) || !files.TryGetValue(entry.FullName, out var hash) ||
                        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) return null;
                    using var input = entry.Open();
                    if (Convert.ToHexStringLower(SHA256.HashData(input)) != hash) return null;
                }
                return identity;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
                JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException)
            {
                return null;
            }
        }

        private static string CheckedUpdatePath(string root, string relative)
        {
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(prefix, relative));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid bundled Git path.");
            for (var current = path; current.Length >= prefix.Length; current = Path.GetDirectoryName(current)!)
                if (Path.Exists(current)) RejectLink(current);
            return path;
        }

        internal static string FindOrExtract(string app, string fallbackCache, Func<string, bool> usable = null)
        {
            var root = Path.Combine(app, "git");
            var payload = Path.Combine(root, "payload-manifest.json");
            if (!File.Exists(payload)) return Existing(root, usable);

            var key = Path.GetFullPath(root).ToUpperInvariant();
            using var gate = new Mutex(false, "TreeGit.GitLayout." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
            try { if (!gate.WaitOne(TimeSpan.FromSeconds(90))) throw new IOException("Timed out preparing bundled Git."); }
            catch (AbandonedMutexException) { }
            try
            {
                if (!File.Exists(payload)) return Existing(root, usable);
                var manifest = Path.Combine(root, "runtime-manifest.json");
                var outer = ReadManifest(manifest);
                if (!outer.TryGetValue("runtime.zip", out var archiveHash) ||
                    !outer.TryGetValue("payload-manifest.json", out var payloadHash) || !Matches(payload, payloadHash))
                    throw new InvalidDataException("Incomplete bundled Git manifest.");
                var files = ReadManifest(payload);
                if (!RequiredFiles.All(files.ContainsKey)) throw new InvalidDataException("Incomplete bundled Git.");
                var archive = Path.Combine(root, "runtime.zip");
                try
                {
                    RejectLink(root);
                    if (!Complete(root, files))
                    {
                        if (!Matches(archive, archiveHash)) throw new InvalidDataException("Invalid bundled Git archive.");
                        Unpack(archive, root, files, reuse: true);
                    }
                    var path = Existing(root, usable);
                    if (path == null) return null;
                    // A live updater needs the original package files until it has committed or rolled back.
                    if (!File.Exists(Path.Combine(app, ".treegit-update", "active.json")))
                    {
                        try
                        {
                            File.Delete(archive);
                            File.Move(payload, manifest, true);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // A locked archive can be released on the next launch. The verified Git is already usable.
                        }
                    }
                    return path;
                }
                catch (UnauthorizedAccessException) when (!string.IsNullOrEmpty(fallbackCache))
                {
                    // On read-only media the portable archive belongs to all users. Only the user cache is writable.
                    var contents = string.Join('\n', files.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => pair.Key + "\t" + pair.Value));
                    var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(contents)));
                    var path = Extract(archive, archiveHash, id, fallbackCache, files);
                    return usable == null || usable(path) ? path : null;
                }
            }
            finally { gate.ReleaseMutex(); }
        }

        private static string Existing(string root, Func<string, bool> usable)
        {
            var path = Launcher(root);
            return RequiredFiles.All(file => File.Exists(Path.Combine(root, file))) && (usable == null || usable(path)) ? path : null;
        }

        private static string Extract(string archive, string archiveHash, string id, string cache, Dictionary<string, string> files)
        {
            Directory.CreateDirectory(cache);
            RejectLink(cache);
            using var mutex = new Mutex(false, "TreeGit.GitRuntime." + id);
            try
            {
                try { if (!mutex.WaitOne(TimeSpan.FromSeconds(90))) throw new IOException("Timed out preparing bundled Git."); }
                catch (AbandonedMutexException) { }
                try
                {
                    var target = Path.Combine(cache, id);
                    // The mutex also covers recovery from a process killed during extraction.
                    var stage = Path.Combine(cache, ".extract-" + id);
                    var previous = Path.Combine(cache, ".previous-" + id);
                    if (!Directory.Exists(target) && Directory.Exists(previous))
                    {
                        RejectLink(previous);
                        if (!File.Exists(Path.Combine(previous, ".ready"))) throw new IOException("Invalid prior Git cache.");
                        Directory.Move(previous, target);
                    }
                    if (Directory.Exists(stage))
                    {
                        RejectLink(stage);
                        var owner = Path.Combine(stage, ".extract-owner");
                        if (File.Exists(owner) && File.ReadAllText(owner) == id) Directory.Delete(stage, true);
                        else Directory.Delete(stage, false);
                    }
                    if (Ready(target, files)) return Launcher(target);
                    if (!Matches(archive, archiveHash)) throw new InvalidDataException("Invalid bundled Git archive.");
                    Directory.CreateDirectory(stage);
                    File.WriteAllText(Path.Combine(stage, ".extract-owner"), id);
                    try
                    {
                        Unpack(archive, stage, files, reuse: false);
                        // Only complete, verified runtimes are visible to other processes.
                        if (Directory.Exists(target))
                        {
                            RejectLink(target);
                            // A damaged cache may still have active processes. Never alter those files in place.
                            if (!File.Exists(Path.Combine(target, ".ready"))) throw new IOException("Bundled Git cache is incomplete.");
                            foreach (var name in files.Keys.Where(IsConfig))
                            {
                                var oldConfig = Path.Combine(target, name);
                                if (!File.Exists(oldConfig)) continue;
                                RejectLink(Path.GetDirectoryName(oldConfig)!);
                                RejectLink(oldConfig);
                                File.Copy(oldConfig, Path.Combine(stage, name), true);
                            }
                        }
                        WriteReceipt(stage, id, files);
                        if (Directory.Exists(target)) Directory.Move(target, previous);
                        try { Directory.Move(stage, target); }
                        catch
                        {
                            if (Directory.Exists(previous)) Directory.Move(previous, target);
                            throw;
                        }
                        if (Directory.Exists(previous)) RemoveTemporary(previous);
                        return Launcher(target);
                    }
                    finally { if (Directory.Exists(stage)) RemoveTemporary(stage); }
                }
                finally { mutex.ReleaseMutex(); }
            }
            catch (OverflowException e) { throw new InvalidDataException("Invalid bundled Git size.", e); }
        }

        private static void Unpack(string archive, string root, Dictionary<string, string> files, bool reuse)
        {
            using var zip = ZipFile.OpenRead(archive);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (zip.Entries.Count != files.Count || zip.Entries.Sum(entry => entry.Length) > 512L * 1024 * 1024)
                throw new InvalidDataException("Invalid bundled Git archive.");
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                if (!files.TryGetValue(name, out var expected) || !seen.Add(name) ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("Invalid bundled Git entry.");
                var file = Path.Combine(root, name);
                var directory = Path.GetDirectoryName(file)!;
                for (var parent = directory; parent != null; parent = Path.GetDirectoryName(parent))
                {
                    if (Path.Exists(parent)) RejectLink(parent);
                    if (parent.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
                }
                if (File.Exists(file))
                {
                    RejectLink(file);
                    if (reuse && (IsConfig(name) || Matches(file, expected))) continue;
                }
                Directory.CreateDirectory(directory);
                // The layout mutex owns this scratch path. A retry reclaims a partially written component.
                var temp = Path.Combine(root, ".treegit-unpack.partial");
                if (File.Exists(temp))
                {
                    RejectLink(temp);
                    File.Delete(temp);
                }
                try
                {
                    using (var input = entry.Open())
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var hash = SHA256.Create())
                    using (var checkedOutput = new CryptoStream(output, hash, CryptoStreamMode.Write))
                    {
                        input.CopyTo(checkedOutput);
                        checkedOutput.FlushFinalBlock();
                        if (output.Length != entry.Length || !Convert.ToHexString(hash.Hash!).Equals(expected, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Invalid bundled Git component.");
                    }
                    File.Move(temp, file, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }

        private static bool Ready(string root, Dictionary<string, string> files)
        {
            if (!Directory.Exists(root)) return false;
            RejectLink(Path.GetDirectoryName(root)!);
            RejectLink(root);
            var marker = Path.Combine(root, ".ready");
            if (!File.Exists(marker) || new FileInfo(marker).Length > 2 * 1024 * 1024) return false;
            RejectLink(marker);
            var receipt = File.ReadAllLines(marker);
            return receipt.Length > 0 && receipt[0] == Path.GetFileName(root) && Complete(root, files, receipt);
        }

        private static void WriteReceipt(string root, string id, Dictionary<string, string> files)
        {
            using var receipt = File.CreateText(Path.Combine(root, ".ready"));
            receipt.WriteLine(id);
            foreach (var name in files.Keys.Order(StringComparer.Ordinal))
                receipt.WriteLine(FileStamp(new FileInfo(Path.Combine(root, name))));
        }

        private static string FileStamp(FileInfo file) => FormattableString.Invariant($"{file.Length}/{file.LastWriteTimeUtc.Ticks}");

        private static bool Complete(string root, Dictionary<string, string> files, string[] receipt = null,
            bool verifyConfig = false, CancellationToken token = default)
        {
            var checkedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var name in files.Keys.Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                index++;
                var path = Path.Combine(root, name);
                var info = new FileInfo(path);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                for (var parent = Path.GetDirectoryName(path); parent != null && checkedDirectories.Add(parent); parent = Path.GetDirectoryName(parent))
                {
                    RejectLink(parent);
                    if (parent.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
                }
                if (!verifyConfig && IsConfig(name)) continue;
                if (receipt?.Length == files.Count + 1 && receipt[index] == FileStamp(info)) continue;
                if (!Matches(path, files[name])) return false;
            }
            return true;
        }

        private static Dictionary<string, string> ReadManifest(string path) => ReadManifest(path, out _);

        private static Dictionary<string, string> ReadManifest(string path, out string gitVersion)
        {
            if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Invalid bundled Git manifest size.");
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            gitVersion = json.RootElement.TryGetProperty("gitVersion", out var version) ? version.GetString() : null;
            var entries = json.RootElement.GetProperty("files");
            if (entries.GetArrayLength() is < 1 or > 5000) throw new InvalidDataException("Invalid bundled Git manifest size.");
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in entries.EnumerateArray())
            {
                var name = item.GetProperty("path").GetString();
                var hash = item.GetProperty("sha256").GetString();
                if (string.IsNullOrEmpty(name) || name.Equals(".treegit-unpack.partial", StringComparison.OrdinalIgnoreCase) || name.Contains('\\') || name.Contains(':') ||
                    name.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) ||
                    hash?.Length != 64 || !hash.All(Uri.IsHexDigit) || !files.TryAdd(name, hash.ToLowerInvariant()))
                    throw new InvalidDataException("Invalid bundled Git manifest entry.");
            }
            return files;
        }

        private static string Launcher(string root) => Path.Combine(root, "cmd", "git.exe");

        private static bool IsConfig(string name) => name is "etc/gitconfig" or "ucrt64/etc/gitconfig" or "mingw64/etc/gitconfig";

        private static bool Matches(string path, string expected)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        private static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked Git cache path.");
        }

        private static void RemoveTemporary(string path)
        {
            try { Directory.Delete(path, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
