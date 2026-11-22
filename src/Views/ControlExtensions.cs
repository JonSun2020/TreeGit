using System;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace SourceGit.Views
{
    public static class ControlExtensions
    {
        public static Control CreateFromViewModels(object data)
        {
            var dataTypeName = data.GetType().FullName;
            if (string.IsNullOrEmpty(dataTypeName) || !dataTypeName.Contains(".ViewModels.", StringComparison.Ordinal))
                return null;

            var viewTypeName = dataTypeName.Replace(".ViewModels.", ".Views.");
            var viewType = Type.GetType(viewTypeName);
            if (viewType != null)
                return Activator.CreateInstance(viewType) as Control;

            return null;
        }

        public static Task CopyPathAsync(this Control control, string path) =>
            control.CopyTextAsync(Native.OS.FormatLocalPath(path));

        public static async Task CopyTextAsync(this Control control, string text)
        {
            var topLevel = TopLevel.GetTopLevel(control) as Window;
            if (topLevel == null)
                return;

            try
            {
                if (topLevel.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(text);
            }
            catch (Exception e)
            {
                await new Alert().ShowAsync(topLevel, App.Text("TreeGit.Message.CopyFailed", e.Message), true);
            }
        }

        public static Path CreateMenuIcon(this Control control, string iconKey)
        {
            if (control?.FindResource(iconKey) is StreamGeometry geo)
            {
                return new Path()
                {
                    Data = geo,
                    Width = 12,
                    Height = 12,
                    Stretch = Stretch.Uniform
                };
            }

            return null;
        }

        public static void ShowWindow(this Control control, object data)
        {
            if (data == null)
                return;

            if (data is ViewModels.MergeConflictEditor merge && App.FindOpenMergeEditor(merge.RepositoryPath, merge.FilePath) is { } existing)
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }

            if (data is not ChromelessWindow window)
            {
                window = CreateFromViewModels(data) as ChromelessWindow;
                if (window == null)
                    return;

                window.DataContext = data;
            }

            var owner = TopLevel.GetTopLevel(control) as Window;
            do
            {
                if (owner != null)
                {
                    // Get the screen where current window locates.
                    var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
                    if (screen == null)
                        break;

                    // Calculate the startup position (Center Screen Mode) of target window
                    var rect = new PixelRect(PixelSize.FromSize(window.ClientSize, owner.DesktopScaling));
                    var centeredRect = screen.WorkingArea.CenterRect(rect);
                    if (owner.Screens.ScreenFromPoint(centeredRect.Position) == null)
                        break;

                    // Use the startup position
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Position = centeredRect.Position;
                }
            } while (false);

            if (owner != null) window.Show(owner);
            else window.Show();
        }

        public static Task ShowDialogAsync(this Control control, object data)
        {
            var owner = TopLevel.GetTopLevel(control) as Window;
            if (owner == null)
                return null;

            if (data is ChromelessWindow window)
                return window.ShowDialog(owner);

            window = CreateFromViewModels(data) as ChromelessWindow;
            if (window != null)
            {
                window.DataContext = data;
                return window.ShowDialog(owner);
            }

            return null;
        }
    }
}
