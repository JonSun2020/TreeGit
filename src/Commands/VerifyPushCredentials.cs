using System.Threading.Tasks;

namespace SourceGit.Commands
{
    // Authenticate against receive-pack without sending an update, invoking a
    // pre-push hook, setting upstreams, or following additional refs. This runs
    // before a commit-and-push can change the index or create a local commit.
    internal sealed class VerifyPushCredentials : Command
    {
        protected override bool SupportsAuthenticationRetry => true;
        protected override bool RequiresAuthenticationTransportExitCode => true;

        internal VerifyPushCredentials(string repo, Models.Remote remote)
        {
            WorkingDirectory = Context = repo;
            HttpProxyTarget = remote.PushURL ?? remote.URL;
            SSHKey = remote.PrivateSSHKey;
            RaiseError = false;
            Editor = EditorType.None;
            Args = "-c push.autoSetupRemote=false -c push.followTags=false -c push.recurseSubmodules=no " +
                "-c " + ("remote." + remote.Name + ".mirror=false").Quoted() +
                " push --dry-run --no-verify --porcelain --all -- " + remote.Name.Quoted();
        }

        internal async Task<bool> VerifyAsync()
        {
            var success = await ExecAsync().ConfigureAwait(false);
            // A rejected dry-run ref (e.g. non-fast-forward) does not invalidate
            // credentials explicitly approved by Git's credential protocol.
            return !WasAuthenticationCancelled && (success || WasCredentialAccepted ||
                LastExitCode == 1 && FailureMessage.Contains("No refs in common and none specified", System.StringComparison.OrdinalIgnoreCase));
        }
    }
}
