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
        private readonly record struct Page(long Offset, int Length, int Count);

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
            if (Length == 0) return -1;
            if (current < 0 || current >= Length)
                return FindByte(direction > 0 ? 0 : Length - 1, direction, true, token);
            var same = FindByte(current, direction, false, token);
            return same < 0 ? -1 : FindByte(same, direction, true, token);
        }

        private long FindByte(long start, int direction, bool different, CancellationToken token)
        {
            var pageIndex = _pages.BinarySearch(new Page(start / PageSize * PageSize, 0, 0),
                Comparer<Page>.Create((a, b) => a.Offset.CompareTo(b.Offset)));
            if (pageIndex < 0) pageIndex = ~pageIndex;
            for (var pos = start; pos >= 0 && pos < Length;)
            {
                token.ThrowIfCancellationRequested();
                var pageOffset = pos / PageSize * PageSize;
                while (pageIndex > 0 && _pages[pageIndex - 1].Offset >= pageOffset) pageIndex--;
                while (pageIndex < _pages.Count && _pages[pageIndex].Offset < pageOffset) pageIndex++;
                var count = pageIndex < _pages.Count && _pages[pageIndex].Offset == pageOffset ? _pages[pageIndex].Count : 0;
                var length = (int)Math.Min(PageSize, Length - pageOffset);
                if ((different && count == 0) || (!different && count == length))
                {
                    if (different)
                    {
                        var next = direction > 0 ? pageIndex : pageIndex - 1;
                        if (next < 0 || next >= _pages.Count) return -1;
                        pos = _pages[next].Offset + (direction > 0 ? 0 : _pages[next].Length - 1);
                    }
                    else pos = direction > 0 ? pageOffset + length : pageOffset - 1;
                    continue;
                }
                if ((different && count == length) || (!different && count == 0)) return pos;
                var a = Read(true, pageOffset, PageSize);
                var b = Read(false, pageOffset, PageSize);
                for (var i = (int)(pos - pageOffset); i >= 0 && i < length; i += direction)
                    if (Different(a, b, i) == different) return pageOffset + i;
                pos = direction > 0 ? pageOffset + length : pageOffset - 1;
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
