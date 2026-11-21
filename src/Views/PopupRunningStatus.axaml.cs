using Avalonia;
using Avalonia.Controls;

namespace SourceGit.Views
{
    public partial class PopupRunningStatus : UserControl
    {
        public static readonly DirectProperty<PopupRunningStatus, string> DescriptionProperty =
            AvaloniaProperty.RegisterDirect<PopupRunningStatus, string>(
                nameof(Description),
                static o => o.Description,
                static (o, v) => o.Description = v);

        public static readonly StyledProperty<bool> IsRunningProperty =
            AvaloniaProperty.Register<PopupRunningStatus, bool>(nameof(IsRunning));

        public string Description
        {
            get => _description;
            set => SetAndRaise(DescriptionProperty, ref _description, value);
        }

        public bool IsRunning
        {
            get => GetValue(IsRunningProperty);
            set => SetValue(IsRunningProperty, value);
        }

        public PopupRunningStatus()
        {
            InitializeComponent();
        }

        private string _description = string.Empty;
    }
}
