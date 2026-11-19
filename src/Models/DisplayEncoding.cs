using System;
using System.Text;
using System.Text.Unicode;

namespace SourceGit.Models
{
    // These decoders produce display text only. Git patches keep their original bytes.
    public static class DisplayEncoding
    {
        private static readonly int[] CodePages = [0, 65001, 1200, 1201, 12000, 12001, 51936, 936, 54936, 950, 1252];
        private static readonly Encoding Chinese;
        private static readonly Encoding Western;
        static DisplayEncoding()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Chinese = Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            Western = Encoding.GetEncoding(1252);
        }

        public static int CodePage(int index) => (uint)index < CodePages.Length ? CodePages[index] : 0;
        public static bool IsWide(int codePage) => codePage is 1200 or 1201 or 12000 or 12001;
        public static bool IsReadableText(string text)
        {
            foreach (var c in text)
                if (char.IsControl(c) && c is not ('\t' or '\n' or '\r' or '\f')) return false;
            return true;
        }

        public static int WidePreamble(ReadOnlySpan<byte> data)
        {
            return data switch
            {
                [0xff, 0xfe, 0, 0, ..] => 12000,
                [0, 0, 0xfe, 0xff, ..] => 12001,
                [0xff, 0xfe, ..] => 1200,
                [0xfe, 0xff, ..] => 1201,
                _ => 0,
            };
        }

        public static int DetectWide(ReadOnlySpan<byte> data)
        {
            var marked = WidePreamble(data);
            if (marked != 0) return marked;
            // Look for ASCII code units, including whitespace, on consistent byte
            // boundaries. A single NUL is insufficient evidence for Unicode.
            var count = Math.Min(data.Length, 4096);
            if (count < 4 || !data[..count].Contains((byte)0)) return 0;
            foreach (var cp in WideCandidates)
            {
                var width = cp is 12000 or 12001 ? 4 : 2;
                var ascii = cp is 1200 or 12000 ? 0 : width - 1;
                var matches = 0;
                var units = count / width;
                for (var i = 0; i < units * width; i += width)
                {
                    var ch = data[i + ascii];
                    if (!(ch is 9 or 10 or 13 or >= 32 and <= 126)) continue;
                    var expected = true;
                    for (var j = 0; j < width; j++) if (j != ascii && data[i + j] != 0) expected = false;
                    if (expected) matches++;
                }
                if (matches >= 2 && matches * 3 >= units) return cp;
            }
            return 0;
        }

        public static string Decode(ReadOnlySpan<byte> bytes, int codePage = 0)
        {
            if (codePage == 0) codePage = DetectWide(bytes);
            if (codePage != 0)
            {
                var encoding = Encoding.GetEncoding(codePage);
                var preamble = encoding.Preamble;
                if (bytes.StartsWith(preamble)) bytes = bytes[preamble.Length..];
                return encoding.GetString(bytes);
            }
            if (bytes.StartsWith(Encoding.UTF8.Preamble)) bytes = bytes[Encoding.UTF8.Preamble.Length..];
            return DecodeNarrow(bytes);
        }

        public static string DecodeNarrow(ReadOnlySpan<byte> bytes)
        {
            if (Utf8.IsValid(bytes)) return Encoding.UTF8.GetString(bytes);
            if (!bytes.TrimEnd("\r\n"u8).ContainsAny((byte)'\r', (byte)'\n'))
                return DecodeLegacyLine(bytes);

            var text = new StringBuilder(bytes.Length);
            while (!bytes.IsEmpty)
            {
                var end = bytes.IndexOfAny((byte)'\r', (byte)'\n');
                var length = end < 0 ? bytes.Length : end + 1;
                if (length < bytes.Length && bytes[end] == '\r' && bytes[length] == '\n') length++;
                var line = bytes[..length];
                text.Append(Utf8.IsValid(line) ? Encoding.UTF8.GetString(line) : DecodeLegacyLine(line));
                bytes = bytes[length..];
            }
            return text.ToString();
        }

        private static readonly int[] WideCandidates = [12000, 12001, 1200, 1201];

        private static string DecodeLegacyLine(ReadOnlySpan<byte> bytes)
        {
            // A GB character can contain a valid UTF-8 sequence. Keep the whole
            // line together, including when falling back to the Western code page.
            try { return Chinese.GetString(bytes); }
            catch (DecoderFallbackException) { return Western.GetString(bytes); }
        }
    }
}
