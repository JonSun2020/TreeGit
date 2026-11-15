using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace SourceGit.Native
{
    internal sealed class ExternalMergePlan
    {
        public string Executable { get; set; }
        public string Arguments { get; set; }
        public string GitExecutable { get; set; }
        public string UnsavedMessage { get; set; }
    }

    internal static class ExternalMergeSession
    {
        public static string ShellQuote(string value)
            => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        public static int Run(string planFile, string based, string local, string remote, string merged)
        {
            ExternalMergePlan plan = null;
            try
            {
                if (!File.Exists(planFile) || new FileInfo(planFile).Length > 1024 * 1024) return 1;
                plan = JsonSerializer.Deserialize(File.ReadAllText(planFile), JsonCodeGen.Default.ExternalMergePlan);
                if (plan == null || !File.Exists(plan.Executable) || !File.Exists(merged)) return 1;
                based = string.IsNullOrEmpty(based) ? based : Path.GetFullPath(based);
                local = Path.GetFullPath(local);
                remote = Path.GetFullPath(remote);
                merged = Path.GetFullPath(merged);
                // Some installed Git versions re-read a leading '-' filename
                // without '--' inside git-mergetool. Recover the real stages
                // with checkout-index's safe argument form before opening a
                // tool; otherwise it can receive three empty input files.
                var relative = Path.GetRelativePath(Environment.CurrentDirectory, merged);
                if (relative.StartsWith('-') && !PrepareDashPathInputs(plan.GitExecutable, merged, based, local, remote))
                    return 1;
                var before = Hash(merged);
                var index = ReadGit(plan.GitExecutable, ["--literal-pathspecs", "ls-files", "--unmerged", "--stage", "-z", "--", merged]);
                var head = ReadGit(plan.GitExecutable, ["rev-parse", "--verify", "HEAD"]);
                if (string.IsNullOrEmpty(index) || string.IsNullOrWhiteSpace(head)) return 1;
                var shell = FindShell(plan.GitExecutable);
                if (shell == null) return 1;
                var executable = OperatingSystem.IsWindows() ? plan.Executable.Replace('\\', '/') : plan.Executable;
                var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(ShellQuote(executable) + " " + plan.Arguments);
                start.Environment["BASE"] = based;
                start.Environment["LOCAL"] = local;
                start.Environment["REMOTE"] = remote;
                start.Environment["MERGED"] = merged;
                using var process = ProcessLauncher.Start(start);
                if (process == null) return 1;
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    PreserveFailedDraft(planFile, merged, before, index, head);
                    return 1;
                }
                if (!File.Exists(merged) || CryptographicOperations.FixedTimeEquals(before, Hash(merged)) ||
                    HasConflictMarkers(merged, MarkerSize(plan.GitExecutable, merged)))
                {
                    PreserveFailedDraft(planFile, merged, before, index, head);
                    Console.Error.WriteLine(plan.UnsavedMessage);
                    return 1;
                }
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            {
                Console.Error.WriteLine(plan?.UnsavedMessage ?? "The merge result could not be verified.");
                return 1;
            }
        }

        private static byte[] Hash(string file)
        {
            using var stream = File.OpenRead(file);
            return SHA256.HashData(stream);
        }

        private static void PreserveFailedDraft(string planFile, string merged, byte[] original, string index, string head)
        {
            if (!File.Exists(merged) || CryptographicOperations.FixedTimeEquals(original, Hash(merged))) return;
            var id = Guid.NewGuid().ToString("N");
            var draft = new ExternalMergeDraft
            {
                File = merged, Recovery = Path.Combine(Path.GetDirectoryName(merged)!, ".treegit-merge-draft-" + id),
                OriginalHash = Convert.ToHexString(original), EditedHash = Convert.ToHexString(Hash(merged)), Index = index, Head = head,
            };
            var record = Path.Combine(Path.GetDirectoryName(planFile)!, "draft-" + id + ".json");
            File.WriteAllText(record, JsonSerializer.Serialize(draft, JsonCodeGen.Default.ExternalMergeDraft));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(record, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            // Git restores its backup on a failed mergetool exit. Rename on
            // the same volume so that rollback cannot discard an edited draft
            // and preserving a large file does not require another full copy.
            try { File.Move(merged, draft.Recovery); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A reader can permit reads but prohibit renaming on Windows.
                // Fall back to a copy before Git attempts to restore its backup.
                File.Copy(merged, draft.Recovery);
            }
        }

        internal static (int Restored, List<ExternalMergeDraft> RecoveryFiles) RestoreFailedDrafts(string session, string repository, string git)
        {
            var restored = 0;
            var remaining = new List<ExternalMergeDraft>();
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var record in Directory.EnumerateFiles(session, "draft-*.json"))
            {
                try
                {
                    if (new FileInfo(record).Length > 1024 * 1024) throw new InvalidDataException("Invalid merge recovery record.");
                    var draft = JsonSerializer.Deserialize(File.ReadAllText(record), JsonCodeGen.Default.ExternalMergeDraft);
                    if (draft == null || !Path.IsPathFullyQualified(draft.File ?? "") || !Path.IsPathFullyQualified(draft.Recovery ?? "") ||
                        !string.Equals(Path.GetDirectoryName(draft.File), Path.GetDirectoryName(draft.Recovery), comparison) ||
                        !Path.GetFileName(draft.Recovery).StartsWith(".treegit-merge-draft-", StringComparison.Ordinal))
                        throw new InvalidDataException("Invalid merge recovery path.");
                    if (File.Exists(draft.Recovery))
                    {
                        if (Convert.ToHexString(Hash(draft.Recovery)) != draft.EditedHash)
                            throw new InvalidDataException("The saved merge draft is incomplete or has changed.");
                        var index = ReadGit(git, ["--literal-pathspecs", "ls-files", "--unmerged", "--stage", "-z", "--", draft.File], repository);
                        var head = ReadGit(git, ["rev-parse", "--verify", "HEAD"], repository);
                        if (!string.IsNullOrEmpty(index) && index == draft.Index && head == draft.Head && File.Exists(draft.File) &&
                            (File.GetAttributes(draft.File) & FileAttributes.ReparsePoint) == 0 &&
                            (File.GetAttributes(draft.Recovery) & FileAttributes.ReparsePoint) == 0)
                        {
                            var current = Convert.ToHexString(Hash(draft.File));
                            if (current == draft.OriginalHash)
                            {
                                File.Move(draft.Recovery, draft.File, true);
                                restored++;
                            }
                            else if (current == Convert.ToHexString(Hash(draft.Recovery)))
                            {
                                File.Delete(draft.Recovery);
                                restored++;
                            }
                            else remaining.Add(draft);
                        }
                        else remaining.Add(draft);
                    }
                    // Keep the original-to-draft mapping for manual recovery.
                    if (!File.Exists(draft.Recovery)) File.Delete(record);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    OS.LogException(error);
                    remaining.Add(new ExternalMergeDraft { File = repository, Recovery = session });
                }
            }
            return (restored, remaining);
        }

        private static bool PrepareDashPathInputs(string git, string merged, params string[] inputs)
        {
            var index = ReadGit(git, ["--literal-pathspecs", "ls-files", "--unmerged", "-z", "--", merged]);
            if (index == null || index.Length == 0) return false;
            var present = new bool[4];
            foreach (var entry in index.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var tab = entry.IndexOf('\t');
                if (tab < 0) return false;
                var fields = entry[..tab].Split(' ');
                if (fields.Length != 3 || !int.TryParse(fields[2], out var stage) || stage is < 1 or > 3) return false;
                present[stage] = true;
            }
            for (var stage = 1; stage <= 3; stage++)
            {
                if (!File.Exists(inputs[stage - 1])) return false;
                if (!present[stage])
                {
                    File.WriteAllBytes(inputs[stage - 1], []);
                    continue;
                }
                var output = ReadGit(git, ["checkout-index", "--temp", $"--stage={stage}", "--", merged]);
                var tab = output?.IndexOf('\t') ?? -1;
                if (tab <= 0) return false;
                var name = output[..tab];
                // checkout-index owns this temporary file in its current
                // directory. Never use a response path outside that directory.
                if (!name.StartsWith(".merge_file_", StringComparison.Ordinal) || name != Path.GetFileName(name)) return false;
                var temporary = Path.Combine(Environment.CurrentDirectory, name);
                try { File.Copy(temporary, inputs[stage - 1], true); }
                finally { File.Delete(temporary); }
            }
            return true;
        }

        private static string ReadGit(string git, string[] arguments, string repository = null)
        {
            var start = new ProcessStartInfo(git) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = repository ?? Environment.CurrentDirectory };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = ProcessLauncher.Start(start);
            if (process == null) return null;
            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(); errors.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? output : null;
        }

        private static string FindShell(string git)
        {
            if (!OperatingSystem.IsWindows()) return File.Exists("/bin/sh") ? "/bin/sh" : null;
            var directory = Path.GetDirectoryName(git);
            for (var i = 0; i < 4 && !string.IsNullOrEmpty(directory); i++, directory = Path.GetDirectoryName(directory))
                foreach (var relative in new[] { "usr/bin/sh.exe", "bin/sh.exe", "sh.exe" })
                {
                    var candidate = Path.Combine(directory, relative);
                    if (File.Exists(candidate)) return candidate;
                }
            return null;
        }

        private static int MarkerSize(string git, string file)
        {
            try
            {
                var start = new ProcessStartInfo(git) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "check-attr", "-z", "conflict-marker-size", "--", file }) start.ArgumentList.Add(argument);
                using var process = ProcessLauncher.Start(start);
                if (process == null) return 7;
                var errors = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(); errors.GetAwaiter().GetResult();
                var parts = output.Split('\0');
                return process.ExitCode == 0 && parts.Length >= 3 && int.TryParse(parts[2], out var size) && size > 0 ? Math.Min(size, 1024) : 7;
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return 7; }
        }

        private static bool HasConflictMarkers(string file, int markerSize)
        {
            using var stream = File.OpenRead(file);
            // Scan bytes so UTF-8, legacy code pages and UTF-16/32 with or
            // without a BOM all receive the same check. Keep a small overlap
            // for a marker crossing two read buffers.
            var overlap = (markerSize - 1) * 4 + 5;
            var buffer = new byte[65536 + overlap];
            var kept = 0;
            var bomLength = 0;
            int read;
            while ((read = stream.Read(buffer, kept, buffer.Length - kept)) > 0)
            {
                var count = kept + read;
                var absoluteStart = stream.Position - count;
                if (absoluteStart == 0)
                {
                    if (count >= 4 && (buffer[0] == 0xFF && buffer[1] == 0xFE && buffer[2] == 0 && buffer[3] == 0 ||
                        buffer[0] == 0 && buffer[1] == 0 && buffer[2] == 0xFE && buffer[3] == 0xFF)) bomLength = 4;
                    else if (count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) bomLength = 3;
                    else if (count >= 2 && (buffer[0] == 0xFF && buffer[1] == 0xFE || buffer[0] == 0xFE && buffer[1] == 0xFF)) bomLength = 2;
                }
                var offset = 0;
                while (offset < count)
                {
                    var next = buffer.AsSpan(offset, count - offset).IndexOfAny((byte)'<', (byte)'|', (byte)'>');
                    if (next < 0) break;
                    offset += next;
                    var marker = buffer[offset];
                    for (var stride = 1; stride <= 4; stride *= 2)
                    {
                        var lineStart = offset >= stride && buffer[offset - stride] == '\n';
                        var firstLine = absoluteStart + offset >= bomLength && absoluteStart + offset < bomLength + stride;
                        if (!lineStart && !firstLine) continue;
                        var padding = lineStart ? stride - 1 : (int)(absoluteStart + offset - bomLength);
                        var paddingIsZero = true;
                        for (var i = 1; i <= padding; i++) if (buffer[offset - i] != 0) { paddingIsZero = false; break; }
                        if (!paddingIsZero) continue;
                        var length = (markerSize - 1) * stride + 1;
                        if (offset + length > count) continue;
                        var matches = true;
                        for (var i = 0; i < length; i++)
                            if (buffer[offset + i] != (i % stride == 0 ? marker : (byte)0)) { matches = false; break; }
                        // A remaining opening, base or closing marker is
                        // unresolved even if its other marker lines were edited.
                        if (matches) return true;
                    }
                    offset++;
                }
                kept = Math.Min(overlap, count);
                buffer.AsSpan(count - kept, kept).CopyTo(buffer);
            }
            return false;
        }
    }

    internal sealed class ExternalMergeDraft
    {
        public string File { get; set; }
        public string Recovery { get; set; }
        public string OriginalHash { get; set; }
        public string EditedHash { get; set; }
        public string Index { get; set; }
        public string Head { get; set; }
    }
}
