using System;

using Avalonia.Controls;

namespace SourceGit.Views
{
    public sealed class PathTextBox : TextBox
    {
        protected override Type StyleKeyOverride => typeof(TextBox);

        protected override string CoerceText(string value) => base.CoerceText(Native.OS.FormatLocalPath(value));
    }

    public sealed class RemoteAddressTextBox : TextBox
    {
        protected override Type StyleKeyOverride => typeof(TextBox);

        protected override string CoerceText(string value) => base.CoerceText(Native.OS.FormatRemoteAddress(value));
    }
}
