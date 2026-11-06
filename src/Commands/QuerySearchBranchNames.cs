using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    // One ref snapshot and one streaming walk, rather than one Git process per row.
    public class QuerySearchBranchNames : Command
    {
        public QuerySearchBranchNames(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
            RaiseError = false;
        }

        public async Task<Dictionary<string, string>> GetResultAsync(List<Models.Commit> commits)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (commits.Count == 0) return names;
            try
            {
                Args = "for-each-ref " + "--format=\"%(refname)%00%(objectname)%00%(upstream)%00%(HEAD)\" refs/heads refs/remotes";
                var result = await ReadToEndAsync().ConfigureAwait(false);
                if (!result.IsSuccess || CancellationToken.IsCancellationRequested) return names;
                var branches = new List<Models.Branch>();
                foreach (var line in result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('\0');
                    if (parts.Length != 4 || parts[0].EndsWith("/HEAD", StringComparison.Ordinal)) continue;
                    var local = parts[0].StartsWith("refs/heads/", StringComparison.Ordinal);
                    var name = parts[0].Substring(local ? 11 : 13);
                    var slash = local ? -1 : name.IndexOf('/');
                    if (!local && slash < 1) continue;
                    branches.Add(new Models.Branch { FullName = parts[0], Head = parts[1], Upstream = parts[2], IsCurrent = parts[3] == "*", IsLocal = local,
                        Name = local ? name : name.Substring(slash + 1), Remote = local ? null : name.Substring(0, slash) });
                }
                string Choose(string sha, HashSet<string> refs) => Models.BranchSelection.Choose(branches, sha, refs)?.FriendlyName ?? "无分支";
                if (commits.Count == 1)
                {
                    var containing = await new QueryBranchesContainingCommit(WorkingDirectory, commits[0].SHA) { CancellationToken = CancellationToken }.GetResultAsync().ConfigureAwait(false);
                    names[commits[0].SHA] = Choose(commits[0].SHA, containing);
                    return names;
                }
                var wanted = new HashSet<string>(commits.ConvertAll(c => c.SHA), StringComparer.Ordinal);
                var pending = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
                var bits = new BigInteger[branches.Count];
                for (var i = 0; i < branches.Count; i++)
                {
                    bits[i] = BigInteger.One << i;
                    pending.TryGetValue(branches[i].Head, out var mask);
                    pending[branches[i].Head] = mask | bits[i];
                }
                if (branches.Count == 0)
                {
                    foreach (var commit in commits) names[commit.SHA] = "无分支";
                    return names;
                }
                Args = "rev-list --topo-order --parents --branches --remotes";
                using var process = new Process { StartInfo = CreateGitStartInfo(true) };
                Native.ProcessLauncher.Start(process);
                void Stop()
                {
                    try { if (!process.HasExited) Native.OS.TerminateProcess(process); }
                    catch (Exception) { /* The owned read-only process may already have exited. */ }
                }
                using var cancellation = CancellationToken.Register(Stop);
                var errors = process.StandardError.ReadToEndAsync();
                while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (CancellationToken.IsCancellationRequested) break;
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;
                    pending.Remove(parts[0], out var mask);
                    if (wanted.Remove(parts[0]))
                    {
                        var refs = new HashSet<string>(StringComparer.Ordinal);
                        for (var i = 0; i < bits.Length; i++)
                            if ((mask & bits[i]) != BigInteger.Zero) refs.Add(branches[i].FullName);
                        names[parts[0]] = Choose(parts[0], refs);
                        if (wanted.Count == 0) { Stop(); break; }
                    }
                    for (var i = 1; i < parts.Length; i++)
                    {
                        pending.TryGetValue(parts[i], out var parentMask);
                        pending[parts[i]] = parentMask | mask;
                    }
                }
                await process.WaitForExitAsync().ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                if (!CancellationToken.IsCancellationRequested && process.ExitCode == 0)
                    foreach (var sha in wanted) names[sha] = "无分支";
            }
            catch (Exception) when (CancellationToken.IsCancellationRequested) { }
            catch (Exception) { return new Dictionary<string, string>(StringComparer.Ordinal); }
            return names;
        }
    }
}
