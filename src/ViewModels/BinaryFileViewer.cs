using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class BinaryFileViewer : ObservableObject
    {
        public string File
        {
            get => _file;
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        public Models.BinaryFile Content
        {
            get => _content;
            private set => SetProperty(ref _content, value);
        }

        public BinaryFileViewer(string repo, string file, string revision)
        {
            _repo = repo;
            _file = file;
            _revision = revision;
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            private set => SetProperty(ref _errorMessage, value);
        }

        public async Task LoadAsync()
        {
            if (_closed || _loadCancellation != null || Content != null) return;
            IsLoading = true;
            ErrorMessage = string.Empty;
            var cancellation = new CancellationTokenSource();
            _loadCancellation = cancellation;
            var repo = _repo;
            var file = _file;
            var revision = _revision;
            string temporary = null;
            Models.BinaryFile content = null;
            try
            {
                if (revision != null)
                {
                    temporary = Path.GetTempFileName();
                    if (!await Commands.SaveRevisionFile.RunAsync(repo, revision, file, temporary, cancellation.Token, reportFailure: false))
                    {
                        if (!_closed) ErrorMessage = App.Text("File.HexViewer.LoadFailed");
                        return;
                    }
                    cancellation.Token.ThrowIfCancellationRequested();
                    content = new Models.BinaryFile(temporary, true);
                    temporary = null;
                }
                else
                {
                    var path = Path.Combine(repo, file);
                    if (!System.IO.File.Exists(path)) throw new FileNotFoundException();
                    content = new Models.BinaryFile(path, false);
                }
                if (_closed) return;
                Content = content;
                content = null;
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                if (!_closed) ErrorMessage = App.Text("File.HexViewer.LoadFailed");
            }
            finally
            {
                content?.Dispose();
                if (temporary != null)
                {
                    try { System.IO.File.Delete(temporary); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Native.OS.LogException(error); }
                }
                _loadCancellation = null;
                cancellation.Dispose();
                if (!_closed) IsLoading = false;
            }
        }

        public void Cleanup()
        {
            _closed = true;
            _loadCancellation?.Cancel();
            _repo = null;
            _file = null;
            _revision = null;

            _content?.Dispose();
            _content = null;
        }

        private bool _isLoading = true;
        private string _repo = null;
        private string _file = null;
        private string _revision = null;
        private Models.BinaryFile _content = null;
        private string _errorMessage = string.Empty;
        private CancellationTokenSource _loadCancellation;
        private bool _closed;
    }
}
