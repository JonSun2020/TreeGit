using System;
using System.Collections;
using System.Collections.Generic;

namespace SourceGit.ViewModels
{
    public class ChangeSelection
    {
        public List<Models.Change> Changes { get; }
        public bool IsSingleFolder { get; }
        public string SingleFolderPath { get; }
        public bool HasFolder { get; }

        public int Count => Changes.Count;

        public ChangeSelection(IList selected)
        {
            var changes = new List<Models.Change>();

            IsSingleFolder = false;
            SingleFolderPath = null;
            HasFolder = false;

            if (selected is { Count: > 0 })
            {
                foreach (var item in selected)
                {
                    if (item is Models.Change c)
                    {
                        changes.Add(c);
                    }
                    else if (item is ChangeTreeNode node)
                    {
                        CollectChangesInNode(changes, node);

                        if (node.IsFolder && !HasFolder)
                            HasFolder = true;
                    }
                }

                if (selected.Count == 1 && selected[0] is ChangeTreeNode { IsFolder: true } folder)
                {
                    IsSingleFolder = true;
                    SingleFolderPath = folder.FullPath;
                }
            }

            Changes = changes;
        }

        public bool IsChanged(ChangeSelection other)
        {
            if (other == null ||
                IsSingleFolder != other.IsSingleFolder ||
                SingleFolderPath != other.SingleFolderPath ||
                HasFolder != other.HasFolder ||
                Changes.Count != other.Changes.Count)
                return true;

            foreach (var c in other.Changes)
            {
                if (!Changes.Contains(c))
                    return true;
            }

            return false;
        }

        internal List<Models.Change> GetVisibleChanges(List<Models.Change> visible)
        {
            if (visible.Count == 0 || Count == 0)
                return [];

            if (Count == 1)
            {
                var selected = Changes[0];
                foreach (var change in visible)
                    if (change.Path == selected.Path)
                        return [selected];
                return [];
            }

            // A few selections can be checked without allocating a path set.
            if (Count <= 4)
                return Changes.FindAll(change => visible.Exists(item => item.Path == change.Path));

            var missing = new HashSet<string>(Count, StringComparer.Ordinal);
            foreach (var change in Changes)
                missing.Add(change.Path);

            foreach (var change in visible)
            {
                if (missing.Remove(change.Path) && missing.Count == 0)
                    return Changes;
            }

            var remaining = new List<Models.Change>(Count - missing.Count);
            foreach (var change in Changes)
                if (!missing.Contains(change.Path))
                    remaining.Add(change);
            return remaining;
        }

        private static void CollectChangesInNode(List<Models.Change> outs, ChangeTreeNode node)
        {
            if (node.IsFolder)
            {
                foreach (var child in node.Children)
                    CollectChangesInNode(outs, child);
            }
            else
            {
                if (!outs.Contains(node.Change))
                    outs.Add(node.Change);
            }
        }
    }
}
