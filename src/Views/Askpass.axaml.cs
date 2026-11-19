using System;
using System.Security.Cryptography;
using System.Text;

using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SourceGit.Views
{
    public partial class Askpass : ChromelessWindow
    {
        public Askpass()
        {
            InitializeComponent();
            CloseOnESC = true;
            Closed += (_, _) =>
            {
#if INTEGRATION_TESTS
                Tests.AskpassProbe.Trace($"window-closed-{_sessionClosing}-{_sessionSucceeded}");
#endif
                _verificationTimer.Stop();
                TxtUsername.Text = string.Empty; TxtPassphrase.Text = string.Empty;
                if (_sessionSubmit != null && !_sessionClosing && !_sessionSucceeded) _sessionCancel?.Invoke();
            };
            _verificationTimer.Tick += (_, _) =>
            {
                _verificationTimer.Stop();
                SessionFailed("TreeGit.Credentials.Timeout");
                _sessionTimeout?.Invoke();
            };
        }

        public bool IsCredentialPrompt { get; private set; }
        internal bool IsVerifying { get; private set; }
        internal bool SessionSucceeded => _sessionSucceeded;

        internal void ConfigureSession(Native.CredentialDialogMessage request, Action<string, string> submit, Action cancel, Action timeout)
        {
            _sessionSubmit = submit; _sessionCancel = cancel; _sessionTimeout = timeout;
            if (string.IsNullOrEmpty(request.Prompt)) ConfigureCredentials(request.Repo, request.Remote, request.Username, request.Retry);
            else
            {
                TxtDescription.Text = request.Prompt;
                if (request.Prompt.StartsWith("Username", StringComparison.OrdinalIgnoreCase)) { TxtPassphrase.PasswordChar = '\0'; ToggleShowPassword.IsVisible = false; }
            }
        }

        internal void PrepareSessionRequest(Native.CredentialDialogMessage request)
        {
            if (IsVerifying || request.Retry && !ValidationMessage.IsVisible) SessionFailed("TreeGit.Credentials.Retry");
            if (IsCredentialPrompt && string.IsNullOrEmpty(TxtUsername.Text)) TxtUsername.Text = request.Username ?? string.Empty;
            Activate();
#if INTEGRATION_TESTS
            // LFS may retry within its own command after the rejection has already enabled input.
            if (_loaded) Tests.AskpassProbe.OnLoaded(this, () => EnterPassword(null, null));
#endif
        }

        internal void SessionFailed(string key)
        {
#if INTEGRATION_TESTS
            Tests.AskpassProbe.Trace(key);
#endif
            if (!IsVerifying && ValidationMessage.IsVisible && ValidationMessage.Text == App.Text(key)) return;
            _verificationTimer.Stop(); IsVerifying = false;
            CredentialFields.IsEnabled = ConfirmAuthentication.IsEnabled = true;
            VerificationOverlay.IsVisible = false;
            ValidationMessage.Text = App.Text(key); ValidationMessage.IsVisible = true;
            TxtPassphrase.Text = string.Empty; TxtPassphrase.Focus();
        }

        internal async void SessionAccepted()
        {
            if (_sessionSucceeded) return;
#if INTEGRATION_TESTS
            Tests.AskpassProbe.Trace("success");
#endif
            _verificationTimer.Stop(); _sessionSucceeded = true;
            VerificationStatus.Text = App.Text("TreeGit.Credentials.Success");
            VerificationOverlay.IsVisible = true;
            ConfirmAuthentication.IsEnabled = false;
            await System.Threading.Tasks.Task.Delay(500);
            CloseSession();
        }

        internal void CloseSession() { _sessionClosing = true; _verificationTimer.Stop(); Close(); }

        protected override void OnOpened(EventArgs e)
        {
            if (OperatingSystem.IsWindows()) Native.Windows.AttachAuthenticationOwner(this);
            base.OnOpened(e);
        }

        public void ConfigureCredentials(string repo, string remote, string username, bool retry = false)
        {
            IsCredentialPrompt = true;
            Title = App.Text("TreeGit.Credentials.Authentication");
            TxtTitle.Text = Title;
            TxtDescription.Text = App.Text("TreeGit.Credentials.Context", repo, remote);
            UsernameSection.IsVisible = true;
            TxtUsername.Text = username ?? string.Empty;
            if (retry)
            {
                ValidationMessage.Text = App.Text("TreeGit.Credentials.Retry");
                ValidationMessage.IsVisible = true;
            }
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
#if INTEGRATION_TESTS
            _loaded = true;
#endif
            Activate();
            if (IsCredentialPrompt && string.IsNullOrWhiteSpace(TxtUsername.Text))
                TxtUsername.Focus(NavigationMethod.Directional);
            else
                TxtPassphrase.Focus(NavigationMethod.Directional);
#if INTEGRATION_TESTS
            Tests.AskpassProbe.OnLoaded(this, () => EnterPassword(null, null));
#endif
        }

        private void CloseWindow(object _1, RoutedEventArgs _2)
        {
            if (_sessionSubmit != null) { _sessionCancel?.Invoke(); CloseSession(); return; }
            if (!IsCredentialPrompt)
                Console.Error.WriteLine(Native.CredentialSession.CancelMarker);
            App.Quit(-1);
        }

        private void EnterPassword(object _1, RoutedEventArgs _2)
        {
            if (IsVerifying || _sessionSucceeded) return;
            var passphrase = TxtPassphrase.Text ?? string.Empty;
            var username = TxtUsername.Text?.Trim() ?? string.Empty;
            if (IsCredentialPrompt && (string.IsNullOrWhiteSpace(username) || username.IndexOfAny(['\r', '\n', '\0']) >= 0 ||
                passphrase.IndexOfAny(['\r', '\n', '\0']) >= 0))
            {
                ValidationMessage.Text = App.Text("TreeGit.Credentials.InvalidInput");
                ValidationMessage.IsVisible = true;
                return;
            }
            if (_sessionSubmit != null)
            {
                IsVerifying = true;
                ValidationMessage.IsVisible = false;
                CredentialFields.IsEnabled = ConfirmAuthentication.IsEnabled = false;
                VerificationStatus.Text = App.Text("TreeGit.Credentials.Verifying");
                VerificationOverlay.IsVisible = true;
                _verificationTimer.Start();
                _sessionSubmit(username, passphrase);
                TxtPassphrase.Text = string.Empty;
                return;
            }
            byte[] passBytes = Encoding.UTF8.GetBytes(IsCredentialPrompt ? $"username={username}\npassword={passphrase}\n\n" : passphrase + "\n");
            try
            {
                var outStream = Console.OpenStandardOutput();
                outStream.Write(passBytes, 0, passBytes.Length);
                outStream.Flush();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passBytes);
                TxtPassphrase.Text = string.Empty;
                TxtUsername.Text = string.Empty;
            }
            App.Quit(0);
        }

        private Action<string, string> _sessionSubmit;
        private Action _sessionCancel, _sessionTimeout;
        private bool _sessionClosing, _sessionSucceeded;
#if INTEGRATION_TESTS
        private bool _loaded;
        internal void SetTestVerificationTimeout(int milliseconds) => _verificationTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
#endif
        private readonly DispatcherTimer _verificationTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    }
}
