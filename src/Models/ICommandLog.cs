namespace SourceGit.Models
{
    public interface ICommandLogReceiver
    {
        void OnReceiveCommandLog(string line);
        void OnAuthenticationState(string state) { }
        void OnCommandFailed(string message) { }
    }

    public interface ICommandLog
    {
        void AppendLine(string line);
        bool AuthenticationCancelled => false;
        void MarkAuthenticationCancelled() { }
        void ReportAuthenticationState(string state) { }
        void MarkFailed(string message) { }
        bool HasOperationPanel => false;
        System.Threading.CancellationToken OperationCancellation => default;
    }
}
