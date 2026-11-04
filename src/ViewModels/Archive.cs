using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class Archive : Popup
    {
        [Required(ErrorMessage = "Output file name is required")]
        public string SaveFile
        {
            get => _saveFile;
            set => SetProperty(ref _saveFile, value, true);
        }

        public object BasedOn
        {
            get;
            private set;
        }

        public Archive(Repository repo, Models.Branch branch)
        {
            _repo = repo;
            _revision = branch.Head;
            _saveFile = $"archive-{Path.GetFileName(branch.Name)}.zip";
            BasedOn = branch;
        }

        public Archive(Repository repo, Models.Commit commit)
        {
            _repo = repo;
            _revision = commit.SHA;
            _saveFile = $"archive-{commit.SHA.AsSpan(0, 10)}.zip";
            BasedOn = commit;
        }

        public Archive(Repository repo, Models.Tag tag)
        {
            _repo = repo;
            _revision = tag.SHA;
            _saveFile = $"archive-{Path.GetFileName(tag.Name)}.zip";
            BasedOn = tag;
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = "Archiving ...";

            var log = _repo.CreateLog("Archive");
            Use(log);

            string stagedFile = null;
            try
            {
                var destination = Path.GetFullPath(Path.IsPathRooted(_saveFile) ? _saveFile : Path.Combine(_repo.FullPath, _saveFile));
                stagedFile = Path.Combine(Path.GetDirectoryName(destination)!, ".treegit-archive-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (File.Open(stagedFile, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

                // Git opens --output before validating the revision. Build next
                // to the destination and replace it only after a complete archive.
                if (!await new Commands.Archive(_repo.FullPath, _revision, stagedFile).Use(log).ExecAsync())
                    return false;
                File.Move(stagedFile, destination, true);
                _repo.SendNotification($"Save archive to : {_saveFile}");
                return true;
            }
            catch (Exception ex)
            {
                log.AppendLine(ex.Message);
                _repo.SendNotification(ex.Message, true);
                return false;
            }
            finally
            {
                log.Complete();
                try { if (stagedFile != null) File.Delete(stagedFile); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private readonly Repository _repo = null;
        private string _saveFile;
        private readonly string _revision;
    }
}
