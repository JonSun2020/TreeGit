using System;
using System.Collections.Generic;
using System.Text;

namespace SourceGit.Models
{
    public class DiffOption
    {
        public bool IsLocalChange => _revisions.Count == 0;
        public bool IsUnstaged => _isUnstaged;
        public List<string> Revisions => _revisions;
        public string Path => _path;
        public string OrgPath => _orgPath;
        public string BinaryOldRevision { get; }
        public string BinaryNewRevision { get; }
        public string BinaryOldPath => _binaryOldPath ?? (string.IsNullOrEmpty(_orgPath) ? _path : _orgPath);
        public string BinaryNewPath => _binaryNewPath ?? _path;

        /// <summary>
        ///     Only used for working copy changes
        /// </summary>
        /// <param name="change"></param>
        /// <param name="isUnstaged"></param>
        public DiffOption(Change change, bool isUnstaged)
        {
            _isUnstaged = isUnstaged;
            _path = change.Path;
            _orgPath = change.OriginalPath;
            BinaryOldRevision = isUnstaged ? string.Empty : change.DataForAmend?.ParentSHA ?? "HEAD";
            BinaryNewRevision = isUnstaged ? null : string.Empty;

            if (isUnstaged)
            {
                switch (change.WorkTree)
                {
                    case ChangeState.Added:
                    case ChangeState.Untracked:
                        _extra = "--no-index";
                        _orgPath = "/dev/null";
                        break;
                }
                _binaryOldPath = change.WorkTree == ChangeState.Renamed ? change.OriginalPath : _orgPath == "/dev/null" ? "/dev/null" : change.Path;
            }
            else
            {
                if (change.DataForAmend != null)
                    _extra = $"--cached {change.DataForAmend.ParentSHA}";
                else
                    _extra = "--cached";
            }
        }

        /// <summary>
        ///     Only used for commit changes.
        /// </summary>
        /// <param name="commit"></param>
        /// <param name="change"></param>
        public DiffOption(Commit commit, Change change)
        {
            BinaryOldRevision = commit.FirstParentToCompare;
            BinaryNewRevision = commit.SHA;
            _revisions.Add(commit.FirstParentToCompare);
            _revisions.Add(commit.SHA);
            _path = change.Path;
            _orgPath = change.OriginalPath;
        }

        /// <summary>
        ///     Used to diff in `FileHistory`
        /// </summary>
        /// <param name="ver"></param>
        public DiffOption(FileVersion ver)
        {
            BinaryOldRevision = ver.HasParent ? $"{ver.SHA}^" : EmptyTreeHash.Guess(ver.SHA);
            BinaryNewRevision = ver.SHA;
            _binaryOldPath = string.IsNullOrEmpty(ver.OriginalPath) ? ver.Path : ver.OriginalPath;
            if (string.IsNullOrEmpty(ver.OriginalPath))
            {
                _revisions.Add(ver.HasParent ? $"{ver.SHA}^" : EmptyTreeHash.Guess(ver.SHA));
                _revisions.Add(ver.SHA);
                _path = ver.Path;
            }
            else
            {
                _revisions.Add($"{ver.SHA}^:{ver.OriginalPath.Quoted()}");
                _revisions.Add($"{ver.SHA}:{ver.Path.Quoted()}");
                _path = ver.Path;
                _orgPath = ver.Change.OriginalPath;
                _ignorePaths = true;
            }
        }

        /// <summary>
        ///     Used to diff two revisions in `FileHistory`
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        public DiffOption(FileVersion start, FileVersion end)
        {
            BinaryOldRevision = start.Change.Index == ChangeState.Deleted ? EmptyTreeHash.Guess(end.SHA) : start.SHA;
            BinaryNewRevision = end.Change.Index == ChangeState.Deleted ? EmptyTreeHash.Guess(start.SHA) : end.SHA;
            _binaryOldPath = start.Path;
            _binaryNewPath = end.Path;
            if (start.Change.Index == ChangeState.Deleted)
            {
                _revisions.Add(EmptyTreeHash.Guess(end.SHA));
                _revisions.Add(end.SHA);
                _path = end.Path;
            }
            else if (end.Change.Index == ChangeState.Deleted)
            {
                _revisions.Add(start.SHA);
                _revisions.Add(EmptyTreeHash.Guess(start.SHA));
                _path = start.Path;
            }
            else if (!end.Path.Equals(start.Path, StringComparison.Ordinal))
            {
                _revisions.Add($"{start.SHA}:{start.Path.Quoted()}");
                _revisions.Add($"{end.SHA}:{end.Path.Quoted()}");
                _path = end.Path;
                _orgPath = start.Path;
                _ignorePaths = true;
            }
            else
            {
                _revisions.Add(start.SHA);
                _revisions.Add(end.SHA);
                _path = start.Path;
            }
        }

        /// <summary>
        ///     Used to show differences between two revisions.
        /// </summary>
        /// <param name="baseRevision"></param>
        /// <param name="targetRevision"></param>
        /// <param name="change"></param>
        public DiffOption(string baseRevision, string targetRevision, Change change)
        {
            BinaryOldRevision = string.IsNullOrEmpty(baseRevision) ? null : baseRevision;
            BinaryNewRevision = string.IsNullOrEmpty(targetRevision) ? null : targetRevision;
            _revisions.Add(string.IsNullOrEmpty(baseRevision) ? "-R" : baseRevision);
            _revisions.Add(targetRevision);
            _path = change.Path;
            _orgPath = change.OriginalPath;
        }

        /// <summary>
        ///     Converts to diff command arguments.
        /// </summary>
        public override string ToString()
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrEmpty(_extra))
                builder.Append($"{_extra} ");
            foreach (var r in _revisions)
                builder.Append($"{r} ");

            if (_ignorePaths)
                return builder.ToString();

            builder.Append("-- ");
            if (!string.IsNullOrEmpty(_orgPath))
                builder.Append($"{_orgPath.Quoted()} ");
            builder.Append(_path.Quoted());

            return builder.ToString();
        }

        private readonly bool _isUnstaged = false;
        private readonly string _path;
        private readonly string _orgPath = string.Empty;
        private readonly string _extra = string.Empty;
        private readonly List<string> _revisions = [];
        private readonly bool _ignorePaths = false;
        private readonly string _binaryOldPath;
        private readonly string _binaryNewPath;
    }
}
