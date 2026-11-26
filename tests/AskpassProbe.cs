using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SourceGit.Tests
{
    internal static class AskpassProbe
    {
        private sealed record Draft(string Username, string Password);
        private static readonly ConditionalWeakTable<Views.Askpass, Draft> _submittedDrafts = new();

        internal static void VerifyRevealAfterReturningToInput(Views.PasswordTextBox input)
        {
            if (input.InnerRightContent is not ToggleButton eye || !eye.Focus(NavigationMethod.Pointer))
                throw new InvalidOperationException("The eye button must accept focus.");
            eye.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            if (!input.RevealPassword || !input.Focus(NavigationMethod.Pointer) || !input.RevealPassword || eye.IsChecked != true)
                throw new InvalidOperationException("Returning from the eye to the password field must keep it revealed.");
        }

        internal static void Trace(string state)
        {
            var fixture = Environment.GetEnvironmentVariable("SOURCEGIT_TEST_ASKPASS_FIXTURE");
            if (!string.IsNullOrEmpty(fixture)) Append(fixture, $"state:{Environment.ProcessId}:{state}\n");
        }

        private static void Append(string fixture, string text)
        {
            using var gate = new System.Threading.Mutex(false, "Local\\TreeGit.AskpassFixture.Events");
            try { if (!gate.WaitOne(2000)) return; }
            catch (System.Threading.AbandonedMutexException) { }
            try { File.AppendAllText(fixture + ".events", text); }
            catch (IOException) { /* Diagnostics must never change authentication. */ }
            finally { gate.ReleaseMutex(); }
        }
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        private static void VerifyCredentialFields(Views.Askpass view)
        {
            var labels = new[] { view.RepositoryLabel, view.ServerLabel, view.UsernameLabel, view.PasswordLabel };
            Control[] values = [view.TxtRepository, view.TxtServer, view.TxtUsername, view.TxtPassphrase];
            double Right(Control c) => c.TranslatePoint(new Point(c.Bounds.Width, 0), view).Value.X;
            double X(Control c) => c.TranslatePoint(default, view).Value.X;
            double Center(Control c) => c.TranslatePoint(new Point(0, c.Bounds.Height / 2), view).Value.Y;
            bool Rounded(TextBox box) => box.GetVisualDescendants().OfType<Border>().Any(b =>
                b.Name == "PART_BorderElement" && b.CornerRadius.TopLeft > 0 && b.CornerRadius.BottomRight > 0);
            var token = Native.RepositoryCredentials.UsesGitHubToken(view.TxtServer.Text);
            if (labels.Any(label => Math.Abs(Right(label) - Right(labels[0])) > 1) ||
                values.Any(value => Math.Abs(X(value) - X(values[0])) > 1) ||
                Math.Abs(Center(view.UsernameLabel) - Center(view.TxtUsername)) > 1 ||
                Math.Abs(Center(view.PasswordLabel) - Center(view.TxtPassphrase)) > 1 ||
                !Rounded(view.TxtUsername) || !Rounded(view.TxtPassphrase) || view.TokenHelp.IsVisible != token ||
                view.PasswordLabel.Text != App.Text(token ? "TreeGit.Credentials.GitHubTokenLabel" : "TreeGit.Credentials.Password") ||
                view.TxtPassphrase.Watermark != App.Text(token ? "TreeGit.Credentials.GitHubTokenPlaceholder" : "TreeGit.Credentials.OptionalPassword"))
                throw new InvalidOperationException("Credential rows, rounded fields, or provider-specific labels are incorrect.");
        }

        public static void OnLoaded(Views.Askpass view, Action submit)
        {
            var fixture = Environment.GetEnvironmentVariable("SOURCEGIT_TEST_ASKPASS_FIXTURE");
            if (string.IsNullOrEmpty(fixture))
                return;
            using var document = JsonDocument.Parse(File.ReadAllText(fixture));
            var value = document.RootElement;
            var privateLocalAudit = OperatingSystem.IsWindows() && Automation.LocalAuditHost.IsEnabled;
            if (view.Topmost || view.ShowActivated == privateLocalAudit)
                throw new InvalidOperationException("Authentication must activate without covering other applications.");
            if (OperatingSystem.IsWindows() && long.TryParse(Environment.GetEnvironmentVariable("TREEGIT_AUTH_OWNER_WINDOW"), out var owner))
            {
                if (GetWindow(view.TryGetPlatformHandle().Handle, 4 /* GW_OWNER */).ToInt64() != owner)
                    throw new InvalidOperationException("The authentication prompt lost its verified TreeGit owner.");
                Append(fixture, "owner-verified\n");
            }
            if (view.IsCredentialPrompt)
            {
                if (!view.UsernameLabel.IsEffectivelyVisible || !view.TxtUsername.IsEffectivelyVisible || !view.TxtPassphrase.IsEffectivelyVisible)
                    throw new InvalidOperationException("Username and password must be visible in the same authentication window.");
                if (_submittedDrafts.TryGetValue(view, out var previous))
                {
                    if (!view.ValidationMessage.IsVisible || view.TxtUsername.Text != previous.Username || view.TxtPassphrase.Text != previous.Password)
                        throw new InvalidOperationException("Failed authentication must preserve the submitted account and secret.");
                    Append(fixture, "draft-preserved-after-failure\n");
                    _submittedDrafts.Remove(view);
                }
                var hasAttempts = value.TryGetProperty("attempts", out var attempts);
                var attempt = (hasAttempts ? attempts[0] : value).Clone();
                var next = hasAttempts && attempts.GetArrayLength() > 1 ? JsonSerializer.Serialize(new { attempts = attempts.EnumerateArray().Skip(1).Select(a => a.Clone()).ToArray() }) : null;
                var cancel = attempt.TryGetProperty("cancel", out var cancelValue) && cancelValue.GetBoolean();
                if (attempt.TryGetProperty("timeoutMs", out var timeoutMs) && Automation.LocalAuditHost.IsEnabled)
                    view.SetTestVerificationTimeout(Math.Clamp(timeoutMs.GetInt32(), 100, 30000));
                view.TxtUsername.Text = attempt.GetProperty("username").GetString();
                view.TxtPassphrase.Text = attempt.GetProperty("password").GetString();
                if (view.ValidationMessage.IsVisible && view.ValidationMessage.Text.Contains("重新输入", StringComparison.Ordinal))
                    Append(fixture, "retry-message\n");
                Append(fixture, "combined-dialog\nscoped-combined-dialog\n");
                Dispatcher.UIThread.Post(async () =>
                {
                    if (attempt.TryGetProperty("delayMs", out var delayMs)) await System.Threading.Tasks.Task.Delay(Math.Clamp(delayMs.GetInt32(), 0, 5000));
                    if (!File.Exists(fixture + ".png"))
                    {
                        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(view.Bounds.Width), (int)Math.Ceiling(view.Bounds.Height)), new Vector(96, 96));
                        bitmap.Render(view);
                        bitmap.Save(fixture + ".png");
                    }
                    var passwordProvider = ControlAutomationPeer.CreatePeerForElement(view.TxtPassphrase) as IValueProvider;
                    var usernameProvider = ControlAutomationPeer.CreatePeerForElement(view.TxtUsername) as IValueProvider;
                    if (passwordProvider?.Value != string.Empty || usernameProvider?.Value != view.TxtUsername.Text)
                        throw new InvalidOperationException("Accessibility must preserve the account field without exposing the password.");
                    view.UpdateLayout();
                    VerifyCredentialFields(view);
                    if (attempt.TryGetProperty("verifyFixedWidth", out var verifyWidth) && verifyWidth.GetBoolean())
                    {
                        var original = view.TxtPassphrase.Text;
                        var windowWidth = view.Bounds.Width;
                        var inputWidth = view.TxtPassphrase.Bounds.Width;
                        view.TxtPassphrase.Text = new string('x', 512);
                        await Task.Delay(100);
                        view.UpdateLayout();
                        if (Math.Abs(view.Bounds.Width - windowWidth) > .5 ||
                            Math.Abs(view.TxtPassphrase.Bounds.Width - inputWidth) > .5)
                            throw new InvalidOperationException("A long token must not resize the authentication dialog or the input field.");
                        view.TxtPassphrase.Text = original;
                        Append(fixture, "credential-width-stable\n");
                    }
                    VerifyRevealAfterReturningToInput(view.TxtPassphrase);
                    if (passwordProvider.Value != string.Empty)
                        throw new InvalidOperationException("Revealing a password must not publish it through the value interface.");
                    view.ToggleShowPassword.IsChecked = false;
                    if (view.TxtPassphrase.RevealPassword || view.TxtPassphrase.PasswordChar != '*')
                        throw new InvalidOperationException("The eye toggle did not restore star masking.");
                    Append(fixture, "visibility-toggle\n");
                    Append(fixture, "password-interface-protected\n");
                    if (attempt.TryGetProperty("expectTokenRequired", out var tokenRequired) && tokenRequired.GetBoolean())
                    {
                        view.TxtPassphrase.Text = string.Empty;
                        submit();
                        if (!view.ValidationMessage.IsVisible || view.ValidationMessage.Text != App.Text("TreeGit.Credentials.GitHubTokenRequired"))
                            throw new InvalidOperationException("GitHub HTTPS must require a token in manual mode.");
                        Append(fixture, "github-token-required\n");
                    }
                    if (next != null) File.WriteAllText(fixture, next);
                    if (cancel) view.Close();
                    else
                    {
                        var draft = new Draft(view.TxtUsername.Text, view.TxtPassphrase.Text);
                        submit();
                        if ((view.IsVerifying || view.ValidationMessage.IsVisible) &&
                            (view.TxtUsername.Text != draft.Username || view.TxtPassphrase.Text != draft.Password))
                            throw new InvalidOperationException("Submitting or validating an authentication form must keep the input.");
                        if (view.IsVerifying && (!view.VerificationOverlay.IsVisible || view.CredentialFields.IsEnabled || view.ConfirmAuthentication.IsEnabled))
                            throw new InvalidOperationException("The verifying mask must block repeated confirmation and input.");
                        if (view.IsVerifying)
                        {
                            _submittedDrafts.Add(view, draft);
                            var before = view.TxtPassphrase.Text;
                            var blocked = false;
                            try { passwordProvider.SetValue("blocked verification edit"); }
                            catch (InvalidOperationException) { blocked = true; }
                            if (!blocked || view.TxtPassphrase.Text != before)
                                throw new InvalidOperationException("Accessibility cannot edit credentials while verification is running.");
                        }
                        Trace(view.IsVerifying ? "verifying" : "input-validation");
                        if (attempt.TryGetProperty("cancelWhileVerifying", out var cancelDuring) && cancelDuring.GetBoolean())
                            Dispatcher.UIThread.Post(async () => { await System.Threading.Tasks.Task.Delay(150); Trace("cancel-verifying"); view.Close(); });
                        if (!view.IsVerifying && next != null)
                            Dispatcher.UIThread.Post(() => OnLoaded(view, submit), DispatcherPriority.Background);
                    }
                }, DispatcherPriority.Background);
                return;
            }
            var username = view.TxtDescription.Text.StartsWith("Username", StringComparison.OrdinalIgnoreCase);
            Append(fixture, username ? "username-dialog\n" : "password-dialog\n");
            Append(fixture, username ? "scoped-username-dialog\n" : "scoped-password-dialog\n");
            view.TxtPassphrase.Text = value.GetProperty(username ? "username" : "password").GetString();
            Dispatcher.UIThread.Post(submit, DispatcherPriority.Background);
        }
    }
}
