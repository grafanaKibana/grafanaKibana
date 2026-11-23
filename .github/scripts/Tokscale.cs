using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Tokscale
{
    private const string User = "grafanaKibana";
    private const string Api = "https://tokscale.ai/api";
    private const string Asset = "assets/tokscale/activity.svg";

    internal static async Task RefreshAsync(HttpClient http)
    {
        JsonElement profile = await http.GetFromJsonAsync<JsonElement>($"{Api}/users/{User}");
        JsonElement user = profile.GetProperty("user");
        if (!IsProfileOwner(user) || profile.GetProperty("period").GetString() != "all")
        {
            throw new InvalidDataException("Unexpected Tokscale profile or period.");
        }

        JsonElement stats = profile.GetProperty("stats");
        JsonElement cost = stats.GetProperty("totalCost");
        double? totalCost = cost.ValueKind == JsonValueKind.Null ? null : cost.GetDouble();
        if (totalCost is double amount && (!double.IsFinite(amount) || amount < 0))
        {
            throw new InvalidDataException("Invalid Tokscale cost.");
        }

        long? rank = null;
        if (totalCost > 0 && user.GetProperty("rank").ValueKind != JsonValueKind.Null)
        {
            rank = await FetchRankAsync(http, user.GetProperty("id").GetString());
        }

        string original = await File.ReadAllTextAsync(Asset);
        string updated = Render(original, profile, stats, totalCost, rank);
        if (updated != original)
        {
            string temporary = $"{Asset}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, updated);
                File.Move(temporary, Asset, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsProfileOwner(JsonElement user) =>
        string.Equals(user.GetProperty("username").GetString(), User, StringComparison.OrdinalIgnoreCase);

    private static async Task<long?> FetchRankAsync(HttpClient http, string? userId)
    {
        using HttpResponseMessage response = await http.GetAsync($"{Api}/leaderboard/user/{User}?sortBy=cost&period=all");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        JsonElement standing = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (!IsProfileOwner(standing) || standing.GetProperty("userId").GetString() != userId)
        {
            throw new InvalidDataException("Cost standing belongs to a different profile.");
        }

        if (standing.GetProperty("rank").ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        long rank = ReadCount(standing, "rank");
        return rank > 0 ? rank : throw new InvalidDataException("Cost rank must be positive.");
    }

    private static long ReadCount(JsonElement element, string property)
    {
        long value = element.GetProperty(property).GetInt64();
        return value >= 0 ? value : throw new InvalidDataException($"Negative Tokscale {property}.");
    }

    private static DateOnly ReadDate(JsonElement element, string property) =>
        DateOnly.ParseExact(element.GetProperty(property).GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Render(string template, JsonElement profile, JsonElement stats, double? cost, long? rank)
    {
        long tokens = ReadCount(stats, "totalTokens");
        long activeDays = ReadCount(stats, "activeDays");
        JsonElement range = profile.GetProperty("chartRange");
        DateOnly start = ReadDate(range, "start");
        DateOnly end = ReadDate(range, "end");
        if (end < start)
        {
            throw new InvalidDataException("Reversed Tokscale chart dates.");
        }

        List<(string Label, string Value)> metrics = [("Lifetime tokens", Compact(tokens))];
        List<string> description = [$"{tokens:N0} lifetime tokens."];
        if (cost is double amount)
        {
            string formatted = amount >= 100 ? $"${amount:N0}" : $"${amount:N2}";
            metrics.Add(("Estimated cost", formatted));
            description.Add($"Estimated cost {formatted}.");
        }

        if (rank.HasValue)
        {
            metrics.Add(("Cost rank", $"#{rank:N0}"));
            description.Add($"All-time cost rank #{rank:N0}.");
        }

        description.Add($"Daily token activity from {start:MMM d, yyyy} to {end:MMM d, yyyy}, "
            + $"with {activeDays:N0} active days. Weeks start on Sunday. "
            + "The four green shades show quartiles of nonzero daily token counts.");
        string stamp = "";
        if (profile.GetProperty("updatedAt").GetString() is string updated)
        {
            if (!Regex.IsMatch(updated, @"(?:Z|[+-]\d\d:\d\d)$"))
            {
                throw new InvalidDataException("Missing time zone in Tokscale update time.");
            }

            DateTime utc = DateTimeOffset.Parse(updated, CultureInfo.InvariantCulture).UtcDateTime;
            stamp = $"Updated {utc:MMM d, yyyy}";
            description.Add($"{stamp} UTC.");
        }

        var metricHtml = new StringBuilder();
        foreach ((string label, string value) in metrics)
        {
            string classes = metricHtml.Length == 0 ? "metric primary" : "metric";
            metricHtml.Append($"""<div class="{classes}"><span>{label}</span><strong>{value}</strong></div>""");
        }

        Dictionary<string, string> slots = new()
        {
            ["description"] = string.Join(' ', description),
            ["metrics"] = metricHtml.ToString(),
            ["days"] = $"{activeDays:N0} active days",
            ["calendar"] = Calendar(profile.GetProperty("contributions"), start, end),
            ["stamp"] = stamp
        };

        foreach ((string key, string value) in slots)
        {
            string open = $"<!-- data:{key} -->";
            string close = $"<!-- /data:{key} -->";
            var slot = new Regex($"{open}.*?{close}", RegexOptions.Singleline);
            if (slot.Count(template) != 1)
            {
                throw new InvalidDataException($"Expected exactly one {key} slot.");
            }

            template = slot.Replace(template, _ => open + value + close);
        }

        return template;
    }

    private static string Compact(long tokens)
    {
        (long Scale, string Suffix)[] scales = [(1_000_000_000_000, "T"), (1_000_000_000, "B"), (1_000_000, "M"), (1_000, "K")];
        foreach ((long scale, string suffix) in scales)
        {
            if (tokens >= scale)
            {
                return ((double)tokens / scale).ToString("F2", CultureInfo.InvariantCulture)
                    .TrimEnd('0').TrimEnd('.') + suffix;
            }
        }

        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    private static string Calendar(JsonElement contributions, DateOnly start, DateOnly end)
    {
        Dictionary<DateOnly, long> daily = [];
        foreach (JsonElement entry in contributions.EnumerateArray())
        {
            daily.Add(ReadDate(entry, "date"), ReadCount(entry.GetProperty("totals"), "tokens"));
        }

        // Quartiles of active dates keep unusually busy days from washing out the rest.
        long[] positive = daily.Where(day => day.Key >= start && day.Key <= end && day.Value > 0)
            .Select(day => day.Value).Order().ToArray();
        long[] cuts = positive.Length == 0 ? [] : [positive[(positive.Length - 1) / 4],
            positive[(positive.Length - 1) / 2], positive[(positive.Length - 1) * 3 / 4]];
        DateOnly sunday = start.AddDays(-(int)start.DayOfWeek);
        int weeks = (end.DayNumber - sunday.DayNumber) / 7 + 1;
        List<string> halves = [];
        foreach ((int first, int stop) in new[] { (0, weeks / 2), (weeks / 2, weeks) })
        {
            if (first == stop)
            {
                continue;
            }

            var months = new StringBuilder();
            var cells = new StringBuilder();
            for (int day = first * 7; day < stop * 7; day++)
            {
                DateOnly date = sunday.AddDays(day);
                if (date < start || date > end)
                {
                    cells.Append("""<i aria-hidden="true"/>""");
                    continue;
                }

                int column = day / 7;
                if (date.Day == 1 && column < stop - 1)
                {
                    months.Append($"""<span style="grid-column:{column - first + 1}">{date:MMM}</span>""");
                }

                long value = daily.GetValueOrDefault(date);
                int level = value == 0 ? 0 : 1 + cuts.Count(cut => value > cut);
                cells.Append($"""<i class="d{level}"/>""");
            }

            halves.Add($"""<div class="half" style="--weeks:{stop - first}"><div class="months">{months}</div><div class="days">{cells}</div></div>""");
        }

        return string.Join('\n', halves);
    }
}
