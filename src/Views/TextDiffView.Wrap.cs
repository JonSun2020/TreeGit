using System.Linq;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class TextDiffView
    {
        private SideBySideWrapAlignment _wrapAlignment;
        private bool _wrapActive;
        private bool _wrapSetupPending;

        private void InitializeWrapAlignment()
        {
            Loaded += (_, _) => { _wrapActive = true; QueueWrapAlignment(); };
            Unloaded += (_, _) => { _wrapActive = false; _wrapAlignment?.Dispose(); _wrapAlignment = null; };
            DataContextChanged += (_, _) => { _wrapAlignment?.Dispose(); _wrapAlignment = null; QueueWrapAlignment(); };
            LayoutUpdated += (_, _) => QueueWrapAlignment();
        }

        private void QueueWrapAlignment()
        {
            if (!_wrapActive || _wrapSetupPending) return;
            _wrapSetupPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _wrapSetupPending = false;
                if (!_wrapActive) return;
                var sides = this.GetVisualDescendants().OfType<SingleSideTextDiffPresenter>().ToArray();
                var left = sides.FirstOrDefault(side => side.IsOld);
                var right = sides.FirstOrDefault(side => !side.IsOld);
                if (left != null && right != null && _wrapAlignment?.Matches(left, right) == true) return;
                _wrapAlignment?.Dispose();
                _wrapAlignment = left != null && right != null ? new SideBySideWrapAlignment(left, right) : null;
            }, DispatcherPriority.Loaded);
        }
    }
}
