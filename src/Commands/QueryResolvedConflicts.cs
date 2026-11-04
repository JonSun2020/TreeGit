namespace SourceGit.Commands
{
    internal sealed class QueryResolvedConflicts : Command
    {
        public QueryResolvedConflicts(string repository)
        {
            WorkingDirectory = repository;
            Context = repository;
            Args = "ls-files --resolve-undo";
            RaiseError = false;
            Editor = EditorType.None;
        }

        public bool HasRecords()
        {
            var result = ReadToEnd();
            return result.IsSuccess && !string.IsNullOrWhiteSpace(result.StdOut);
        }
    }
}
