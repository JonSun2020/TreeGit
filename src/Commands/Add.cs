namespace SourceGit.Commands
{
    public class Add : Command
    {
        public Add(string repo, string pathspecFromFile)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"--literal-pathspecs add --force --verbose --pathspec-from-file={pathspecFromFile.Quoted()}";
        }
    }
}
