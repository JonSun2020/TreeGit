using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading.Tasks;

using Avalonia.Input.Platform;

namespace SourceGit.ViewModels
{
    public class Apply : Popup
    {
        [CustomValidation(typeof(Apply), nameof(ValidatePatchFile))]
        public string PatchFile
        {
            get => _patchFile;
            set => SetProperty(ref _patchFile, value, true);
        }

        public bool IgnoreWhiteSpace
        {
            get => _ignoreWhiteSpace;
            set => SetProperty(ref _ignoreWhiteSpace, value);
        }

        public Models.ApplyWhiteSpaceMode SelectedWhiteSpaceMode
        {
            get;
            set;
        }

        public bool FromClipboard
        {
            get => _fromClipboard;
            set => SetProperty(ref _fromClipboard, value);
        }

        public bool ThreeWayMerge
        {
            get;
            set;
        } = true;

        public bool CanCreateCommit
        {
            get => _canCreateCommit;
            set => SetProperty(ref _canCreateCommit, value);
        }

        public bool CommitChanges
        {
            get;
            set;
        } = true;

        public IClipboard Clipboard
        {
            get;
            set;
        } = null;

        public Apply(Repository repo)
        {
            _repo = repo;
            SelectedWhiteSpaceMode = Models.ApplyWhiteSpaceMode.Supported[0];
        }

        public static ValidationResult ValidatePatchFile(string file, ValidationContext ctx)
        {
            if (ctx.ObjectInstance is not Apply apply)
                return new ValidationResult("Invalid object instance!!!");

            if (apply.FromClipboard)
            {
                apply.CanCreateCommit = false;
                return ValidationResult.Success;
            }

            if (string.IsNullOrEmpty(file))
            {
                apply.CanCreateCommit = false;
                return new ValidationResult("Please select a patch file!!!");
            }

            if (!File.Exists(file))
            {
                apply.CanCreateCommit = false;
                return new ValidationResult($"File '{file}' can NOT be found!!!");
            }

            apply.CheckPatchFileFormat(file);
            return ValidationResult.Success;
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = "Apply patch...";
            var finalPatchFile = _patchFile;
            CommandLog log = null;
            try
            {
                if (InProgressContext.HasPendingOperation(_repo.GitDir))
                {
                    _repo.SendNotification(App.Text("Rebase.FinishCurrent"), true);
                    return false;
                }
                if (_fromClipboard)
                {
                    if (Clipboard == null)
                    {
                        _repo.SendNotification("Clipboard service is not available.", true);
                        return false;
                    }
                    var content = await Clipboard.TryGetTextAsync();
                    if (string.IsNullOrEmpty(content) || !content.StartsWith("diff --git ", StringComparison.Ordinal))
                    {
                        _repo.SendNotification("There's no valid patch content in clipboard.", true);
                        return false;
                    }
                    finalPatchFile = Path.GetTempFileName();
                    await File.WriteAllTextAsync(finalPatchFile, content);
                }
                var safety = new Commands.CheckMergeSafety(_repo.FullPath, [], patchFile: finalPatchFile);
                if (!await safety.GetResultAsync())
                {
                    _repo.SendNotification(App.Text(safety.Obstruction == null ? "WorkingCopy.RefreshFailed" : "Rebase.LocalFilesBlocked") +
                        (safety.Obstruction == null ? string.Empty : "\n" + safety.Obstruction), true);
                    return false;
                }
                log = _repo.CreateLog("Apply Patch");
                Use(log);
                var extra = ThreeWayMerge ? "--3way" : string.Empty;
                Commands.Command command = !_fromClipboard && _canCreateCommit && CommitChanges ?
                    new Commands.Am(_repo.FullPath, finalPatchFile, _ignoreWhiteSpace, SelectedWhiteSpaceMode.Arg, extra) :
                    new Commands.Apply(_repo.FullPath, finalPatchFile, _ignoreWhiteSpace, SelectedWhiteSpaceMode.Arg, extra);
                // Applying a patch never consumes an unrelated typed commit-message draft.
                var success = await command.Use(log).ExecAsync();
                var conflicts = await new Commands.QueryConflictEntries(_repo.FullPath).GetResultAsync();
                if (!string.IsNullOrEmpty(conflicts) || InProgressContext.HasPendingOperation(_repo.GitDir))
                {
                    _repo.WorkingCopy.RequestConflictSelection();
                    _repo.ShowWorkingCopyOverview();
                    return true;
                }
                if (conflicts == null) _repo.NotifyWorkingCopyStatusFailure();
                return success;
            }
            finally
            {
                log?.Complete();
                if (_fromClipboard && finalPatchFile != _patchFile && File.Exists(finalPatchFile)) File.Delete(finalPatchFile);
                _repo.RefreshBranches();
                _repo.MarkWorkingCopyDirtyManually();
            }
        }

        private void CheckPatchFileFormat(string file)
        {
            try
            {
                using var reader = new StreamReader(file);
                var lines = new List<string>();
                for (int i = 0; i < 4; i++)
                {
                    if (reader.EndOfStream)
                        break;
                    lines.Add(reader.ReadLine());
                }

                CanCreateCommit = lines.Count == 4 &&
                    lines[0].StartsWith("From ", StringComparison.Ordinal) &&
                    lines[1].StartsWith("From: ", StringComparison.Ordinal) &&
                    lines[2].StartsWith("Date: ", StringComparison.Ordinal) &&
                    lines[3].StartsWith("Subject: ", StringComparison.Ordinal);
            }
            catch
            {
                CanCreateCommit = false;
            }
        }

        private readonly Repository _repo = null;
        private string _patchFile = string.Empty;
        private bool _fromClipboard = false;
        private bool _ignoreWhiteSpace = true;
        private bool _canCreateCommit = false;
    }
}
