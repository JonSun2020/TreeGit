using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace SourceGit.Views
{
    public sealed class InternalDiffWindow : OwnedNativeWindow
    {
        public InternalDiffWindow(ViewModels.DiffContext context, Window owner)
        {
            Title = context.Title + " - TreeGit";
            Width = 1200;
            Height = 760;
            MinWidth = 640;
            MinHeight = 360;
            SystemDecorations = SystemDecorations.Full;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
            if (screen != null)
            {
                MaxWidth = Math.Max(1, screen.WorkingArea.Width / screen.Scaling - 32);
                MaxHeight = Math.Max(1, screen.WorkingArea.Height / screen.Scaling - 64);
                MinWidth = Math.Min(MinWidth, MaxWidth);
                MinHeight = Math.Min(MinHeight, MaxHeight);
                Width = Math.Min(Width, MaxWidth);
                Height = Math.Min(Height, MaxHeight);
            }
            Content = new DiffView { DataContext = context };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        }
    }
}
