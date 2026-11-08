using System;
using System.Text;

namespace SourceGit.Models
{
    // Keeps the most recent output, where Git normally reports the final error.
    // Callers synchronize access when stdout and stderr are received concurrently.
    public sealed class BoundedTextBuffer(int capacity)
    {
        public bool WasTruncated { get; private set; }
        public int Length => _text.Length;

        public void Append(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (text.Length >= _capacity)
            {
                WasTruncated |= _text.Length > 0 || text.Length > _capacity;
                var start = text.Length - _capacity;
                if (start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1])) start++;
                _text.Clear();
                _text.Append(text.AsSpan(start));
                return;
            }
            var overflow = _text.Length + text.Length - _capacity;
            if (overflow > 0)
            {
                WasTruncated = true;
                if (overflow < _text.Length && char.IsLowSurrogate(_text[overflow]) && char.IsHighSurrogate(_text[overflow - 1])) overflow++;
                _text.Remove(0, overflow);
            }
            _text.Append(text);
        }

        public void AppendLine(string line)
        {
            Append(line);
            Append(Environment.NewLine);
        }

        public override string ToString() => _text.ToString();

        private readonly int _capacity = capacity >= 16 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
        private readonly StringBuilder _text = new();
    }
}
