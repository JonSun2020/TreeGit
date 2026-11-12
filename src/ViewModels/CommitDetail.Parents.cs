using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public partial class CommitDetail
    {
        public IReadOnlyList<Models.CommitParent> ParentRevisions
        {
            get => _parentRevisions;
            private set => SetProperty(ref _parentRevisions, value);
        }

        private void RefreshParentRevisions(Models.Commit commit, CancellationToken token)
        {
            var request = ++_parentRevisionsRequest;
            var parents = commit?.Parents.ToArray() ?? [];
            // Refreshing the same commit must not recreate text controls and
            // discard a user's active selection or a click in progress.
            if (!ParentRevisions.Select(parent => parent.SHA).SequenceEqual(parents, StringComparer.Ordinal))
                ParentRevisions = parents.Select(sha => new Models.CommitParent(sha)).ToArray();
            if (parents.Length == 0) return;
            Task.Run(async () =>
            {
                try
                {
                    var abbreviations = await new Commands.QueryUniqueCommitAbbreviations(_repo.FullPath, parents) { CancellationToken = token }
                        .GetResultAsync().ConfigureAwait(false);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested || request != _parentRevisionsRequest || _commit?.SHA != commit.SHA) return;
                        foreach (var parent in ParentRevisions) parent.ShortSHA = abbreviations[parent.SHA];
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    Native.OS.LogException(error);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested || request != _parentRevisionsRequest || _commit?.SHA != commit.SHA) return;
                        foreach (var parent in ParentRevisions) parent.ShortSHA = parent.SHA;
                    });
                }
            }, token);
        }

        private IReadOnlyList<Models.CommitParent> _parentRevisions = [];
        private int _parentRevisionsRequest;
    }
}
