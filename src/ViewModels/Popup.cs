using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class Popup : ObservableValidator, Models.ICommandLogReceiver
    {
        public string DialogTitle => GetType().Name switch
        {
            "AddRemote" => App.Text("Remote.AddTitle"),
            "AddSubmodule" => App.Text("Submodule.Add"),
            "AddToIgnore" => App.Text("AddToIgnore"),
            "AddWorktree" => App.Text("AddWorktree"),
            "Apply" => App.Text("Apply.Title"),
            "ApplyStash" => App.Text("ApplyStash"),
            "Archive" => App.Text("Archive.Title"),
            "ChangeSubmoduleUrl" => App.Text("ChangeSubmoduleUrl"),
            "Checkout" => App.Text("Checkout"),
            "CheckoutAndFastForward" => App.Text("Checkout.WithFastForward"),
            "CheckoutAsWorktree" => App.Text("CheckoutAsWorktree"),
            "CheckoutBranchFromStash" => App.Text("CheckoutBranchFromStash"),
            "CheckoutDetached" => App.Text("CheckoutDetached"),
            "CheckoutRemoteBranch" => App.Text("CheckoutRemote.Title"),
            "CheckoutRemoteBranchAsWorktree" => App.Text("CheckoutAsWorktree"),
            "Cleanup" => App.Text("Repository.Clean"),
            "ClearStashes" => App.Text("ClearStashes"),
            "Clone" => App.Text("Clone"),
            "CreateBranch" => App.Text("CreateBranch.Title"),
            "CreateGroup" => App.Text("Welcome.AddRootFolder"),
            "CreateTag" => App.Text("CreateTag.Title"),
            "DeinitSubmodule" => App.Text("DeinitSubmodule"),
            "DeleteBranch" => App.Text("DeleteBranch"),
            "DeleteMultipleBranches" => App.Text("DeleteMultiBranch"),
            "DeleteMultipleTags" => App.Text("DeleteMultiTags"),
            "DeleteRemote" => App.Text("DeleteRemote"),
            "DeleteRepositoryNode" => App.Text(this is DeleteRepositoryNode d && d.Node.IsRepository ? "DeleteRepositoryNode.TitleForRepository" : "DeleteRepositoryNode.TitleForGroup"),
            "DeleteSubmodule" => App.Text("DeleteSubmodule"),
            "DeleteTag" => App.Text("DeleteTag"),
            "Discard" => App.Text("Discard"),
            "DropStash" => App.Text("StashDropConfirm"),
            "EditBranchDescription" => App.Text("EditBranchDescription"),
            "EditRemote" => App.Text("Remote.EditTitle"),
            "EditRepositoryNode" => App.Text(this is EditRepositoryNode e && e.IsRepository ? "EditRepositoryNode.TitleForRepository" : "EditRepositoryNode.TitleForGroup"),
            "ExecuteCustomAction" => (this as ExecuteCustomAction)?.CustomAction.Name,
            "Fetch" => App.Text("Fetch.Title"),
            "FetchInto" => App.Text("Fetch.Title"),
            "GitFlowFinish" => App.Text((this as GitFlowFinish)?.Type switch
            {
                Models.GitFlowBranchType.Release => "GitFlow.FinishRelease",
                Models.GitFlowBranchType.Hotfix => "GitFlow.FinishHotfix",
                _ => "GitFlow.FinishFeature",
            }),
            "GitFlowStart" => App.Text((this as GitFlowStart)?.Type switch
            {
                Models.GitFlowBranchType.Release => "GitFlow.StartReleaseTitle",
                Models.GitFlowBranchType.Hotfix => "GitFlow.StartHotfixTitle",
                _ => "GitFlow.StartFeatureTitle",
            }),
            "Init" => App.Text("Init"),
            "InitGitFlow" => App.Text("GitFlow.Init"),
            "LFSFetch" => App.Text("GitLFS.Fetch.Title"),
            "LFSPrune" => App.Text("GitLFS.Prune"),
            "LFSPull" => App.Text("GitLFS.Pull.Title"),
            "LFSPush" => App.Text("GitLFS.Push.Title"),
            "LFSTrackCustomPattern" => App.Text("GitLFS.AddTrackPattern.Title"),
            "Merge" => App.Text("Merge"),
            "MergeMultiple" => App.Text("MergeMultiple"),
            "MoveRepositoryNode" => App.Text("MoveRepositoryNode"),
            "MoveSubmodule" => App.Text("MoveSubmodule"),
            "OpenLocalRepository" => App.Text("OpenLocalRepository"),
            "PruneRemote" => App.Text("PruneRemote"),
            "PruneWorktrees" => App.Text("PruneWorktrees"),
            "Pull" => App.Text("Pull.Title"),
            "Push" => App.Text("Push.Title"),
            "PushRevision" => App.Text("Push.Revision.Title"),
            "PushTag" => App.Text("PushTag"),
            "Rebase" => App.Text("Rebase"),
            "RemoveWorktree" => App.Text("RemoveWorktree"),
            "RenameBranch" => App.Text("RenameBranch"),
            "Reset" => App.Text("Reset"),
            "ResetWithoutCheckout" => App.Text("ResetWithoutCheckout"),
            "ScanRepositories" => App.Text("ScanRepositories"),
            "SetPushURL" => App.Text("SetPushUrl"),
            "SetSubmoduleBranch" => App.Text("SetSubmoduleBranch"),
            "SetUpstream" => App.Text("SetUpstream"),
            "StashChanges" => App.Text("Stash.Title"),
            "TrustRepository" => App.Text("TrustRepository"),
            "UpdateSubmodules" => App.Text("UpdateSubmodules"),
            _ => string.Empty,
        };
        public bool UsesDialogTitle => !string.IsNullOrEmpty(DialogTitle);

        public bool CanTerminate
        {
            get => _canTerminate;
            protected set
            {
                if (OperatingSystem.IsWindows() || Native.OS.SupportSetSid())
                    SetProperty(ref _canTerminate, value);
            }
        }

        public bool InProgress
        {
            get => _inProgress;
            set => SetProperty(ref _inProgress, value);
        }

        public string ProgressDescription
        {
            get => _progressDescription;
            set => SetProperty(ref _progressDescription, value);
        }

        [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2026:RequiresUnreferencedCode")]
        public bool Check()
        {
            if (HasErrors)
                return false;
            ValidateAllProperties();
            return !HasErrors;
        }

        public void OnReceiveCommandLog(string data)
        {
            var desc = data.Trim();
            if (desc.Length > 1024) desc = desc[..1024];
            if (!string.IsNullOrEmpty(desc))
                ProgressDescription = desc;
        }

        public virtual void Cleanup()
        {
            _log?.Unsubscribe(this);
            if (_log is { IsComplete: false }) _log.Complete();
        }

        public virtual bool CanStartDirectly()
        {
            return true;
        }

        public virtual Task<bool> Sure()
        {
            return null;
        }

        public virtual void Terminate()
        {
        }

        protected void Use(CommandLog log)
        {
            if (_log != null && !ReferenceEquals(_log, log))
            {
                _log.Unsubscribe(this);
                if (!_log.IsComplete) _log.Complete();
            }
            _log = log;
            _log.Subscribe(this);
        }

        private bool _canTerminate = false;
        private bool _inProgress = false;
        private string _progressDescription = string.Empty;
        private CommandLog _log = null;
    }
}
