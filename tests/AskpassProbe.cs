using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace SourceGit.Tests
{
    internal static class AskpassProbe
    {
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
                if (!view.UsernameSection.IsVisible || !view.TxtUsername.IsEffectivelyVisible || !view.TxtPassphrase.IsEffectivelyVisible)
                    throw new InvalidOperationException("Username and password must be visible in the same authentication window.");
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
                    view.ToggleShowPassword.IsChecked = true;
                    if (!view.TxtPassphrase.RevealPassword) throw new InvalidOperationException("The eye toggle did not reveal the password.");
                    if (passwordProvider.Value != string.Empty)
                        throw new InvalidOperationException("Revealing a password must not publish it through the value interface.");
                    view.ToggleShowPassword.IsChecked = false;
                    if (view.TxtPassphrase.RevealPassword || view.TxtPassphrase.PasswordChar != '*')
                        throw new InvalidOperationException("The eye toggle did not restore star masking.");
                    Append(fixture, "visibility-toggle\n");
                    Append(fixture, "password-interface-protected\n");
                    if (next != null) File.WriteAllText(fixture, next);
                    if (cancel) view.Close();
                    else
                    {
                        submit();
                        if (view.IsVerifying && (!view.VerificationOverlay.IsVisible || view.CredentialFields.IsEnabled || view.ConfirmAuthentication.IsEnabled))
                            throw new InvalidOperationException("The verifying mask must block repeated confirmation and input.");
                        if (view.IsVerifying)
                        {
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
