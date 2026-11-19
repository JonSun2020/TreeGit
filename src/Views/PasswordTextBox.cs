using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace SourceGit.Views
{
    // TextBox's default value provider returns Text even when PasswordChar is
    // set. Mask both value queries and value-change notifications for secrets.
    public sealed class PasswordTextBox : TextBox
    {
        protected override Type StyleKeyOverride => typeof(TextBox);

        protected override AutomationPeer OnCreateAutomationPeer() => new PasswordPeer(this);

        private sealed class PasswordPeer : ControlAutomationPeer, IValueProvider
        {
            private readonly PasswordTextBox _input;

            internal PasswordPeer(PasswordTextBox input) : base(input)
            {
                _input = input;
                input.PropertyChanged += OnInputChanged;
            }

            public bool IsReadOnly => _input.IsReadOnly || !_input.IsEffectivelyEnabled;
            public string Value => _input.PasswordChar == '\0' ? _input.Text : string.Empty;

            public void SetValue(string value)
            {
                if (IsReadOnly) throw new InvalidOperationException("The input is unavailable.");
                _input.SetCurrentValue(TextProperty, value);
            }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
            protected override string GetPlaceholderTextCore() => _input.Watermark;

            private void OnInputChanged(object sender, AvaloniaPropertyChangedEventArgs e)
            {
                if (e.Property == TextProperty || e.Property == PasswordCharProperty)
                    RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, string.Empty, Value);
            }
        }
    }
}
