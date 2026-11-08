using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class Histories
    {
        private static readonly string[] ColumnNames = ["graph", "description", "date", "author", "commit", "authorDate"];
        private bool _columnGesture;
        private bool _columnChanged;

        private void InitializeColumnResizing()
        {
            CommitListContainer.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            {
                if (e.Source is Control source && source.FindAncestorOfType<DataGridColumnHeader>(true) != null)
                {
                    _columnGesture = true;
                    _columnChanged = false;
                }
            }, RoutingStrategies.Tunnel, true);
            CommitListContainer.AddHandler(InputElement.PointerReleasedEvent, (_, _) => FinishColumnResize(), RoutingStrategies.Bubble, true);
            CommitListContainer.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => FinishColumnResize(), RoutingStrategies.Bubble, true);
            foreach (var column in CommitListContainer.Columns)
                column.PropertyChanged += (_, e) =>
                {
                    if (_columnGesture && e.Property == DataGridColumn.WidthProperty) _columnChanged = true;
                };
        }

        private void FinishColumnResize()
        {
            if (!_columnGesture) return;
            if (_columnChanged && DataContext is ViewModels.Histories vm)
                vm.SaveHistoryColumnWidths(GetColumnWidths());
            _columnGesture = false;
        }

        private void RestoreColumnWidths()
        {
            if (DataContext is not ViewModels.Histories vm) return;
            for (var i = 0; i < ColumnNames.Length; i++)
            {
                var width = vm.GetHistoryColumnWidth(ColumnNames[i]);
                var column = CommitListContainer.Columns[i];
                column.MinWidth = i == 0 ? 40 : i == 1 ? 100 : 60;
                column.Width = width.HasValue ? new(width.Value) : i switch
                {
                    0 => new(vm.GraphWidth), 1 => new(1, DataGridLengthUnitType.Star),
                    2 or 5 => new(160), 3 => new(vm.AuthorColumnWidth), _ => new(100),
                };
            }
        }

        public Dictionary<string, double> GetColumnWidths()
        {
            var widths = new Dictionary<string, double>();
            for (var i = 0; i < ColumnNames.Length; i++)
            {
                var column = CommitListContainer.Columns[i];
                var width = column.ActualWidth;
                if (!double.IsFinite(width) || width < column.MinWidth)
                    width = (DataContext as ViewModels.Histories)?.GetHistoryColumnWidth(ColumnNames[i]) ?? (i == 0 ? 48 : i == 1 ? 400 : i is 2 or 5 ? 160 : i == 3 ? 120 : 100);
                widths[ColumnNames[i]] = Math.Round(width, 2);
            }
            return widths;
        }

        public Dictionary<string, double> ConfigureColumns(Dictionary<string, double> widths, bool reset)
        {
            if (DataContext is not ViewModels.Histories vm) throw new InvalidOperationException("History is not loaded.");
            foreach (var pair in widths)
                if (!ColumnNames.Contains(pair.Key) || !double.IsFinite(pair.Value) || pair.Value < (pair.Key == "graph" ? 40 : pair.Key == "description" ? 100 : 60) || pair.Value > 8192)
                    throw new ArgumentException("Invalid history column width: " + pair.Key);
            if (reset) vm.ResetHistoryColumnWidths();
            vm.SaveHistoryColumnWidths(widths);
            RestoreColumnWidths();
            CommitListContainer.UpdateLayout();
            return GetColumnWidths();
        }

        private void UpdateWorkingCopyColumns()
        {
            if (WorkingCopyColumns == null) return;
            WorkingCopyRow.Classes.Set("selected", DataContext is ViewModels.Histories { IsWorkingCopySelected: true });
            // Column measurement is temporarily incomplete while the selected
            // details view changes. Do not publish that intermediate geometry.
            var widths = CommitListContainer.Columns.Select(c => c.IsVisible ? c.ActualWidth : 0).ToArray();
            for (var i = 0; i < widths.Length; i++)
                if (CommitListContainer.Columns[i].IsVisible && (!double.IsFinite(widths[i]) || widths[i] < CommitListContainer.Columns[i].MinWidth)) return;
            for (var i = 0; i < ColumnNames.Length; i++)
            {
                WorkingCopyColumns.ColumnDefinitions[i].Width = new GridLength(widths[i]);
            }
        }
    }
}
