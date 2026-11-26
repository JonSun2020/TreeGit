using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SourceGit.Models
{
    public class User
    {
        public static readonly User Invalid = new User(string.Empty);

        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;

        public User(string data)
        {
            var separator = data.IndexOf('±');
            if (separator < 0)
            {
                Email = data;
            }
            else
            {
                Name = data[..separator];
                Email = data[(separator + 1)..];
            }
        }

        public static User FindOrAdd(ReadOnlySpan<char> data)
        {
            var cache = Volatile.Read(ref _cache);
            if (cache.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(data, out var user))
                return user;

            // Open views own their users. Interning only needs to retain recent identities.
            if (cache.Count >= MaxCachedUsers)
            {
                Interlocked.CompareExchange(ref _cache, new(StringComparer.Ordinal), cache);
                cache = Volatile.Read(ref _cache);
            }

            return cache.GetOrAdd(data.ToString(), static key => new User(key));
        }

        public override string ToString()
        {
            return $"{Name} <{Email}>";
        }

        private const int MaxCachedUsers = 4096;
        private static ConcurrentDictionary<string, User> _cache = new(StringComparer.Ordinal);
    }
}
