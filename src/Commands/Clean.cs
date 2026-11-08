namespace SourceGit.Commands
{
    public class Clean : Command
    {
        public Clean(string repo, Models.CleanMode mode, string path = null)
        {
            WorkingDirectory = repo;
            Context = repo;

            Args = mode switch
            {
                Models.CleanMode.OnlyUntrackedFiles => "clean -qfd",
                Models.CleanMode.OnlyIgnoredFiles => "clean -qfdX",
                _ => "clean -qfdx",
            };
            if (path != null) Args = $"--literal-pathspecs {Args} -- {path.Quoted()}";
        }
    }
}
