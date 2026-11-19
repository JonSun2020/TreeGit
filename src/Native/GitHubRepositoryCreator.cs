using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Native
{
    internal sealed class GitHubRepositoryCreator : IDisposable
    {
        internal sealed record Repository(string Owner, string Name)
        {
            public string CloneUrl => $"https://github.com/{Owner}/{Name}.git";
            public string BrowserUrl => $"https://github.com/{Owner}/{Name}";
        }

        internal sealed class Failure(string key, bool uncertain = false) : Exception(App.Text(key))
        {
            public string Key { get; } = key;
            public bool Uncertain { get; } = uncertain;
        }

        private readonly HttpClient _client;

        internal GitHubRepositoryCreator(HttpMessageHandler handler = null)
        {
            _client = handler == null
                ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                : new HttpClient(handler);
            _client.Timeout = TimeSpan.FromSeconds(30);
        }

        public async Task<string> GetAccountAsync(string token)
        {
            using var doc = await SendAsync(HttpMethod.Get, "user", token);
            var login = doc.RootElement.GetProperty("login").GetString();
            if (!SafeSegment(login)) throw new Failure("TreeGit.CreateRepository.InvalidResponse");
            return login;
        }

        public async Task<Repository> FindAsync(string token, string owner, string name)
        {
            using var doc = await SendAsync(HttpMethod.Get, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}", token, allowNotFound: true);
            return doc == null ? null : ParseRepository(doc, owner, name);
        }

        public async Task<Repository> CreateAsync(string token, string owner, string name, bool isPrivate)
        {
            using var doc = await SendAsync(HttpMethod.Post, "user/repos", token,
                JsonSerializer.Serialize(new { name, @private = isPrivate, auto_init = false }));
            try { return ParseRepository(doc, owner, name); }
            catch { throw new Failure("TreeGit.CreateRepository.InvalidResponse", true); }
        }

        private static bool SafeSegment(string value)
            => !string.IsNullOrEmpty(value) && value.Length <= 100 &&
                System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[A-Za-z0-9_.-]+\z") && value is not ("." or "..");

        private static Repository ParseRepository(JsonDocument doc, string owner, string name)
        {
            var root = doc.RootElement;
            var actualOwner = root.GetProperty("owner").GetProperty("login").GetString();
            var actualName = root.GetProperty("name").GetString();
            if (!SafeSegment(actualOwner) || !SafeSegment(actualName) ||
                !string.Equals(actualOwner, owner, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actualName, name, StringComparison.OrdinalIgnoreCase))
                throw new Failure("TreeGit.CreateRepository.InvalidResponse");
            return new Repository(actualOwner, actualName);
        }

        private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string token, string body = null, bool allowNotFound = false)
        {
            var mutating = method == HttpMethod.Post;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var request = new HttpRequestMessage(method, new Uri("https://api.github.com/" + path));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.UserAgent.ParseAdd("TreeGit");
                request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
                if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return null;
                if (!response.IsSuccessStatusCode)
                {
                    var limited = (int)response.StatusCode == 429 || response.StatusCode == HttpStatusCode.Forbidden && await IsRateLimitedAsync(response, timeout.Token);
                    var key = limited ? "TreeGit.CreateRepository.RateLimited" : response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => "TreeGit.CreateRepository.AuthenticationFailed",
                        HttpStatusCode.Forbidden => "TreeGit.CreateRepository.PermissionDenied",
                        HttpStatusCode.UnprocessableEntity => "TreeGit.CreateRepository.RemoteRejected",
                        _ => "TreeGit.CreateRepository.ConnectionFailed",
                    };
                    throw new Failure(key, mutating && (int)response.StatusCode >= 500);
                }
                return await ReadDocumentAsync(response.Content, timeout.Token, mutating);
            }
            catch (Failure) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                // Do not automatically replay a creation after a lost response.
                throw new Failure("TreeGit.CreateRepository.ConnectionFailed", mutating);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                throw new Failure("TreeGit.CreateRepository.InvalidResponse", mutating);
            }
        }

        private static async Task<bool> IsRateLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.Headers.RetryAfter != null) return true;
            if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining))
                foreach (var value in remaining)
                    if (value.Trim() == "0") return true;
            try
            {
                // Secondary limits can omit both headers. Keep error bodies as
                // bounded as successful responses, without exposing server text.
                using var doc = await ReadDocumentAsync(response.Content, cancellationToken, false);
                return doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String &&
                    message.GetString().Contains("rate limit", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is Failure or JsonException or HttpRequestException or OperationCanceledException or IOException)
            {
                // A malformed error body must not hide the authoritative status.
                return false;
            }
        }

        private static async Task<JsonDocument> ReadDocumentAsync(HttpContent content, CancellationToken cancellationToken, bool mutating)
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > 65536) throw new Failure("TreeGit.CreateRepository.InvalidResponse", mutating);
                buffer.Write(chunk, 0, read);
            }
            return JsonDocument.Parse(buffer.ToArray());
        }

        public void Dispose() => _client.Dispose();
    }
}
