using System.Text;

namespace SourceGit.Commands
{
    public class Rebase : Command
    {
        public Rebase(string repo, string newBase, string upstream)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"-c rebase.autoStash=false -c core.commentChar=\"^\" -c core.commentString=\"±\" rebase --onto {newBase.Quoted()} {upstream.Quoted()}";
        }

        public Rebase(string repo, string basedOn, bool autoStash, bool noVerify)
        {
            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder(512);
            builder.Append("-c rebase.autoStash=false -c core.commentChar=\"^\" -c core.commentString=\"±\" rebase ");
            if (autoStash)
                builder.Append("--autostash ");
            if (noVerify)
                builder.Append("--no-verify ");

            Args = builder.Append(basedOn).ToString();
        }
    }
}
