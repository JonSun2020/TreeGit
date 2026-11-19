using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Automation
{
    // Mock only GitHub HTTP. Folder creation, Git init, remote config and GUI
    // commands still use the real application in an owned, anonymous fixture.
    internal static class RepositoryCreationProbe
    {
        private static int _scenario;
        private static int _posts;
        private static bool _exists;
        private static bool _private;
        private static string _name;

        internal static object Configure(int scenario)
        {
            if (Environment.GetEnvironmentVariable("CI") != "true") throw new InvalidOperationException("An isolated CI fixture is required.");
            _scenario = scenario; _posts = 0; _exists = scenario == 5; _private = false; _name = null;
            ViewModels.CreateRepository.GitHubTestFactory = () => new Native.GitHubRepositoryCreator(new Handler());
            return State();
        }

        internal static object State() => new { scenario = _scenario, posts = _posts, exists = _exists, isPrivate = _private, name = _name, network = "mock-only" };

        private sealed class Handler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri.Host != "api.github.com" || request.Headers.Authorization?.Parameter != "fixture-token")
                    throw new InvalidOperationException("Unexpected fixture request.");
                HttpResponseMessage Reply(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (request.RequestUri.AbsolutePath == "/user")
                {
                    if (_scenario == 7) await Task.Delay(800, cancellationToken);
                    if (_scenario is >= 8 and <= 10)
                    {
                        var limited = Reply(HttpStatusCode.Forbidden, _scenario == 10
                            ? "{\"message\":\"You have exceeded a secondary rate limit. Please wait a few minutes before you try again.\"}"
                            : "{}");
                        if (_scenario == 8) limited.Headers.Add("x-ratelimit-remaining", "0");
                        if (_scenario == 9) limited.Headers.Add("retry-after", "60");
                        return limited;
                    }
                    return _scenario == 1 ? Reply(HttpStatusCode.Unauthorized, "{}") : Reply(HttpStatusCode.OK, "{\"login\":\"treegit-demo\"}");
                }
                string Repository() => JsonSerializer.Serialize(new { name = _name ?? request.RequestUri.Segments[^1], owner = new { login = "treegit-demo" } });
                if (request.Method == HttpMethod.Get)
                    return _exists ? Reply(HttpStatusCode.OK, Repository()) : Reply(HttpStatusCode.NotFound, "{}");
                _posts++;
                using var payload = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                _name = payload.RootElement.GetProperty("name").GetString();
                _private = payload.RootElement.GetProperty("private").GetBoolean();
                if (payload.RootElement.GetProperty("auto_init").GetBoolean()) throw new InvalidOperationException("Do not invent an initial commit.");
                if (_scenario == 2) return Reply(HttpStatusCode.Forbidden, "{}");
                if (_scenario == 4) throw new HttpRequestException("Fixture offline");
                _exists = true;
                if (_scenario == 3) throw new HttpRequestException("Fixture lost successful response");
                if (_scenario == 6)
                {
                    var model = App.GetLauncher().ActivePage.Popup as ViewModels.CreateRepository;
                    if (model == null || !model.ParentFolder.Contains("TreeGit creation fixture ", StringComparison.Ordinal))
                        throw new InvalidOperationException("Owned creation fixture required.");
                    File.WriteAllText(Path.Combine(model.ParentFolder, model.Name), "preserve blocker");
                }
                return Reply(HttpStatusCode.Created, Repository());
            }
        }
    }
}
