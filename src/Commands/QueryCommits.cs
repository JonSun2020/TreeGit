using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryCommits : Command
    {
        public bool Succeeded { get; private set; }
        public QueryCommits(string repo, string limits, bool markMerged = true)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"log --no-show-signature --decorate=full --format=%H%x00%P%x00%D%x00%aN±%aE%x00%at%x00%cN±%cE%x00%ct%x00%s {limits}";
            _markMerged = markMerged;
        }

        public QueryCommits(string repo, string filter, Models.CommitSearchMethod method, bool onlyCurrentBranch)
        {
            var builder = new StringBuilder();
            builder.Append("log -1000 --date-order --no-show-signature --decorate=full --format=%H%x00%P%x00%D%x00%aN±%aE%x00%at%x00%cN±%cE%x00%ct%x00%s ");

            if (!onlyCurrentBranch)
                builder.Append("--branches --remotes ");

            if (method == Models.CommitSearchMethod.ByAuthor)
            {
                builder.Append("-i --author=").Append(filter.Quoted());
            }
            else if (method == Models.CommitSearchMethod.ByMessage)
            {
                var words = filter.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var word in words)
                    builder.Append("--grep=").Append(word.Trim().Quoted()).Append(' ');
                builder.Append("--all-match -i");
            }
            else if (method == Models.CommitSearchMethod.ByPath)
            {
                builder.Append("-- ").Append(filter.Quoted());
            }
            else
            {
                builder.Append("-G").Append(filter.Quoted());
            }

            WorkingDirectory = repo;
            Context = repo;
            Args = builder.ToString();
            _markMerged = false;
        }

        public async Task<List<Models.Commit>> GetResultAsync()
        {
            Succeeded = false;
            var commits = new List<Models.Commit>();
            try
            {
                CancellationToken.ThrowIfCancellationRequested();
                using var proc = new Process();
                proc.StartInfo = CreateGitStartInfo(true);
                Native.ProcessLauncher.Start(proc);
                using var cancellation = CancellationToken.Register(() =>
                {
                    try { if (!proc.HasExited) Native.OS.TerminateProcess(proc); }
                    catch (InvalidOperationException) { }
                });
                proc.BeginErrorReadLine();

                var findHead = false;
                while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    var commit = ParseCommitLine(line);
                    if (commit == null)
                        continue;

                    commits.Add(commit);

                    if (!findHead && commit.IsMerged)
                        findHead = true;
                }

                await proc.WaitForExitAsync().ConfigureAwait(false);
                if (proc.ExitCode != 0 || CancellationToken.IsCancellationRequested) return [];

                if (_markMerged && !findHead && commits.Count > 0)
                {
                    var ancestry = new QueryCurrentBranchCommitHashes(WorkingDirectory, commits[^1].CommitterTime) { CancellationToken = CancellationToken };
                    var set = await ancestry.GetResultAsync().ConfigureAwait(false);
                    if (!ancestry.Succeeded) return [];

                    foreach (var c in commits)
                    {
                        if (set.Contains(c.SHA))
                        {
                            c.IsMerged = true;
                            break;
                        }
                    }
                }
                Succeeded = !CancellationToken.IsCancellationRequested;
            }
            catch (Exception e)
            {
                if (!CancellationToken.IsCancellationRequested) RaiseException(App.Text("TreeGit.Query.CommitsFailed", e.Message));
            }

            return Succeeded ? commits : [];
        }

        internal static Models.Commit ParseCommitLine(string line)
        {
            var data = line.AsSpan();
            Span<Range> fields = stackalloc Range[7];
            var start = 0;
            for (var i = 0; i < fields.Length; i++)
            {
                var end = data[start..].IndexOf('\0');
                if (end < 0)
                    return null;
                end += start;
                fields[i] = start..end;
                start = end + 1;
            }

            var subject = data[start..];
            if (subject.Contains('\0'))
                return null;

            var commit = new Models.Commit() { SHA = data[fields[0]].ToString() };
            commit.ParseParents(data[fields[1]].ToString());
            commit.ParseDecorators(data[fields[2]].ToString());
            commit.Author = Models.User.FindOrAdd(data[fields[3]]);
            commit.AuthorTime = ulong.Parse(data[fields[4]]);
            commit.Committer = Models.User.FindOrAdd(data[fields[5]]);
            commit.CommitterTime = ulong.Parse(data[fields[6]]);
            commit.Subject = subject.ToString();
            return commit;
        }

        private bool _markMerged = false;
    }
}
