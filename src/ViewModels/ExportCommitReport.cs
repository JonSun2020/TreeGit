using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    internal sealed class ExportCommitReport(
        Repository repo,
        IReadOnlyList<Models.Commit> commits,
        Func<string, Task<string>> pickDestination) : Popup
    {
        public IReadOnlyList<int> ContextOptions => ContextChoices;

        public int ContextLines
        {
            get => _contextLines;
            set => SetProperty(ref _contextLines, value);
        }

        public string CommitCount => App.Text("CommitReport.Selection", commits.Count);

        public override void RefreshLocalizedText()
        {
            base.RefreshLocalizedText();
            OnPropertyChanged(nameof(CommitCount));
        }

        public override async Task<bool> Sure()
        {
            try
            {
                SetProgressDescription("CommitReport.ChooseFile");
                var branch = repo.CurrentBranch is { IsDetachedHead: false } current ? current.FriendlyName : "HEAD";
                var reportName = MakeReportName(branch, DateTime.Now);
                var destination = await pickDestination(reportName + ".html");
                if (destination == null)
                    return false;

                SetProgressDescription("CommitReport.Writing");
                var labels = new Commands.ExportCommitReport.Labels(
                    App.ActiveLocale.Replace('_', '-'), App.Text("CommitReport.SelectCommit"),
                    App.Text("CommitReport.Context"), App.Text("CommitReport.Author"), App.Text("CommitReport.Date"),
                    App.Text("CommitReport.Parents"), App.Text("CommitReport.Changes"), App.Text("CommitReport.FirstParent"),
                    App.Text("CommitReport.EmptyTree"), App.Text("CommitReport.NoChanges"),
                    App.Text("CommitDetail.Changes.Search"), App.Text("CommitReport.NoMatches"), App.Text("CommitReport.Wrap"),
                    App.Text("CommitReport.Binary"), App.Text("CommitReport.Added"), App.Text("CommitReport.Removed"),
                    App.Text("CommitDetail.Info.SHA"), App.Text("CommitReport.OldVersion"), App.Text("CommitReport.NewVersion"),
                    App.Text("Diff.Prev"), App.Text("Diff.Next"), App.Text("CommitDetail.Changes.Count").Trim(),
                    App.Text("TreeGit.History.Description"), App.Text("TreeGit.History.Date"),
                    App.Text("TreeGit.History.Commit"), App.Text("CommitReport.ResizeColumn"),
                    App.Text("CommitReport.Branch"), App.Text("CommitBranches.Unknown"), App.Text("CommitBranches.Failed"), App.Text("CommitReport.ResizePane"),
                    App.Text("CommitReport.Ordinal"));
                var graph = repo.Histories?.Commits ?? [];
                var locals = repo.Branches.Where(item => item.IsLocal && !item.IsDetachedHead).ToList();
                var shown = (repo.Histories?.DetailContext as CommitDetail)?.BranchDetails;
                var details = commits.ToDictionary(commit => commit.SHA,
                    commit => shown?.Commit.SHA == commit.SHA ? shown : commit.SearchBranchDetails, StringComparer.Ordinal);
                using var cancellation = new CancellationTokenSource();
                _cancellation = cancellation;
                CanTerminate = true;
                var context = ContextLines;
                await Task.Run(async () =>
                {
                    var selections = await ResolveSelectionsAsync(graph, locals, details, cancellation.Token).ConfigureAwait(false);
                    var export = new Commands.ExportCommitReport(repo.FullPath, selections, context, reportName, labels);
                    await export.WriteAsync(destination, cancellation.Token).ConfigureAwait(false);
                }, cancellation.Token);

                repo.SendNotification(App.Text("CommitReport.Saved", destination));
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                repo.SendNotification(App.Text("CommitReport.Failed", Native.RepositoryCredentials.RedactLog(ex.Message)), true);
                return false;
            }
            finally
            {
                _cancellation = null;
                CanTerminate = false;
            }
        }

        public override void Terminate() => _cancellation?.Cancel();

        private async Task<Commands.ExportCommitReport.Selection[]> ResolveSelectionsAsync(List<Models.Commit> graph,
            List<Models.Branch> locals, Dictionary<string, Models.CommitBranchDetails> details, CancellationToken cancellation)
        {
            var missing = commits.Where(commit => details[commit.SHA] == null).ToList();
            if (missing.Count > 0)
            {
                // Use the same coverage lookup and branch ranking as the change comparison.
                var branches = await new Commands.QuerySearchBranchNames(repo.FullPath) { CancellationToken = cancellation }
                    .GetResultAsync(missing).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (branches.Count > 0)
                {
                    var origins = locals.Count == 0 ? new Dictionary<string, string>(StringComparer.Ordinal) :
                        await new Commands.QueryLocalCommitOrigins(repo.FullPath) { CancellationToken = cancellation }
                            .GetResultAsync(locals.Select(branch => branch.FullName).ToHashSet(StringComparer.Ordinal)).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    var positions = Models.CommitBranchDetails.IndexGraph(graph);
                    foreach (var commit in missing)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (branches.TryGetValue(commit.SHA, out var coverage))
                            details[commit.SHA] = Models.CommitBranchDetails.FromSnapshot(commit, coverage, graph, locals, origins, positions);
                    }
                }
            }

            return commits.Select(commit =>
            {
                var detail = details[commit.SHA];
                return new Commands.ExportCommitReport.Selection(commit.SHA, commit.Subject,
                    Models.DateTimeFormat.Format(commit.CommitterTime), commit.Author?.Name ?? string.Empty,
                    detail?.Primary?.FriendlyName, detail == null);
            }).ToArray();
        }

        private static string MakeReportName(string branch, DateTime timestamp)
        {
            var safe = new StringBuilder();
            var invalid = Path.GetInvalidFileNameChars();
            var bytes = 0;
            foreach (var rune in (branch ?? "").EnumerateRunes())
            {
                var replace = Rune.IsControl(rune) || rune.IsBmp &&
                    (Array.IndexOf(invalid, (char)rune.Value) >= 0 || "<>:\"/\\|?*".Contains((char)rune.Value));
                var size = replace ? 1 : rune.Utf8SequenceLength;
                if (bytes + size > 234)
                    break;
                safe.Append(replace ? "_" : rune.ToString());
                bytes += size;
            }
            var name = safe.ToString().Trim(' ', '.');
            return (name.Length > 0 ? name : "HEAD") + "_" + timestamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        }

        private static readonly int[] ContextChoices = [20, 50, 100];
        private int _contextLines = 20;
        private CancellationTokenSource _cancellation;
    }
}
