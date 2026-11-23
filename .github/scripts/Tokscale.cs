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
    private sealed record Palette(string Canvas, string Surface, string Border, string Ink, string Muted, string[] Cells);

    private static readonly Palette Light = new("#ffffff", "#f6f8fa", "#d1d9e0", "#1f2328", "#59636e",
        ["#e9edf1", "#c4ddac", "#93c76e", "#579342", "#336b2c"]);

    private static readonly Palette Dark = new("#0d1117", "#151b23", "#343d48", "#f0f6fc", "#a0aab4",
        ["#212830", "#234934", "#39744a", "#6aa354", "#a7d760"]);

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

        JsonElement range = profile.GetProperty("chartRange");
        DateOnly start = ReadDate(range, "start");
        DateOnly end = ReadDate(range, "end");
        if (end < start)
        {
            throw new InvalidDataException("Reversed Tokscale chart dates.");
        }

        Dictionary<DateOnly, long> daily = [];
        HashSet<DateOnly> active = [];
        foreach (JsonElement entry in profile.GetProperty("contributions").EnumerateArray())
        {
            DateOnly date = ReadDate(entry, "date");
            JsonElement totals = entry.GetProperty("totals");
            long tokens = ReadCount(totals, "tokens");
            daily.Add(date, tokens);
            if (tokens > 0 || (totals.TryGetProperty("cost", out JsonElement dayCost)
                && dayCost.ValueKind == JsonValueKind.Number && dayCost.GetDouble() > 0))
            {
                active.Add(date);
            }
        }

        DateOnly? updated = null;
        if (profile.GetProperty("updatedAt").GetString() is string stamp)
        {
            if (!Regex.IsMatch(stamp, @"(?:Z|[+-]\d\d:\d\d)$"))
            {
                throw new InvalidDataException("Missing time zone in Tokscale update time.");
            }

            updated = DateOnly.FromDateTime(DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture).UtcDateTime);
        }

        var usage = new Usage(ReadCount(stats, "totalTokens"), totalCost, rank, updated, daily, active);
        DateOnly cutoff = end.AddMonths(-6).AddDays(1);
        DateOnly mobileStart = start > cutoff ? start : cutoff;
        foreach ((Palette palette, string suffix) in new[] { (Light, ""), (Dark, "-dark") })
        {
            await SaveAsync($"assets/tokscale/activity{suffix}.svg", Render(usage, start, end, palette, mobile: false));
            await SaveAsync($"assets/tokscale/activity-mobile{suffix}.svg", Render(usage, mobileStart, end, palette, mobile: true));
        }
    }

    private sealed record Usage(long Tokens, double? Cost, long? Rank, DateOnly? Updated,
        Dictionary<DateOnly, long> Daily, HashSet<DateOnly> Active);

    private static async Task SaveAsync(string path, string content)
    {
        if (File.Exists(path) && await File.ReadAllTextAsync(path) == content)
        {
            return;
        }

        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
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

    private static string Render(Usage usage, DateOnly start, DateOnly end, Palette palette, bool mobile)
    {
        int width = mobile ? 320 : 800;
        int secondRow = mobile && usage.Cost.HasValue ? 49 : 0;
        int height = mobile ? 267 + secondRow : 278;
        int summaryHeight = 76 + secondRow;
        int headingY = summaryHeight + 25;
        int calendarY = headingY + 31;
        int footerY = mobile ? 230 + secondRow : 258;
        int activeDays = usage.Active.Count(date => date >= start && date <= end);
        string period = $"{start:MMM d, yyyy} to {end:MMM d, yyyy}";
        string stamp = usage.Updated is DateOnly updated ? $"Updated {updated:MMM d, yyyy}" : "";

        var metrics = new StringBuilder();
        var description = new StringBuilder($"{usage.Tokens:N0} lifetime tokens.");
        AppendMetric(metrics, palette, 16, 25, "Lifetime tokens", Compact(usage.Tokens), 32);
        if (usage.Cost is double cost)
        {
            string formatted = cost >= 100 ? $"${cost:N0}" : $"${cost:N2}";
            AppendMetric(metrics, palette, mobile ? 16 : 528, mobile ? 82 : 25, "Estimated cost", formatted, 22);
            description.Append($" Estimated cost {formatted}.");
        }

        if (usage.Rank is long rank)
        {
            AppendMetric(metrics, palette, mobile ? 174 : 688, mobile ? 82 : 25, "Cost rank", $"#{rank:N0}", 22);
            description.Append($" All-time cost rank #{rank:N0}.");
        }

        description.Append($" Daily tokens from {period}, with {activeDays} active days. Weeks start on Sunday.");
        if (stamp.Length > 0)
        {
            description.Append($" {stamp} UTC.");
        }

        int stampX = mobile ? 16 : width - 112;
        int stampY = mobile ? footerY + 23 : footerY;
        string dateAnchor = mobile ? "start" : "end";
        string legend = string.Concat(palette.Cells.Select((color, i) =>
            $"""<rect class="d{i}" x="{47 + i * 12}" y="{footerY - 9}" width="9" height="9" fill="{color}"/>"""));
        return $$"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{{width}}" height="{{height}}" viewBox="0 0 {{width}} {{height}}" role="img" aria-labelledby="title desc" font-family="-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif">
              <title id="title">Tokscale token usage</title>
              <desc id="desc">{{description}}</desc>
              <rect class="panel" x=".5" y=".5" width="{{width - 1}}" height="{{height - 1}}" fill="{{palette.Canvas}}" stroke="{{palette.Border}}"/>
              <rect class="summary" x="1" y="1" width="{{width - 2}}" height="{{summaryHeight - 1}}" fill="{{palette.Surface}}"/>
              <path class="rule" d="M1 {{summaryHeight}}h{{width - 2}}" stroke="{{palette.Border}}"/>
              {{metrics}}
              <text class="ink" fill="{{palette.Ink}}" x="16" y="{{headingY}}" font-size="15" font-weight="600">Daily tokens</text>
              <text class="muted" fill="{{palette.Muted}}" x="{{width - 16}}" y="{{headingY}}" text-anchor="end" font-size="13">{{activeDays}} active days</text>
              {{Calendar(usage.Daily, start, end, width, calendarY, mobile, palette)}}
              <g class="muted" fill="{{palette.Muted}}" font-size="13">
                <text x="16" y="{{footerY}}">Less</text>{{legend}}<text x="111" y="{{footerY}}">More</text>
                <text x="{{stampX}}" y="{{stampY}}" text-anchor="{{dateAnchor}}">{{stamp}}</text>
                <text class="ink" fill="{{palette.Ink}}" x="{{width - 16}}" y="{{footerY}}" text-anchor="end">Tokscale ↗</text>
              </g>
            </svg>

            """;
    }

    private static void AppendMetric(StringBuilder metrics, Palette palette, int x, int y, string label, string value, int size)
    {
        metrics.Append($"""
            <g transform="translate({x} {y})">
                <text class="muted" fill="{palette.Muted}" font-size="14">{label}</text>
                <text class="ink" fill="{palette.Ink}" y="31" font-size="{size}" font-weight="600">{value}</text>
              </g>
            """);
    }

    private static string Calendar(Dictionary<DateOnly, long> daily, DateOnly start, DateOnly end,
        int width, int top, bool mobile, Palette palette)
    {
        DateOnly sunday = start.AddDays(-(int)start.DayOfWeek);
        int weeks = (end.DayNumber - sunday.DayNumber) / 7 + 1;
        double step = Math.Min(mobile ? 11 : 15, (width - 32d) / weeks);
        double side = step - (mobile ? 2 : 3);

        // Use the positive days in the displayed period for the four heatmap quartiles.
        long[] positive = daily.Where(day => day.Key >= start && day.Key <= end && day.Value > 0)
            .Select(day => day.Value).Order().ToArray();
        long[] cuts = positive.Length == 0 ? [] : [positive[(positive.Length - 1) / 4],
            positive[(positive.Length - 1) / 2], positive[(positive.Length - 1) * 3 / 4]];

        var chart = new StringBuilder();
        for (DateOnly date = start; date <= end; date = date.AddDays(1))
        {
            int column = (date.DayNumber - sunday.DayNumber) / 7;
            double x = 16 + column * step;
            double y = top + (int)date.DayOfWeek * step;
            if (date.Day == 1 && x + 24 <= width - 12)
            {
                chart.AppendLine($"""<text class="muted" fill="{palette.Muted}" x="{x:0.##}" y="{top - 7}" font-size="12">{date:MMM}</text>""");
            }

            long value = daily.GetValueOrDefault(date);
            int level = value == 0 ? 0 : 1 + cuts.Count(cut => value > cut);
            chart.AppendLine($"""<rect class="d{level}" x="{x:0.##}" y="{y:0.##}" width="{side:0.##}" height="{side:0.##}" fill="{palette.Cells[level]}"/>""");
        }

        return chart.ToString().TrimEnd();
    }
}
