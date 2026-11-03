namespace SourceGit.Models
{
    public interface IRepository
    {
        bool IsAutoRefreshEnabled => true;

        bool MayHaveSubmodules();

        void RefreshBranches();
        void RefreshWorktrees();
        void RefreshTags();
        void RefreshCommits();
        void RefreshSubmodules();
        void RefreshWorkingCopyChanges();
        void RefreshStashes();
    }
}
