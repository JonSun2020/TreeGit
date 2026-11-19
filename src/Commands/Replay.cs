using System.Diagnostics;
using System.Threading.Tasks;
using System;

namespace SourceGit.Commands
{
    public class Replay : MergePreview
    {
        public Replay(string repo, string onto, string range)
        {
            WorkingDirectory = repo;
            Context = repo;
            RaiseError = false;
            _onto = onto;
            _range = range;
        }

        protected override async Task<(string Arguments, string Head)> PreparePreviewAsync()
        {
            // Resolve one two-dot range before leaving the repository; the
            // isolated preview deliberately contains no mutable branch refs.
            var separator = _range.IndexOf("..", StringComparison.Ordinal);
            if (separator <= 0 || _range.IndexOf("..", separator + 2, StringComparison.Ordinal) >= 0) return default;
            var onto = await ResolveCommitAsync(_onto).ConfigureAwait(false);
            var start = await ResolveCommitAsync(_range[..separator]).ConfigureAwait(false);
            var end = await ResolveCommitAsync(_range[(separator + 2)..]).ConfigureAwait(false);
            if (!IsObjectId(onto) || !IsObjectId(start) || !IsObjectId(end) || onto.Length != start.Length || onto.Length != end.Length) return default;
            // Git 2.56 otherwise updates refs by default. Keep the explicit
            // print setting even though all writes now go to quarantine.
            // Git checks a two-dot range as a possible filename even with --.
            // Separate endpoints avoid the 130-character SHA-256 filename that
            // otherwise exceeds Windows path limits under a deep working dir.
            return ($"-c replay.refAction=print -c merge.renormalize=false replay --onto {onto} {end} ^{start} --", end);
        }

        private readonly string _onto, _range;
    }
}
