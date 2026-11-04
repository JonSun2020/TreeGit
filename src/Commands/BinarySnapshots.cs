using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    internal static class BinarySnapshots
    {
        public static async Task<Models.BinaryComparison> LoadAsync(Models.BinaryDiff diff, CancellationToken token)
        {
            var folder = Path.Combine(Path.GetTempPath(), "TreeGit-binary-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            Models.BinaryComparison comparison = null;
            try
            {
                var oldFile = Path.Combine(folder, "old.bin");
                var newFile = Path.Combine(folder, "new.bin");
                await SaveAsync(diff.Repository, diff.OldRevision, diff.OldFilePath, oldFile, token).ConfigureAwait(false);
                await SaveAsync(diff.Repository, diff.NewRevision, diff.FilePath, newFile, token).ConfigureAwait(false);
                comparison = new Models.BinaryComparison(oldFile, newFile, folder);
                await comparison.ScanAsync(token).ConfigureAwait(false);
                return comparison;
            }
            catch
            {
                if (comparison != null) comparison.Dispose();
                else Directory.Delete(folder, true);
                throw;
            }
        }

        private static async Task SaveAsync(string repo, string revision, string path, string target, CancellationToken token)
        {
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            if (path == "/dev/null") return;
            if (revision == null)
            {
                var full = Path.GetFullPath(Path.Combine(repo, path));
                if (!full.StartsWith(Path.GetFullPath(repo).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new ArgumentException("File path must stay inside the repository.");
                if (!File.Exists(full)) return;
                await using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                return;
            }

            // Resolve only refs here. A missing path is a valid added/deleted side;
            // a broken ref must not silently look like an empty file.
            if (revision.Length > 0)
            {
                using var check = Start(repo, "rev-parse", "--verify", "--end-of-options", revision + "^{tree}");
                var (_, code) = await FinishAsync(check, token).ConfigureAwait(false);
                if (code != 0)
                {
                    using var unborn = Start(repo, "symbolic-ref", "-q", "HEAD");
                    var (_, unbornCode) = await FinishAsync(unborn, token).ConfigureAwait(false);
                    if (revision == "HEAD" && unbornCode == 0)
                    {
                        using var existingHead = Start(repo, "rev-parse", "--verify", "--quiet", "HEAD");
                        var (_, headCode) = await FinishAsync(existingHead, token).ConfigureAwait(false);
                        if (headCode != 0) return;
                    }
                    throw new IOException("Cannot resolve binary comparison revision: " + revision);
                }
            }
            var spec = revision + ":" + path;
            using var exists = Start(repo, "cat-file", "-t", spec);
            var (type, existsCode) = await FinishAsync(exists, token).ConfigureAwait(false);
            if (existsCode != 0)
            {
                // A missing blob is not the same as a path absent from a valid tree/index.
                using var entry = revision.Length == 0 ? Start(repo, "ls-files", "--error-unmatch", "--", path) :
                    Start(repo, "ls-tree", "--full-tree", "-z", revision, "--", path);
                var (entryText, entryCode) = await FinishAsync(entry, token).ConfigureAwait(false);
                if (revision.Length == 0 ? entryCode == 1 : entryCode == 0 && entryText.Length == 0) return;
                throw new IOException("Cannot read binary comparison file: " + spec);
            }
            if (type.Trim() != "blob") throw new IOException("The selected path is not a file.");
            using var process = Start(repo, "cat-file", "blob", spec);
            using var cancel = token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            var error = process.StandardError.ReadToEndAsync(token);
            await process.StandardOutput.BaseStream.CopyToAsync(output, token).ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var message = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException(message);
        }

        private static Process Start(string repo, params string[] args)
        {
            var info = new ProcessStartInfo(Native.OS.GitExecutable) { WorkingDirectory = repo, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--no-optional-locks");
            info.ArgumentList.Add("--literal-pathspecs");
            foreach (var arg in args) info.ArgumentList.Add(arg);
            return Process.Start(info) ?? throw new IOException("Cannot start Git.");
        }

        private static async Task<(string Output, int Code)> FinishAsync(Process process, CancellationToken token)
        {
            using var cancel = token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return (await output.ConfigureAwait(false), process.ExitCode);
        }
    }
}
