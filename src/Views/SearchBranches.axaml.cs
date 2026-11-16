using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SourceGit.Views
{
    public partial class SearchBranches : UserControl
    {
        public SearchBranches()
        {
            InitializeComponent();
            AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => BranchFilter.Focus(), DispatcherPriority.Loaded);
            AttachedToVisualTree += (_, _) => App.LocaleChanged += OnLocaleChanged;
            DetachedFromVisualTree += (_, _) => App.LocaleChanged -= OnLocaleChanged;
        }

        public static Flyout CreateFlyout(Control target, SearchBranches content) => new ScreenBoundFlyout { Content = content };

        private void OnLocaleChanged()
        {
            if (DataContext is ViewModels.SearchBranches context) context.RefreshLocalizedText();
        }

        private void OnBranchClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ViewModels.SearchBranchRow row } && DataContext is ViewModels.SearchBranches context)
                context.Choose(row.Branch);
            e.Handled = true;
        }

        private void OnBranchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && BranchList.SelectedItem is ViewModels.SearchBranchRow row && DataContext is ViewModels.SearchBranches context)
            {
                context.Choose(row.Branch);
                e.Handled = true;
            }
        }
    }
}
