using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Tests
{
    internal sealed class UpdateStartupHttpHandler(string responsePath) : HttpMessageHandler
    {
        internal static HttpClient CreateClient()
        {
            var response = Environment.GetEnvironmentVariable("TREEGIT_TEST_UPDATE_RESPONSE_FILE");
            return string.IsNullOrEmpty(response) ? null : new HttpClient(new UpdateStartupHttpHandler(response));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            File.AppendAllText(responsePath + ".requests", request.RequestUri + Environment.NewLine);
            var response = File.ReadAllText(responsePath);
            return Task.FromResult(response == "offline"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
        }
    }
}
