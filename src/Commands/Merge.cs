using System.Collections.Generic;
using System.Text;

namespace SourceGit.Commands
{
    public class Merge : Command
    {
        public Merge(string repo, string source, string mode, bool edit, bool autoStash = false, bool allowUnrelated = false)
        {
            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder();
            builder.Append("merge --progress --no-overwrite-ignore ");
            builder.Append(edit ? "--edit " : "--no-edit ");
            if (autoStash) builder.Append("--autostash ");
            if (allowUnrelated) builder.Append("--allow-unrelated-histories ");
            builder.Append(source.Quoted());
            builder.Append(' ');
            builder.Append(mode);

            Args = builder.ToString();
        }

        public Merge(string repo, List<string> targets, bool autoCommit, string strategy, bool autoStash = false, string mode = null, string messageFile = null)
        {
            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder();
            builder.Append("merge --progress --no-overwrite-ignore ");
            if (autoStash) builder.Append("--autostash ");
            if (!string.IsNullOrEmpty(mode)) builder.Append(mode).Append(' ');
            if (messageFile != null) builder.Append("--no-edit --file=").Append(messageFile.Quoted()).Append(' ');
            if (!string.IsNullOrEmpty(strategy))
                builder.Append("--strategy=").Append(strategy).Append(' ');
            if (!autoCommit)
                builder.Append("--no-commit ");

            foreach (var t in targets)
            {
                builder.Append(t.Quoted());
                builder.Append(' ');
            }

            Args = builder.ToString();
        }
    }
}
