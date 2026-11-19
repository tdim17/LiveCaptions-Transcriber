using System.Net.Http;
using System.Text.Json;

namespace LiveCaptionsTranscriber.utils
{
    public static class UpdateUtil
    {
        public const string GitHubRepoUrl = "https://github.com/tdim17/LiveCaptions-Transcriber";
        public const string GitHubReleasesUrl = "https://github.com/tdim17/LiveCaptions-Transcriber/releases";
        public const string GitHubLatestReleaseApi = "https://api.github.com/repos/tdim17/LiveCaptions-Transcriber/releases/latest";

        public static async Task<string> GetLatestVersion()
        {
            string apiUrl = GitHubLatestReleaseApi;

            using var client = new HttpClient()
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveCaptions-Transcriber");
            using var response = await client.GetAsync(apiUrl);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return string.Empty;

            response.EnsureSuccessStatusCode();
            string responseBody = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);
            var latestVersionRaw = doc.RootElement.GetProperty("tag_name").GetString();
            var latestVersion = string.IsNullOrEmpty(latestVersionRaw)
                ? String.Empty
                : RegexPatterns.VersionNumber().Replace(latestVersionRaw, "");
            return latestVersion;
        }


    }
}
