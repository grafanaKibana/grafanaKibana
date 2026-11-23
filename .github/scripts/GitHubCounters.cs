using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class GitHubCounters
{
    internal static async Task RefreshAsync(HttpClient http)
    {
        string owner = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY_OWNER")
            ?? throw new InvalidOperationException("GITHUB_REPOSITORY_OWNER is required.");
        string userUrl = $"https://api.github.com/users/{Uri.EscapeDataString(owner)}";
        if (Environment.GetEnvironmentVariable("GH_TOKEN") is { Length: > 0 } token)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        JsonElement user = await http.GetFromJsonAsync<JsonElement>(userUrl);
        long followers = user.GetProperty("followers").GetInt64();
        long stars = 0;
        for (var page = 1; ; page++)
        {
            JsonElement repos = await http.GetFromJsonAsync<JsonElement>(
                $"{userUrl}/repos?type=owner&per_page=100&page={page}");
            foreach (JsonElement repo in repos.EnumerateArray())
            {
                if (!repo.GetProperty("fork").GetBoolean() && !repo.GetProperty("private").GetBoolean())
                {
                    stars += repo.GetProperty("stargazers_count").GetInt64();
                }
            }

            if (repos.GetArrayLength() < 100)
            {
                break;
            }
        }

        const string Asset = "assets/hero/profile-stats.svg";
        string original = await File.ReadAllTextAsync(Asset);
        string updated = ReplaceCount(ReplaceCount(original, "followers", followers), "stars", stars);
        if (updated != original)
        {
            await File.WriteAllTextAsync(Asset, updated);
        }
    }

    private static string ReplaceCount(string svg, string name, long count)
    {
        var marker = new Regex($"""(?<=data-count="{name}">)[^<]*(?=</span>)""");
        if (marker.Count(svg) != 1)
        {
            throw new InvalidDataException($"Expected one {name} counter.");
        }

        if (count < 1000)
        {
            return marker.Replace(svg, count.ToString());
        }

        (int scale, string suffix) = count >= 1_000_000 ? (1_000_000, "M") : (1000, "k");
        string value = (count / (double)scale).ToString("F1");
        return marker.Replace(svg, (value.EndsWith(".0") ? value[..^2] : value) + suffix);
    }
}
