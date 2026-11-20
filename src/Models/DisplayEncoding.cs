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
        static DisplayEncoding()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Chinese = Encoding.GetEncoding(51936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
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

        public static int HeaderCodePage(ReadOnlySpan<byte> data)
            => data.StartsWith(Encoding.UTF8.Preamble) ? 65001 : WidePreamble(data);

        public static string Decode(ReadOnlySpan<byte> bytes, int codePage = 0)
        {
            if (codePage == 0) codePage = HeaderCodePage(bytes);
            if (codePage != 0)
            {
                var encoding = Encoding.GetEncoding(codePage);
                var preamble = encoding.Preamble;
                if (bytes.StartsWith(preamble)) bytes = bytes[preamble.Length..];
                return encoding.GetString(bytes);
            }
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

        private static string DecodeLegacyLine(ReadOnlySpan<byte> bytes)
        {
            // GB2312 uses two high bytes in the standard zones. Do not
            // treat wider GB encodings as another automatic candidate.
            for (var i = 0; i < bytes.Length; i++)
            {
                var first = bytes[i];
                if (first < 0x80) continue;
                if (first is < 0xA1 or > 0xF7 || ++i == bytes.Length || bytes[i] is < 0xA1 or > 0xFE)
                    return Encoding.UTF8.GetString(bytes);
            }
            try { return Chinese.GetString(bytes); }
            catch (DecoderFallbackException) { return Encoding.UTF8.GetString(bytes); }
        }
    }
}
