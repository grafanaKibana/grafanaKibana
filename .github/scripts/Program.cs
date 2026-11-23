using System.Globalization;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("github-profile-assets/1.0");

switch (args)
{
    case ["counters"]:
        await GitHubCounters.RefreshAsync(http);
        break;
    case ["covers"]:
        await PortfolioCovers.RefreshAsync(http);
        break;
    case ["tokscale"]:
        await Tokscale.RefreshAsync(http);
        break;
    default:
        Console.Error.WriteLine("Usage: ProfileAssets <counters|covers|tokscale>");
        return 2;
}

return 0;
