using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Media;

namespace SourceGit.Models
{
    public partial class CommitGraph
    {
        // Keep identity separate from the limited display palette. Adjacency is
        // collected across the entire history, including later lane compaction.
        private readonly List<HashSet<int>> _branchNeighbors = [];
        private readonly List<(Commit Commit, int Branch)> _branchCommits = [];
        private int _colorVersion = -1;

        private void RecordOrderedNeighbors(IEnumerable<int> branches)
        {
            var previous = -1;
            foreach (var branch in branches.Distinct())
            {
                if (previous >= 0)
                {
                    _branchNeighbors[previous].Add(branch);
                    _branchNeighbors[branch].Add(previous);
                }
                previous = branch;
            }
        }

        public void UpdatePalette()
        {
            var version = System.Threading.Volatile.Read(ref s_paletteVersion);
            if (_branchNeighbors.Count == 0 || _colorVersion == version)
                return;
            var colors = Pens.Select(p => ((ISolidColorBrush)p.Brush).Color).ToArray();
            if (colors.Length == 0)
                return;
            var assigned = Enumerable.Repeat(-1, _branchNeighbors.Count).ToArray();
            assigned[0] = 0;
            var choices = Enumerable.Range(colors.Length > 1 ? 1 : 0, Math.Max(1, colors.Length - 1)).ToArray();
            var differences = colors.Length <= 256 ? new double[colors.Length, colors.Length] : null;
            if (differences != null)
                for (var a = 0; a < colors.Length; a++)
                    for (var b = 0; b < colors.Length; b++) differences[a, b] = ColorDifference(colors[a], colors[b]);
            double Difference(int a, int b) => differences == null ? ColorDifference(colors[a], colors[b]) : differences[a, b];
            var used = new HashSet<int> { 0 };
            double NeighborDistance(int branch, int candidate)
            {
                var distance = double.PositiveInfinity;
                foreach (var neighbor in _branchNeighbors[branch])
                    if (assigned[neighbor] >= 0)
                        distance = Math.Min(distance, Difference(candidate, assigned[neighbor]));
                return double.IsPositiveInfinity(distance) ? 180 : distance;
            }
            int Choose(int branch)
            {
                double Spread(int candidate) => used.Min(c => Difference(candidate, c));
                var free = choices.Where(c => !used.Contains(c)).ToArray();
                var fresh = free.Length == 0 ? -1 : free.MaxBy(c => NeighborDistance(branch, c) * 4 + Spread(c));
                if (fresh >= 0 && NeighborDistance(branch, fresh) >= 110)
                    return fresh;
                return choices.MaxBy(c => NeighborDistance(branch, c) * 4 + Spread(c));
            }
            // Preserve a wide triad for the first three branches.
            for (var branch = 1; branch < Math.Min(3, assigned.Length); branch++)
            {
                assigned[branch] = Math.Min(branch, colors.Length - 1);
                used.Add(assigned[branch]);
            }
            // Maintain saturation incrementally. Re-sorting every uncolored
            // branch at every step made long histories quadratic even when only
            // two lanes were visible at once.
            var saturation = new HashSet<int>[assigned.Length];
            var pending = new PriorityQueue<int, (int Saturation, int Degree, int Identity)>();
            for (var branch = 3; branch < assigned.Length; branch++)
            {
                saturation[branch] = _branchNeighbors[branch].Where(n => assigned[n] >= 0).Select(n => assigned[n]).ToHashSet();
                pending.Enqueue(branch, (-saturation[branch].Count, -_branchNeighbors[branch].Count, branch));
            }
            while (pending.TryDequeue(out var next, out var priority))
            {
                if (assigned[next] >= 0 || priority.Saturation != -saturation[next].Count) continue;
                assigned[next] = Choose(next);
                used.Add(assigned[next]);
                foreach (var neighbor in _branchNeighbors[next])
                    if (assigned[neighbor] < 0 && saturation[neighbor].Add(assigned[next]))
                        pending.Enqueue(neighbor, (-saturation[neighbor].Count, -_branchNeighbors[neighbor].Count, neighbor));
            }
            // Improve tight pairs without moving any line or changing identity.
            for (var pass = 0; pass < 3; pass++)
                for (var branch = 3; branch < assigned.Length; branch++)
                {
                    var current = NeighborDistance(branch, assigned[branch]);
                    if (current >= 110) continue;
                    var best = choices.MaxBy(c => NeighborDistance(branch, c));
                    if (NeighborDistance(branch, best) > current + .01) assigned[branch] = best;
                }
            foreach (var path in Paths)
                if (path.Branch >= 0) path.Color = assigned[path.Branch];
            foreach (var dot in Dots)
                if (dot.Branch >= 0) dot.Color = assigned[dot.Branch];
            foreach (var entry in _branchCommits)
                entry.Commit.Color = assigned[entry.Branch];
            _colorVersion = version;
        }
    }
}
