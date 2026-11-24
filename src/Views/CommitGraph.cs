using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SourceGit.Views
{
    public class CommitGraph : Control
    {
        public static readonly StyledProperty<bool> HasPendingChangesProperty = AvaloniaProperty.Register<CommitGraph, bool>(nameof(HasPendingChanges));
        public bool HasPendingChanges { get => GetValue(HasPendingChangesProperty); set => SetValue(HasPendingChangesProperty, value); }
        public static readonly DirectProperty<CommitGraph, Models.CommitGraph> GraphProperty =
            AvaloniaProperty.RegisterDirect<CommitGraph, Models.CommitGraph>(
                nameof(Graph),
                static o => o.Graph,
                static (o, v) => o.Graph = v);

        public Models.CommitGraph Graph
        {
            get => _graph;
            set => SetAndRaise(GraphProperty, ref _graph, value);
        }

        public static readonly DirectProperty<CommitGraph, Models.CommitGraphLayout> LayoutProperty =
            AvaloniaProperty.RegisterDirect<CommitGraph, Models.CommitGraphLayout>(
                nameof(Layout),
                static o => o.Layout,
                static (o, v) => o.Layout = v);

        public Models.CommitGraphLayout Layout
        {
            get => _layout;
            set => SetAndRaise(LayoutProperty, ref _layout, value);
        }

        public static readonly StyledProperty<IBrush> DotBrushProperty =
            AvaloniaProperty.Register<CommitGraph, IBrush>(nameof(DotBrush), Brushes.Transparent);

        public IBrush DotBrush
        {
            get => GetValue(DotBrushProperty);
            set => SetValue(DotBrushProperty, value);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _graph?.UpdatePalette();
            Models.CommitGraph.PaletteChanged += OnPaletteChanged;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            Models.CommitGraph.PaletteChanged -= OnPaletteChanged;
            base.OnDetachedFromVisualTree(e);
        }

        private void OnPaletteChanged()
        {
            _graph?.UpdatePalette();
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            if (_layout == null)
                return;
            _graph?.UpdatePalette();

            var startY = _layout.StartY;
            var clipWidth = _layout.ClipWidth;
            var clipHeight = Bounds.Height;
            var rowHeight = _layout.RowHeight;
            var rowOffset = HasPendingChanges ? _layout.PendingRowHeight : 0;
            var endY = startY + clipHeight - rowOffset + 28;

            using (context.PushClip(new Rect(0, 0, clipWidth, clipHeight)))
            {
                // The working-copy row is pinned; real history must not paint
                // over its marker when the list has scrolled to older commits.
                var bodyTop = rowOffset;
                if (_graph != null)
                {
                    using (context.PushClip(new Rect(0, bodyTop, clipWidth, System.Math.Max(0, clipHeight - bodyTop))))
                    using (context.PushTransform(Matrix.CreateTranslation(0, rowOffset - startY)))
                    {
                        DrawCurves(context, _graph, startY - rowOffset + bodyTop, endY, rowHeight);
                        DrawAnchors(context, _graph, startY - rowOffset + bodyTop, endY, rowHeight);
                    }
                }
                if (HasPendingChanges)
                {
                    var gray = new Pen(Brushes.Gray, 2);
                    var origin = new Point(6, rowOffset * .5);
                    if (startY <= .01 && _graph?.HasWorkingCopyNode == true)
                    {
                        foreach (var path in _graph.Paths)
                            for (var i = 1; i < path.Points.Count && path.Points[i - 1].Y < 0; i++)
                            {
                                var a = path.Points[i - 1]; var b = path.Points[i];
                                if (b.Y > 0) b = new Point(a.X + (b.X - a.X) * -a.Y / (b.Y - a.Y), 0);
                                context.DrawLine(gray, new Point(a.X, rowOffset + a.Y * rowOffset), new Point(b.X, rowOffset + b.Y * rowOffset));
                            }
                    }
                    else
                        context.DrawLine(gray, origin, new Point(origin.X, rowOffset));
                    context.DrawEllipse(new SolidColorBrush(Color.Parse("#8A8A8A")), new Pen(Brushes.White, 1.5), origin, 5, 5);
                }
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == GraphProperty ||
                change.Property == LayoutProperty ||
                change.Property == DotBrushProperty || change.Property == HasPendingChangesProperty)
                InvalidateVisual();
        }

        private void DrawCurves(DrawingContext context, Models.CommitGraph graph, double top, double bottom, double rowHeight)
        {
            var grayedPen = new Pen(new SolidColorBrush(Colors.Gray, 0.4), Models.CommitGraph.Pens[0].Thickness);

            var links = graph.Links;
            for (var i = FindFirstVisible(links, top, rowHeight, static link => link.End.Y); i < links.Count; ++i)
            {
                var link = links[i];
                var startY = link.Start.Y * rowHeight;
                if (startY > bottom)
                    break;

                var endY = link.End.Y * rowHeight;
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new Point(link.Start.X, startY), false);
                    ctx.LineTo(new Point(link.End.X, endY));
                }

                var pen = link.IsHighlighted ? Models.CommitGraph.Pens[link.Color] : grayedPen;
                context.DrawGeometry(null, pen, geo);
            }

            foreach (var line in graph.Paths)
            {
                var last = new Point(line.Points[0].X, line.Points[0].Y * rowHeight);
                var size = line.Points.Count;
                var endY = line.Points[size - 1].Y * rowHeight;

                if (endY < top)
                    continue;
                if (last.Y > bottom)
                    break;

                var geo = new StreamGeometry();
                var pen = line.IsHighlighted ? Models.CommitGraph.Pens[line.Color] : grayedPen;

                using (var ctx = geo.Open())
                {
                    var started = false;
                    var ended = false;
                    for (int i = 1; i < size; i++)
                    {
                        var cur = new Point(line.Points[i].X, line.Points[i].Y * rowHeight);
                        if (cur.Y < top)
                        {
                            last = cur;
                            continue;
                        }

                        if (!started)
                        {
                            ctx.BeginFigure(last, false);
                            started = true;
                        }

                        if (cur.Y > bottom)
                        {
                            var fraction = (bottom - last.Y) / (cur.Y - last.Y);
                            cur = new Point(last.X + (cur.X - last.X) * fraction, bottom);
                            ended = true;
                        }

                        ctx.LineTo(cur);

                        if (ended)
                            break;
                        last = cur;
                    }
                }

                context.DrawGeometry(null, pen, geo);
            }
        }

        private void DrawAnchors(DrawingContext context, Models.CommitGraph graph, double top, double bottom, double rowHeight)
        {
            var nodeOutline = new Pen(Brushes.White, 1);
            var grayedPen = new Pen(Brushes.Gray, Models.CommitGraph.Pens[0].Thickness);

            var dots = graph.Dots;
            for (var i = FindFirstVisible(dots, top, rowHeight, static dot => dot.Center.Y); i < dots.Count; ++i)
            {
                var dot = dots[i];
                var center = new Point(dot.Center.X, dot.Center.Y * rowHeight);
                if (center.Y > bottom)
                    break;

                var pen = dot.IsHighlighted ? Models.CommitGraph.Pens[dot.Color] : grayedPen;
                context.DrawEllipse(pen.Brush, nodeOutline, center, 4, 4);
            }
        }

        // Both graph builders append nodes and links in row order.
        internal static int FindFirstVisible<T>(List<T> items, double top, double rowHeight, Func<T, double> getY)
        {
            if (items.Count == 0 || getY(items[0]) * rowHeight >= top)
                return 0;

            var first = 1;
            var end = items.Count;
            while (first < end)
            {
                var middle = first + (end - first) / 2;
                if (getY(items[middle]) * rowHeight < top)
                    first = middle + 1;
                else
                    end = middle;
            }

            return first;
        }

        private Models.CommitGraph _graph = null;
        private Models.CommitGraphLayout _layout = null;
    }
}
