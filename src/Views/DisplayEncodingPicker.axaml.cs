using System;
using Avalonia.Controls;

namespace SourceGit.Views
{
    public partial class DisplayEncodingPicker : ComboBox
    {
        protected override Type StyleKeyOverride => typeof(ComboBox);
        public DisplayEncodingPicker() => InitializeComponent();
    }
}
