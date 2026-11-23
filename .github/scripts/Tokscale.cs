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
        string height = mobile ? "100%" : "278";
        int summaryHeight = mobile ? 59 : 76;
        // The compact viewport is fluid. Only the calendar columns scale with available width.
        string viewport = mobile ? "width='100%'" : "width='800' viewBox='0 0 800 278'";
        const string left = "16";
        string rightInset = mobile ? " dx=\"-16\"" : "";
        string right = mobile ? "100%" : "784";
        int headingY = summaryHeight + (mobile ? 21 : 25);
        int calendarY = headingY + (mobile ? 24 : 31);
        string footerTop = mobile ? "100%" : "245";
        int activeDays = usage.Active.Count(date => date >= start && date <= end);
        string period = $"{start:MMM d, yyyy} to {end:MMM d, yyyy}";
        string stamp = usage.Updated is DateOnly updated ? $"Updated {updated:MMM d, yyyy}" : "";

        var metrics = new StringBuilder();
        var description = new StringBuilder($"{usage.Tokens:N0} lifetime tokens.");
        AppendMetric(metrics, palette, mobile ? 21 : 25, "Lifetime tokens", Compact(usage.Tokens), mobile ? 20 : 32, mobile);
        string? rankText = usage.Rank is long rank ? $"#{rank:N0}" : null;
        if (usage.Cost is double cost)
        {
            string formatted = cost >= 100 ? $"${cost:N0}" : $"${cost:N2}";
            int rankSpace = rankText is null ? 0
                : Math.Max(mobile ? 50 : 60, rankText.Length * (mobile ? 10 : 14)) + (mobile ? 12 : 20);
            AppendMetric(metrics, palette, mobile ? 21 : 25, "Estimated cost", formatted, mobile ? 16 : 22, mobile, rightInset: 16 + rankSpace);
            description.Append($" Estimated cost {formatted}.");
        }

        if (rankText is not null)
        {
            AppendMetric(metrics, palette, mobile ? 21 : 25, "Cost rank", rankText, mobile ? 16 : 22, mobile, rightInset: 16);
            description.Append($" All-time cost rank {rankText}.");
        }

        description.Append($" Daily tokens from {period}, with {activeDays} active days. Weeks start on Sunday.");
        if (stamp.Length > 0)
        {
            description.Append($" {stamp} UTC.");
        }

        string stampX = mobile ? "50%" : "688";
        string dateAnchor = mobile ? "middle" : "end";
        string stampText = mobile && usage.Updated is DateOnly date
            ? $"""<tspan class="expanded">Updated </tspan>{date:MMM d}<tspan class="expanded">, {date:yyyy}</tspan>"""
            : stamp;
        string legend = string.Concat(palette.Cells.Select((color, i) =>
            $"""<rect class="d{i}" x="{i * 12}" y="4" width="9" height="9" fill="{color}"/>"""));
        string compactStyles = mobile ? """
            <style>
              .expanded, .metric-long { display: none }
              @media (min-width: 300px) {
                .metric-long { display: inline }
                .short { display: none }
              }
              @media (min-width: 380px) {
                .expanded { display: inline }
                .swatches { transform: translateX(31px) }
              }
            </style>
            """ : "";
        return $$"""
            <svg xmlns="http://www.w3.org/2000/svg" {{viewport}} height="{{height}}" role="img" aria-labelledby="title desc" font-family="-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif">
              <title id="title">Tokscale token usage</title>
              <desc id="desc">{{description}}</desc>
              <rect width="100%" height="100%" fill="{{palette.Canvas}}"/>
              <rect width="100%" height="{{summaryHeight}}" fill="{{palette.Surface}}"/>
              <line x2="100%" y1="{{summaryHeight}}" y2="{{summaryHeight}}" stroke="{{palette.Border}}"/>
            {{compactStyles}}
              {{metrics}}
              <text fill="{{palette.Ink}}" x="{{left}}" y="{{headingY}}" font-size="{{(mobile ? 14 : 15)}}" font-weight="600">Daily tokens</text>
              <text fill="{{palette.Muted}}" x="{{right}}"{{rightInset}} y="{{headingY}}" text-anchor="end" font-size="{{(mobile ? 12 : 13)}}">{{activeDays}} active days</text>
              {{Calendar(usage.Daily, start, end, calendarY, mobile, palette)}}
              <g transform="translate(0 {{(mobile ? -29 : 0)}})"><svg y="{{footerTop}}" width="100%" height="24" fill="{{palette.Muted}}" font-size="{{(mobile ? 12 : 13)}}">
                <svg x="{{left}}" width="140" height="18">
                  <text class="expanded" y="13">Less</text><g class="swatches" transform="translate({{(mobile ? 0 : 31)}})">{{legend}}</g><text class="expanded" x="95" y="13">More</text>
                </svg>
                <text x="{{stampX}}" y="13" text-anchor="{{dateAnchor}}">{{stampText}}</text>
                <text fill="{{palette.Ink}}" x="{{right}}"{{rightInset}} y="13" text-anchor="end">Tokscale ↗</text>
              </svg></g>
              <rect width="100%" height="100%" fill="none" stroke="{{palette.Border}}" stroke-width="2"/>
            </svg>

            """;
    }

    private static void AppendMetric(StringBuilder metrics, Palette palette, int y,
        string label, string value, int size, bool mobile, int? rightInset = null)
    {
        int baseline = y + (mobile ? 22 : 31);
        string position = rightInset.HasValue
            ? $"x=\"100%\" dx=\"-{rightInset.Value}\" text-anchor=\"end\""
            : "x=\"16\" text-anchor=\"start\"";

        if (mobile && label is "Lifetime tokens" or "Estimated cost")
        {
            string shortLabel = label == "Lifetime tokens" ? "Tokens" : "Est. cost";
            label = $"""<tspan class="metric-long">{label}</tspan><tspan class="short">{shortLabel}</tspan>""";
        }

        metrics.Append($"""
            <text fill="{palette.Muted}" {position} y="{y}" font-size="{(mobile ? 12 : 14)}">{label}</text>
              <text fill="{palette.Ink}" {position} y="{baseline}" font-size="{size}" font-weight="600">{value}</text>
            """);
    }

    private static string Calendar(Dictionary<DateOnly, long> daily, DateOnly start, DateOnly end,
        int top, bool mobile, Palette palette)
    {
        DateOnly sunday = start.AddDays(-(int)start.DayOfWeek);
        int weeks = (end.DayNumber - sunday.DayNumber) / 7 + 1;
        double step = Math.Min(15, 768d / weeks);
        double side = mobile ? 8 : step - 3;

        // Use the positive days in the displayed period for the four heatmap quartiles.
        long[] positive = daily.Where(day => day.Key >= start && day.Key <= end && day.Value > 0)
            .Select(day => day.Value).Order().ToArray();
        long[] cuts = positive.Length == 0 ? [] : [positive[(positive.Length - 1) / 4],
            positive[(positive.Length - 1) / 2], positive[(positive.Length - 1) * 3 / 4]];

        var chart = new StringBuilder();
        var cells = new StringBuilder();
        for (var column = 0; column < weeks; column++)
        {
            string x = mobile ? $"{100d * column / weeks:0.###}%" : $"{16 + column * step:0.##}";
            string monthInset = mobile ? $" dx=\"{16 - 32d * column / weeks:0.###}\"" : "";
            string cellX = mobile ? (column * 10).ToString(CultureInfo.InvariantCulture) : x;
            for (var weekday = 0; weekday < 7; weekday++)
            {
                DateOnly date = sunday.AddDays(column * 7 + weekday);
                if (date < start || date > end)
                {
                    continue;
                }

                bool monthFits = mobile ? column <= weeks - 3 : 16 + column * step + 24 <= 788;
                if (date.Day == 1 && monthFits)
                {
                    chart.AppendLine($"""<text fill="{palette.Muted}" x="{x}"{monthInset} y="{top - 7}" font-size="12">{date:MMM}</text>""");
                }

                long value = daily.GetValueOrDefault(date);
                int level = value == 0 ? 0 : 1 + cuts.Count(cut => value > cut);
                double y = mobile ? 10 * weekday : top + weekday * step;
                cells.AppendLine($"""<rect class="d{level}" x="{cellX}" y="{y:0.##}" width="{side:0.##}" height="{side:0.##}" fill="{palette.Cells[level]}"/>""");
            }

            if (!mobile)
            {
                chart.Append(cells);
                cells.Clear();
            }
        }

        if (mobile)
        {
            // The grid uses one coordinate system so every day scales by the same amount on resize.
            chart.AppendLine($"""
                <defs><pattern id="weeks" width="1" height="1" viewBox="0 0 {weeks * 10} 70" preserveAspectRatio="xMinYMin meet">
                {cells}</pattern></defs>
                <rect x="16" y="{top}" width="calc(100% - 32px)" height="175" fill="url(#weeks)"/>
                """);
        }

        return chart.ToString().TrimEnd();
    }
}
