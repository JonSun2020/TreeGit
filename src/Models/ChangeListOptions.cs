using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SourceGit.Models
{
    public sealed class ChangeListOption(string resourceKey) : LocalizedOption
    {
        public string DisplayName => App.Text(resourceKey);
    }

    public static class ChangeListOptions
    {
        public static readonly ChangeListOption[] StatusFilters =
            [new("UI.AllChanges"), new("UI.Conflicts"), new("UI.Modified"), new("UI.Untracked")];
        public static readonly ChangeListOption[] SortOrders =
            [new("UI.SortPath"), new("UI.SortName"), new("UI.SortStatus")];

        public static List<Change> Apply(List<Change> changes, string text, int status, int order, bool staged)
        {
            IEnumerable<Change> query = changes;
            if (!string.IsNullOrEmpty(text)) query = query.Where(c => c.Path.Contains(text, StringComparison.OrdinalIgnoreCase));
            query = status switch
            {
                1 => query.Where(c => c.IsConflicted),
                2 => query.Where(c => (staged ? c.Index : c.WorkTree) is ChangeState.Modified or ChangeState.TypeChanged),
                3 => query.Where(c => !staged && c.WorkTree == ChangeState.Untracked),
                _ => query,
            };
            var sorted = order switch
            {
                1 => query.OrderBy(c => Path.GetFileName(c.Path), Comparer<string>.Create(NumericSort.Compare)),
                2 => query.OrderBy(c => staged ? c.Index : c.WorkTree),
                _ => query.OrderBy(c => c.Path, Comparer<string>.Create(NumericSort.Compare)),
            };
            return sorted.ThenBy(c => c.Path, StringComparer.Ordinal).ToList();
        }
    }
}
