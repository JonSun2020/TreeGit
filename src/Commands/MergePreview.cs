using System.Diagnostics;
using System;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;

namespace SourceGit.Commands
{
    public abstract class MergePreview : Command
    {
        public async Task<int> GetExitCodeAsync()
        {
            if (CancellationToken.IsCancellationRequested) return -1;
            using var proc = new Process();
            var exitCode = -1;
            DirectoryInfo scratch = null;
            var started = false;
            try
            {
                var config = new Config(WorkingDirectory) { RaiseError = false, CancellationToken = CancellationToken };
                var settings = await config.ReadAllAsync().ConfigureAwait(false);
                if (!config.ReadSucceeded || CancellationToken.IsCancellationRequested) return -1;
                // Preview must not execute merge drivers or normalization
                // filters, or fetch missing objects into the real repository.
                // The actual merge continues to use the user's configuration.
                foreach (var key in settings.Keys)
                    if ((key.StartsWith("merge.", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".driver", StringComparison.OrdinalIgnoreCase)) ||
                        key.Equals("extensions.partialclone", StringComparison.OrdinalIgnoreCase) ||
                        (key.StartsWith("remote.", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".promisor", StringComparison.OrdinalIgnoreCase)))
                        return -1;
                var renormalize = await new PreviewRead { WorkingDirectory = WorkingDirectory, RaiseError = false, CancellationToken = CancellationToken,
                    Args = "config --bool --get merge.renormalize" }.GetResultAsync().ConfigureAwait(false);
                if (renormalize.ExitCode is not (0 or 1) || (renormalize.IsSuccess && renormalize.StdOut.Trim() != "false")) return -1;
                var objectPath = await new PreviewRead { WorkingDirectory = WorkingDirectory, RaiseError = false, CancellationToken = CancellationToken,
                    Args = "rev-parse --path-format=absolute --git-path objects" }.GetResultAsync().ConfigureAwait(false);
                if (!objectPath.IsSuccess || CancellationToken.IsCancellationRequested) return -1;
                var objects = objectPath.StdOut.TrimEnd('\r', '\n');
                if (!Path.IsPathFullyQualified(objects) || !Directory.Exists(objects)) return -1;
                var plan = await PreparePreviewAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(plan.Arguments) || !IsObjectId(plan.Head) || CancellationToken.IsCancellationRequested) return -1;
                var common = await new QueryGitCommonDir(WorkingDirectory) { CancellationToken = CancellationToken }.GetResultAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(common) || File.Exists(Path.Combine(common, "info/grafts"))) return -1;
                var replacements = await new PreviewRead { WorkingDirectory = WorkingDirectory, RaiseError = false, CancellationToken = CancellationToken,
                    Args = "for-each-ref --format=%(refname) refs/replace" }.GetResultAsync().ConfigureAwait(false);
                if (!replacements.IsSuccess || !string.IsNullOrWhiteSpace(replacements.StdOut)) return -1;
                scratch = Directory.CreateTempSubdirectory("TreeGit-merge-preview-");
                Directory.CreateDirectory(Path.Combine(scratch.FullName, "objects"));
                Directory.CreateDirectory(Path.Combine(scratch.FullName, "refs"));
                File.WriteAllText(Path.Combine(scratch.FullName, "HEAD"), plan.Head + "\n");
                File.WriteAllText(Path.Combine(scratch.FullName, "config"), "[core]\n bare=true\n repositoryformatversion=" +
                    (plan.Head.Length == 64 ? "1\n[extensions]\n objectFormat=sha256\n" : "0\n"));
                File.WriteAllText(Path.Combine(scratch.FullName, "global-config"), string.Empty);
                foreach (var relative in new[] { "info/attributes", "shallow" })
                {
                    var original = Path.Combine(common, relative);
                    if (!File.Exists(original)) continue;
                    var copy = Path.Combine(scratch.FullName, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(original, copy);
                }
                // A separate Git directory also pins configuration. A new
                // driver configured by another client mid-preview cannot run.
                Args = plan.Arguments;
                proc.StartInfo = CreateGitStartInfo(true);
                var environment = proc.StartInfo.Environment;
                foreach (var key in new List<string>(environment.Keys))
                    if (key.Equals("GIT_CONFIG", StringComparison.OrdinalIgnoreCase) || key.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase))
                        environment.Remove(key);
                environment["GIT_DIR"] = environment["GIT_COMMON_DIR"] = scratch.FullName;
                environment["GIT_INDEX_FILE"] = Path.Combine(scratch.FullName, "index");
                environment.Remove("GIT_WORK_TREE");
                environment.Remove("GIT_ATTR_SOURCE");
                environment.Remove("GIT_NAMESPACE");
                environment["GIT_CONFIG_GLOBAL"] = Path.Combine(scratch.FullName, "global-config");
                environment["GIT_CONFIG_NOSYSTEM"] = "1";
                // Replay creates temporary commits, but their identities are
                // irrelevant to conflict prediction and never leave scratch.
                environment["GIT_COMMITTER_NAME"] = "TreeGit preview";
                environment["GIT_COMMITTER_EMAIL"] = "preview@treegit.invalid";
                var count = 0;
                foreach (var (key, value) in settings)
                    if (PreviewSettings.Contains(key))
                    {
                        environment["GIT_CONFIG_KEY_" + count] = key;
                        environment["GIT_CONFIG_VALUE_" + count++] = value;
                    }
                environment["GIT_CONFIG_COUNT"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                proc.StartInfo.Environment.TryGetValue("GIT_ALTERNATE_OBJECT_DIRECTORIES", out var alternates);
                proc.StartInfo.Environment["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = QuoteAlternate(objects) +
                    (string.IsNullOrEmpty(alternates) ? string.Empty : Path.PathSeparator + alternates);
                proc.StartInfo.Environment["GIT_OBJECT_DIRECTORY"] = Path.Combine(scratch.FullName, "objects");
                proc.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
                proc.StartInfo.Environment["GIT_NO_LAZY_FETCH"] = "1";
                Native.ProcessLauncher.Start(proc);
                started = true;
                using var registration = CancellationToken.Register(() =>
                {
                    try { if (!proc.HasExited) Native.OS.TerminateProcess(proc); }
                    catch (InvalidOperationException) { }
                });
                await Task.WhenAll(proc.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken),
                    proc.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken),
                    proc.WaitForExitAsync(CancellationToken)).ConfigureAwait(false);
                if (!CancellationToken.IsCancellationRequested) exitCode = proc.ExitCode;
            }
            catch
            {
                // Ignore any exceptions and just return -1
            }
            finally
            {
                if (started)
                    try
                    {
                        if (!proc.HasExited) Native.OS.TerminateProcess(proc);
                        await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                    catch { exitCode = -1; }
                if (scratch != null)
                    for (var attempt = 0; attempt < 3; attempt++)
                        try
                        {
                            if ((scratch.Attributes & FileAttributes.ReparsePoint) != 0) scratch.Delete(false);
                            else
                            {
                                // Loose Git objects are read-only on Windows.
                                // These files were created only in this fresh
                                // quarantine; never change alternate objects.
                                foreach (var file in scratch.EnumerateFiles("*", new EnumerationOptions
                                    { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
                                    if ((file.Attributes & FileAttributes.ReadOnly) != 0) file.Attributes &= ~FileAttributes.ReadOnly;
                                scratch.Delete(true);
                            }
                            break;
                        }
                        catch (DirectoryNotFoundException) { break; }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        {
                            if (attempt < 2) await Task.Delay(50 * (attempt + 1)).ConfigureAwait(false);
                            else
                            {
                                try { Native.OS.LogException(error); }
                                catch (IOException) { }
                                catch (UnauthorizedAccessException) { }
                                exitCode = -1;
                            }
                        }
            }

            return exitCode;
        }

        private sealed class PreviewRead : Command
        {
            internal Task<Result> GetResultAsync() => ReadToEndAsync();
        }

        protected abstract Task<(string Arguments, string Head)> PreparePreviewAsync();

        protected async Task<string> ResolveCommitAsync(string revision)
        {
            var query = await new PreviewRead { WorkingDirectory = WorkingDirectory, RaiseError = false, CancellationToken = CancellationToken,
                Args = $"rev-parse --verify --end-of-options {(revision + "^{commit}").Quoted()}" }.GetResultAsync().ConfigureAwait(false);
            return query.IsSuccess ? query.StdOut.Trim() : string.Empty;
        }

        protected static bool IsObjectId(string value)
        {
            if (value == null || value.Length is not (40 or 64)) return false;
            foreach (var c in value) if (!char.IsAsciiHexDigit(c)) return false;
            return true;
        }

        private static readonly HashSet<string> PreviewSettings = new(StringComparer.OrdinalIgnoreCase)
        {
            "merge.renames", "merge.renamelimit", "merge.directoryrenames", "merge.conflictstyle", "merge.default",
            "diff.algorithm", "diff.renames", "diff.renamelimit", "core.bigfilethreshold", "core.attributesfile",
            "core.ignorecase", "core.protecthfs", "core.protectntfs", "core.symlinks", "core.filemode", "core.eol", "core.autocrlf", "core.longpaths",
        };

        private static string QuoteAlternate(string path)
        {
            var quoted = new StringBuilder("\"");
            foreach (var c in path)
                if (c is '\\' or '"') quoted.Append('\\').Append(c);
                else if (c < 32 || c == 127) quoted.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                else quoted.Append(c);
            return quoted.Append('"').ToString();
        }
    }
}
