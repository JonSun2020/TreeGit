using System;
using System.Collections.Generic;
using System.Text;

namespace SourceGit.Models
{
    public sealed record MergeConflictEncoding(int CodePage, string Name)
    {
        public static readonly IReadOnlyList<MergeConflictEncoding> Choices =
        [
            new(65001, "UTF-8"), new(936, "GBK"), new(54936, "GB18030"), new(1252, "Windows-1252"),
            new(1200, "UTF-16 LE"), new(1201, "UTF-16 BE"), new(12000, "UTF-32 LE"), new(12001, "UTF-32 BE"),
        ];
    }

    public sealed class MergeConflictTextFile
    {
        public const int MaxBytes = 8 * 1024 * 1024;
        public string Text { get; }
        public int CodePage => _encoding.CodePage;
        public bool HasPreamble => _preamble.Length > 0;

        static MergeConflictTextFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        private MergeConflictTextFile(byte[] bytes, int codePage, int prefix)
        {
            _encoding = StrictEncoding(codePage);
            _preamble = bytes.AsSpan(0, prefix).ToArray();
            Text = _encoding.GetString(bytes, prefix, bytes.Length - prefix);
            if (Text.IndexOf('\0') >= 0) throw new FormatException("Not a text file");
            if (!Encode(Text).AsSpan().SequenceEqual(bytes)) throw new FormatException("Encoding does not round-trip");
        }

        private MergeConflictTextFile(Encoding encoding, byte[] preamble) { _encoding = encoding; _preamble = preamble; }

        public static MergeConflictTextFile Read(byte[] bytes, int? codePage = null)
        {
            if (bytes.Length > MaxBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
            var (detected, prefix) = Preamble(bytes);
            if (detected != 0) return new(bytes, detected, prefix);
            if (codePage.HasValue) return new(bytes, codePage.Value, 0);
            foreach (var candidate in new[] { 65001, 936, 54936, 1252 })
            {
                try { return new(bytes, candidate, 0); }
                catch (DecoderFallbackException) { }
                catch (EncoderFallbackException) { }
                catch (FormatException) { }
            }
            throw new FormatException("Not a text file");
        }

        public MergeConflictTextFile ConvertTo(int codePage)
        {
            if (codePage == CodePage) return this;
            var encoding = StrictEncoding(codePage);
            byte[] prefix = codePage is 1200 or 1201 or 12000 or 12001 ? encoding.GetPreamble() : [];
            return new(encoding, prefix);
        }

        public byte[] Encode(string text)
        {
            var count = _encoding.GetByteCount(text);
            if (count + _preamble.Length > MaxBytes) throw new ArgumentOutOfRangeException(nameof(text));
            var result = new byte[count + _preamble.Length];
            _preamble.CopyTo(result, 0);
            _encoding.GetBytes(text, 0, text.Length, result, _preamble.Length);
            return result;
        }

        private static Encoding StrictEncoding(int codePage) => Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        private static (int, int) Preamble(byte[] bytes)
        {
            if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) return (12000, 4);
            if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) return (12001, 4);
            if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return (65001, 3);
            if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return (1200, 2);
            if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return (1201, 2);
            return (0, 0);
        }

        private readonly Encoding _encoding;
        private readonly byte[] _preamble;
    }
}
