using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    internal static class DataGridScrolling
    {
        // Avalonia DataGrid 11.3 handles scrolling through ScrollBar.Scroll;
        // assigning Value alone moves the thumb without moving the rows.
        private static readonly MethodInfo ProcessVerticalScroll = typeof(DataGrid).GetMethod("ProcessVerticalScroll", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo ProcessHorizontalScroll = typeof(DataGrid).GetMethod("ProcessHorizontalScroll", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void StepVertical(DataGrid grid, bool down) =>
            ProcessVerticalScroll?.Invoke(grid, [down ? ScrollEventType.SmallIncrement : ScrollEventType.SmallDecrement]);

        public static void SetOffset(ScrollBar bar, double value)
        {
            bar.SetCurrentValue(RangeBase.ValueProperty, Math.Clamp(value, bar.Minimum, bar.Maximum));
            if (bar.FindAncestorOfType<DataGrid>() is { } grid)
            {
                var handler = bar.Name == "PART_VerticalScrollbar" ? ProcessVerticalScroll : bar.Name == "PART_HorizontalScrollbar" ? ProcessHorizontalScroll : null;
                handler?.Invoke(grid, [ScrollEventType.ThumbTrack]);
            }
        }
    }
}
