using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Commit : Command
    {
        public Commit(string repo, string message, bool signOff, bool noVerify, bool amend, bool resetAuthor)
        {
            _tmpFile = Path.GetTempFileName();
            _message = message;

            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder();
            builder.Append("commit --allow-empty --file=");
            builder.Append(_tmpFile.Quoted());
            builder.Append(' ');

            if (signOff)
                builder.Append("--signoff ");

            if (noVerify)
                builder.Append("--no-verify ");

            if (amend)
            {
                builder.Append("--amend ");
                if (resetAuthor)
                    builder.Append("--reset-author ");
                builder.Append("--no-edit");
            }

            Args = builder.ToString();
        }

        public async Task<bool> RunAsync()
        {
            try
            {
                await File.WriteAllTextAsync(_tmpFile, _message).ConfigureAwait(false);
                return await ExecAsync().ConfigureAwait(false);
            }
            catch (System.Exception ex)
            {
                Log?.AppendLine(ex.Message);
                if (RaiseError) RaiseException(ex.Message);
                return false;
            }
            finally
            {
                try { File.Delete(_tmpFile); }
                catch (IOException) { }
                catch (System.UnauthorizedAccessException) { }
            }
        }

        private readonly string _tmpFile;
        private readonly string _message;
    }
}
