using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SourceGit.Models;

internal static class BinaryComparisonProbe
{
    private static int _passed;
    private static void Check(string name, bool ok)
    {
        if (!ok) throw new Exception(name);
        _passed++; Console.WriteLine("PASS " + name);
    }
    public static async Task Main()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TreeGit-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var oldFile = Path.Combine(dir, "old"); var newFile = Path.Combine(dir, "new");
            var random = new Random(71);
            foreach (var length in new[] { 0, 1, 65535, 65536, 65537, 196619 })
            {
                var a = new byte[length]; random.NextBytes(a);
                var b = a.ToArray();
                for (var i = 0; i < length; i++) if (random.Next(7) == 0) b[i] ^= 0x40;
                if (length > 65536) for (var i = 65530; i < Math.Min(length, 65545); i++) b[i] ^= 0x80;
                File.WriteAllBytes(oldFile, a); File.WriteAllBytes(newFile, b);
                using var c = new BinaryComparison(oldFile, newFile);
                await c.ScanAsync(CancellationToken.None);
                var differences = Enumerable.Range(0, length).Where(i => a[i] != b[i]).ToArray();
                Check("exact differing byte count " + length, c.DifferentBytes == differences.Length);
                long position = -1;
                var forward = differences.All(expected => (position = c.Navigate(position, 1, CancellationToken.None)) == expected);
                Check("all forward differing bytes " + length, forward && c.Navigate(position, 1, CancellationToken.None) == -1);
                position = -1;
                var backward = differences.Reverse().All(expected => (position = c.Navigate(position, -1, CancellationToken.None)) == expected);
                Check("all backward differing bytes " + length, backward && c.Navigate(position, -1, CancellationToken.None) == -1);
            }
            foreach (var sizes in new[] { (0, 10), (10, 0), (65540, 65530), (65530, 65540) })
            {
                File.WriteAllBytes(oldFile, new byte[sizes.Item1]); File.WriteAllBytes(newFile, new byte[sizes.Item2]);
                using var c = new BinaryComparison(oldFile, newFile); await c.ScanAsync(CancellationToken.None);
                Check("added/deleted/truncated counts " + sizes, c.DifferentBytes == Math.Abs(sizes.Item1 - sizes.Item2));
                Check("added/deleted/truncated navigation " + sizes, c.Navigate(-1, 1, CancellationToken.None) == Math.Min(sizes.Item1, sizes.Item2) && c.Navigate(-1, -1, CancellationToken.None) == Math.Max(sizes.Item1, sizes.Item2) - 1);
            }
            var sparse = new byte[4 * BinaryComparison.PageSize];
            var firstChange = BinaryComparison.PageSize - 2;
            var lastChange = 3 * BinaryComparison.PageSize + 2;
            File.WriteAllBytes(oldFile, sparse);
            sparse[firstChange] = 1;
            sparse[lastChange] = 1;
            File.WriteAllBytes(newFile, sparse);
            using (var c = new BinaryComparison(oldFile, newFile))
            {
                await c.ScanAsync(CancellationToken.None);
                var unchanged = 2 * BinaryComparison.PageSize;
                Check("navigation skips clean pages in both directions",
                    c.Navigate(unchanged, 1, CancellationToken.None) == lastChange &&
                    c.Navigate(unchanged, -1, CancellationToken.None) == firstChange &&
                    c.Navigate(firstChange, 1, CancellationToken.None) == lastChange &&
                    c.Navigate(lastChange, -1, CancellationToken.None) == firstChange);
            }
            var big = new byte[32 * 1024 * 1024]; File.WriteAllBytes(oldFile, big);
            for (var i = 0; i < big.Length; i += 2) big[i] = 1;
            File.WriteAllBytes(newFile, big);
            using (var c = new BinaryComparison(oldFile, newFile))
            {
                await c.ScanAsync(CancellationToken.None);
                Check("alternating differences have a bounded sparse index", c.IndexedPages == big.Length / BinaryComparison.PageSize && c.DifferentBytes == big.Length / 2);
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                try { await c.ScanAsync(cancel.Token); throw new Exception("Cancellation ignored"); }
                catch (OperationCanceledException) { Check("scan cancellation", true); }
            }
            Array.Fill<byte>(big, 2); File.WriteAllBytes(newFile, big);
            using (var c = new BinaryComparison(oldFile, newFile))
            {
                await c.ScanAsync(CancellationToken.None);
                Check("fully changed pages advance one byte per click",
                    c.Navigate(0, 1, CancellationToken.None) == 1 && c.Navigate(big.Length - 1, -1, CancellationToken.None) == big.Length - 2);
                Check("fully changed pages advance one byte across page boundaries",
                    c.Navigate(BinaryComparison.PageSize - 1, 1, CancellationToken.None) == BinaryComparison.PageSize &&
                    c.Navigate(BinaryComparison.PageSize, -1, CancellationToken.None) == BinaryComparison.PageSize - 1);
            }
            var owned = Path.Combine(dir, "owned"); Directory.CreateDirectory(owned);
            File.Copy(oldFile, Path.Combine(owned, "a")); File.Copy(newFile, Path.Combine(owned, "b"));
            using (var c = new BinaryComparison(Path.Combine(owned, "a"), Path.Combine(owned, "b"), owned))
                Check("random reads at a page boundary", c.Read(false, 65530, 20).Length == 20);
            Check("snapshot cleanup on dispose", !Directory.Exists(owned));
            Console.WriteLine("PASSED " + _passed);
        }
        finally { Directory.Delete(dir, true); }
    }
}
