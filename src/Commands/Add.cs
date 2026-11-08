namespace SourceGit.Commands
{
    public class Add : Command
    {
        public Add(string repo, string pathspecFromFile, bool nul = false)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"--literal-pathspecs add --force --verbose {(nul ? "--pathspec-file-nul " : string.Empty)}--pathspec-from-file={pathspecFromFile.Quoted()}";
        }
    }
}
