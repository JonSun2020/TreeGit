using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    // Immutable snapshots, bounded reads and a sparse page index keep large firmware
    // comparisons off the UI thread without allocating one object per changed byte.
    public sealed class BinaryComparison : IDisposable
    {
        public const int PageSize = 65536;
        public long OldSize { get; }
        public long NewSize { get; }
        public long Length => Math.Max(OldSize, NewSize);
        public long DifferentBytes { get; private set; }
        public int IndexedPages => _pages.Count;
        internal string SnapshotDirectory => _directory;

        private readonly FileStream _old;
        private readonly FileStream _new;
        private readonly string _directory;
        private List<Page> _pages = [];
        private PageBytes _navigationPage;
        private readonly record struct Page(long Offset, int Length, int Count);
        private sealed record PageBytes(long Offset, byte[] Old, byte[] New);
        private static readonly Comparer<Page> PageOrder = Comparer<Page>.Create((a, b) => a.Offset.CompareTo(b.Offset));

        public BinaryComparison(string oldFile, string newFile, string ownedDirectory = null)
        {
            _directory = ownedDirectory;
            _old = File.OpenRead(oldFile);
            try
            {
                OldSize = _old.Length;
                _new = File.OpenRead(newFile);
                NewSize = _new.Length;
            }
            catch { _new?.Dispose(); _old.Dispose(); throw; }
        }

        public byte[] Read(bool old, long offset, int count)
        {
            count = (int)Math.Min(Math.Max(0, (old ? OldSize : NewSize) - offset), count);
            var bytes = new byte[count];
            var read = ReadInto(old ? _old : _new, offset, bytes);
            return read == count ? bytes : bytes[..read];
        }

        private static int ReadInto(FileStream stream, long offset, Span<byte> bytes)
        {
            var read = 0;
            while (read < bytes.Length)
            {
                var n = RandomAccess.Read(stream.SafeFileHandle, bytes[read..], offset + read);
                if (n == 0) break;
                read += n;
            }
            return read;
        }

        public Task ScanAsync(CancellationToken token) => Task.Run(() =>
        {
            var pages = new List<Page>();
            var oldBuffer = new byte[PageSize];
            var newBuffer = new byte[PageSize];
            var total = 0L;
            var length = Length;
            for (long offset = 0; offset < length; offset += PageSize)
            {
                token.ThrowIfCancellationRequested();
                var oldCount = (int)Math.Clamp(OldSize - offset, 0, PageSize);
                var newCount = (int)Math.Clamp(NewSize - offset, 0, PageSize);
                var a = oldBuffer.AsSpan(0, ReadInto(_old, offset, oldBuffer.AsSpan(0, oldCount)));
                var b = newBuffer.AsSpan(0, ReadInto(_new, offset, newBuffer.AsSpan(0, newCount)));
                var count = CountDifferences(a, b);
                if (count > 0) pages.Add(new Page(offset, Math.Max(a.Length, b.Length), count));
                total += count;
            }
            token.ThrowIfCancellationRequested();
            _pages = pages;
            DifferentBytes = total;
        }, token);

        private static int CountDifferences(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            var common = Math.Min(a.Length, b.Length);
            var count = Math.Abs(a.Length - b.Length);
            if (a[..common].SequenceEqual(b[..common])) return count;

            var i = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                var width = Vector128<byte>.Count;
                for (; i <= common - width; i += width)
                {
                    var matches = Vector128.Equals(Vector128.Create(a[i..]), Vector128.Create(b[i..]));
                    count += width - BitOperations.PopCount(matches.ExtractMostSignificantBits());
                }
            }
            for (; i < common; i++)
                if (a[i] != b[i]) count++;
            return count;
        }

        public long Navigate(long current, int direction, CancellationToken token)
        {
            direction = direction < 0 ? -1 : 1;
            var start = current >= 0 && current < Length
                ? current + direction
                : direction > 0 ? 0 : Length - 1;
            if (start < 0 || start >= Length) return -1;

            var pageIndex = _pages.BinarySearch(new Page(start / PageSize * PageSize, 0, 0), PageOrder);
            if (pageIndex < 0)
                pageIndex = ~pageIndex + (direction > 0 ? 0 : -1);

            for (; pageIndex >= 0 && pageIndex < _pages.Count; pageIndex += direction)
            {
                token.ThrowIfCancellationRequested();
                var page = _pages[pageIndex];
                var first = (int)Math.Clamp(start - page.Offset, 0, page.Length - 1);
                if (page.Count == page.Length) return page.Offset + first;

                // A sequence of nearby navigation requests reuses one immutable page.
                var bytes = Volatile.Read(ref _navigationPage);
                if (bytes == null || bytes.Offset != page.Offset)
                {
                    bytes = new PageBytes(page.Offset, Read(true, page.Offset, page.Length), Read(false, page.Offset, page.Length));
                    Volatile.Write(ref _navigationPage, bytes);
                }
                for (var i = first; i >= 0 && i < page.Length; i += direction)
                    if (Different(bytes.Old, bytes.New, i)) return page.Offset + i;
            }
            return -1;
        }

        public static bool Different(byte[] a, byte[] b, int i) => i >= a.Length || i >= b.Length || a[i] != b[i];

        public void Dispose()
        {
            _old.Dispose();
            _new.Dispose();
            if (_directory != null)
            {
                try { Directory.Delete(_directory, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
