using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace SourceGit.Views
{
    // Draw both pieces in the same 24px coordinate system, avoiding Shape stretching
    // each path to its own bounds (which offsets the fetch arrow inside its circle).
    public sealed class ToolbarGlyph : Control
    {
        public string Kind { get; set; } = "Fetch";

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var scale = System.Math.Min(Bounds.Width, Bounds.Height) / 24;
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - scale * 24) / 2, (Bounds.Height - scale * 24) / 2)))
            {
                var brush = this.FindResource("Brush.TreeGit.ToolbarOutline") as IBrush ?? Brushes.Teal;
                var pen = new Pen(brush, 1.3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                if (Kind == "Fetch")
                {
                    context.DrawEllipse(null, new Pen(brush, 1.3, new DashStyle([3, 2], 0)), new Point(12, 12), 10.5, 10.5);
                    context.DrawLine(pen, new Point(12, 5), new Point(12, 18));
                    context.DrawLine(pen, new Point(7, 13), new Point(12, 18));
                    context.DrawLine(pen, new Point(12, 18), new Point(17, 13));
                }
                else
                {
                    context.DrawGeometry(null, pen, Geometry.Parse("M19.8,5.8 A10,10 0 1 0 21.7,14.3 M19.8,5.8 L14.6,5.8 M19.8,5.8 L19.8,0.6"));
                }
            }
        }
    }
}
