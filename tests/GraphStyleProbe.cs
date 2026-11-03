using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SourceGit.Tests
{
    internal static class GraphStyleProbe
    {
        public static int Run(string output)
        {
            try
            {
                App.BuildAvaloniaApp().SetupWithoutStarting();
                Models.CommitGraph.SetDefaultPens();
                var commits = new List<Models.Commit>
                {
                    new() { SHA = "a", Parents = ["b", "c"] },
                    new() { SHA = "b", Parents = ["d"] },
                    new() { SHA = "c", Parents = ["d"] },
                    new() { SHA = "d", Parents = ["e"] },
                    new() { SHA = "e" },
                };
                var graph = Models.CommitGraph.Generate(commits, false, Models.CommitGraphHighlighting.All, [], "a");
                var control = new Views.CommitGraph { Graph = graph, Layout = new(0, 80, 19), Width = 80, Height = 95 };
                var border = new Border { Background = Brushes.White, Child = control, Width = 80, Height = 95 };
                border.Measure(new Size(80, 95));
                border.Arrange(new Rect(0, 0, 80, 95));
                using var bitmap = new RenderTargetBitmap(new PixelSize(80, 95), new Vector(96, 96));
                bitmap.Render(border);
                bitmap.Save(output);
                File.WriteAllText(output + ".json", JsonSerializer.Serialize(graph.Dots.Select((d, i) => new
                {
                    sha = commits[i].SHA,
                    x = d.Center.X,
                    y = d.Center.Y * 19,
                    color = Models.CommitGraph.Pens[d.Color].Brush.ToString(),
                })));
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(output + ".failure", ex.ToString());
                return 1;
            }
        }
    }
}
