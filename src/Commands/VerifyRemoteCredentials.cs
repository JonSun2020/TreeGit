namespace SourceGit.Commands
{
    internal sealed class VerifyRemoteCredentials : Command
    {
        protected override bool SupportsAuthenticationRetry => true;
        protected override bool RequiresAuthenticationTransportExitCode => true;
        internal VerifyRemoteCredentials(string repo, Models.Remote remote, string scope = null)
        {
            WorkingDirectory = Context = repo; CredentialScope = scope;
            SSHKey = remote.PrivateSSHKey; RaiseError = false; Editor = EditorType.None;
            Args = "ls-remote -- " + (remote.Name ?? remote.URL).Quoted() + " HEAD";
        }
    }
}
