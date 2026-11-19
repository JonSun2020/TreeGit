using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class MergeTree : MergePreview
    {
        public MergeTree(string repo, string source, string dest)
        {
            WorkingDirectory = repo;
            RaiseError = false;
            _source = source;
            _destination = dest;
        }

        protected override async Task<(string Arguments, string Head)> PreparePreviewAsync()
        {
            var source = await ResolveCommitAsync(_source).ConfigureAwait(false);
            var destination = await ResolveCommitAsync(_destination).ConfigureAwait(false);
            if (!IsObjectId(source) || !IsObjectId(destination) || source.Length != destination.Length) return default;
            return ($"-c merge.renormalize=false merge-tree --write-tree {source} {destination}", destination);
        }

        private readonly string _source, _destination;
    }
}
