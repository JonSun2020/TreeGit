namespace SourceGit.Commands
{
    public class Restore : Command
    {
        public Restore(string repo, string pathspecFile)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"--literal-pathspecs restore --progress --worktree --recurse-submodules --pathspec-from-file={pathspecFile.Quoted()}";
        }
    }
}
