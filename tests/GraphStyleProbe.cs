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
                App.SetTheme("Light", null);
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
                var crowdedReports = new List<object>();
                foreach (var theme in new[] { "Light", "Dark", "Blue" })
                foreach (var compaction in new[] { false, true })
                {
                    App.SetTheme(theme, null);
                    var palette = Models.CommitGraph.Pens.Select(p => ((ISolidColorBrush)p.Brush).Color).ToArray();
                    var primary = palette[0].ToHsl().H;
                    if (palette.Skip(1).Any(color =>
                    {
                        var difference = Math.Abs(color.ToHsl().H - primary);
                        return Math.Min(difference, 360 - difference) < 45;
                    })) throw new InvalidOperationException(theme + ": a secondary lane uses the primary branch's hue family.");
                    var crowded = new List<Models.Commit> { new() { SHA = "main", Parents = ["main-tail"] } };
                    for (var i = 0; i < 24; i++) crowded.Add(new() { SHA = $"side-{i}", Parents = [compaction ? $"tail-{i}" : "root"] });
                    crowded.Add(new() { SHA = "main-tail", Parents = ["root"] });
                    if (compaction)
                        foreach (var i in Enumerable.Range(0, 24).OrderBy(i => i % 2).ThenByDescending(i => i))
                            crowded.Add(new() { SHA = $"tail-{i}" });
                    crowded.Add(new() { SHA = "root" });
                    var layout = Models.CommitGraph.Generate(crowded, false, Models.CommitGraphHighlighting.All, [], "main");
                    if (crowded.Where((c, i) => c.Color != layout.Dots[i].Color).Any())
                        throw new InvalidOperationException("Commit labels and graph nodes have different colors.");
                    var pairs = new List<object>();
                    for (var row = 1; row < crowded.Count - 1; row++)
                    {
                        var y = row + .95;
                        var columns = new SortedDictionary<double, int>();
                        foreach (var path in layout.Paths)
                            for (var i = 1; i < path.Points.Count; i++)
                            {
                                var a = path.Points[i - 1]; var b = path.Points[i];
                                if (a.Y <= y && b.Y > y && b.Y > a.Y)
                                    columns[Math.Round(a.X + (b.X - a.X) * (y - a.Y) / (b.Y - a.Y), 3)] = path.Color;
                            }
                        var ordered = columns.ToArray();
                        for (var i = 1; i < ordered.Length; i++)
                        {
                            if (ordered[i - 1].Value == ordered[i].Value)
                                throw new InvalidOperationException($"{theme}: adjacent crowded lanes share a color at row {row}.");
                            pairs.Add(new { row, left = ordered[i - 1].Value, right = ordered[i].Value });
                        }
                    }
                    crowdedReports.Add(new { theme, compaction, adjacentPairsChecked = pairs.Count, pairs,
                        palette = Models.CommitGraph.Pens.Select(p => p.Brush.ToString()).ToArray() });
                }
                File.WriteAllText(output + ".crowded.json", JsonSerializer.Serialize(new { passed = crowdedReports.Count, themes = crowdedReports }));
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
