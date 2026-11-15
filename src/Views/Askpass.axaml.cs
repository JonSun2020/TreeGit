using System;
using System.Security.Cryptography;
using System.Text;

using Avalonia.Input;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class Askpass : ChromelessWindow
    {
        public Askpass()
        {
            InitializeComponent();
            Closed += (_, _) => { TxtUsername.Text = string.Empty; TxtPassphrase.Text = string.Empty; };
        }

        public bool IsCredentialPrompt { get; private set; }

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
            if (!IsCredentialPrompt)
                Console.Out.WriteLine("No passphrase entered.");
            App.Quit(-1);
        }

        private void EnterPassword(object _1, RoutedEventArgs _2)
        {
            var passphrase = TxtPassphrase.Text ?? string.Empty;
            var username = TxtUsername.Text?.Trim() ?? string.Empty;
            if (IsCredentialPrompt && (string.IsNullOrWhiteSpace(username) || username.IndexOfAny(['\r', '\n', '\0']) >= 0 ||
                passphrase.IndexOfAny(['\r', '\n', '\0']) >= 0))
            {
                ValidationMessage.Text = App.Text("TreeGit.Credentials.InvalidInput");
                ValidationMessage.IsVisible = true;
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
    }
}
