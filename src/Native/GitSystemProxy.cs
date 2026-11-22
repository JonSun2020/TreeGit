using System;
using System.Diagnostics;
using System.Net.Http;

namespace SourceGit.Native
{
    internal static class GitSystemProxy
    {
        internal static bool IsHttpUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Scheme is "http" or "https";

        internal static void Configure(ProcessStartInfo start, string url)
        {
            if (!OperatingSystem.IsWindows() ||
                !Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
                return;

            var variable = address.Scheme + "_proxy";
            if (start.Environment.ContainsKey(variable) || start.Environment.ContainsKey("all_proxy"))
                return;

            // Resolve the route for this URL through Windows, including its bypass list.
            // Keep the result in the child environment so Git's explicit proxy settings win.
            if (!string.IsNullOrEmpty(address.UserInfo))
                address = new UriBuilder(address) { UserName = "", Password = "" }.Uri;

            Uri proxy;
            try { proxy = HttpClient.DefaultProxy.GetProxy(address); }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return;
            }

            if (proxy != null && proxy != address)
                start.Environment[variable] = proxy.AbsoluteUri;
        }
    }
}
