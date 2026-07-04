using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace MtgPriceSearch.Vendors;

internal static class MtgMateClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly HashSet<string> ExcludeConditions = new(StringComparer.Ordinal)
    {
        "heavily played",
        "damaged",
        "heavily played foil",
        "damaged foil",
    };

    public static async Task<Dictionary<string, CardResult>> FetchAllAsync(IReadOnlyList<string> cards)
    {
        Console.Write($"  [MTGMate] Fetching {cards.Count} cards (bulk)... ");

        var html = await FetchHtmlAsync(cards);
        if (html is null)
        {
            return new Dictionary<string, CardResult>();
        }

        var results = Parse(html);
        Console.WriteLine($"✓  ({results.Count} found)");
        return results;
    }

    private static async Task<string?> FetchHtmlAsync(IReadOnlyList<string> cards)
    {
        var decklist = string.Join("\n", cards.Select(name => $"1 {name}"));
        var parameters = FormEncode(
            ("utf8", "✓"),
            ("decklist", decklist),
            ("commit", "Build Deck"));
        var url = $"https://www.mtgmate.com.au/cards/decklist_results?{parameters}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.AcceptLanguage.ParseAdd("en-AU");
        request.Headers.AcceptLanguage.ParseAdd("en;q=0.9");

        try
        {
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n  [MTGMate] Request failed: {ex.Message}");
            return null;
        }
    }

    private static Dictionary<string, CardResult> Parse(string html)
    {
        var results = new Dictionary<string, CardResult>();
        var chunks = Regex.Split(
            html,
            "<th[^>]*class=\"card-name[^\"]*\"[^>]*>\\s*([^<]+?)\\s*</th>",
            RegexOptions.Singleline);

        var i = 1;
        while (i + 1 < chunks.Length)
        {
            var originalCardName = chunks[i].Trim();
            var cardKey = originalCardName.ToLowerInvariant();
            var block = chunks[i + 1];
            i += 2;

            if (cardKey.Contains('<', StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match row in Regex.Matches(block, "<tr class=\"magic-card[^\"]*\">(.*?)</tr>", RegexOptions.Singleline))
            {
                var rowHtml = row.Groups[1].Value;
                var hrefMatch = Regex.Match(rowHtml, "href=\"/cards/([^\"]+)\"");
                if (!hrefMatch.Success)
                {
                    continue;
                }

                var slugMatch = Regex.Match(rowHtml, ":([a-z-]+)\"");
                var condition = slugMatch.Success
                    ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slugMatch.Groups[1].Value.Replace("-", " ", StringComparison.Ordinal))
                    : "Unknown";

                if (ExcludeConditions.Contains(condition.ToLowerInvariant()))
                {
                    continue;
                }

                var setMatch = Regex.Match(rowHtml, "href=\"/cards/[^\"]+\">([^<]+)</a>");
                var setName = setMatch.Success ? setMatch.Groups[1].Value.Trim() : "";

                var qtyMatch = Regex.Match(rowHtml, @"Available:\s*(\d+)");
                var priceMatch = Regex.Match(rowHtml, @"\$([0-9]+\.[0-9]+)");
                if (!qtyMatch.Success || !priceMatch.Success)
                {
                    continue;
                }

                var qty = int.Parse(qtyMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                if (qty == 0)
                {
                    continue;
                }

                if (!decimal.TryParse(priceMatch.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
                {
                    continue;
                }

                var priceCents = (int)Math.Round(price * 100m);
                var hrefFull = Regex.Match(rowHtml, "href=\"(/cards/[^\"]+)\"");
                var cardUrl = hrefFull.Success ? $"https://www.mtgmate.com.au{hrefFull.Groups[1].Value}" : null;

                if (!results.TryGetValue(cardKey, out var current) || priceCents < current.PriceCents)
                {
                    results[cardKey] = new CardResult(
                        originalCardName,
                        setName,
                        condition,
                        qty,
                        priceCents,
                        cardUrl);
                }
            }
        }

        return results;
    }

    private static string FormEncode(params (string Key, string Value)[] values)
    {
        return string.Join("&", values.Select(v =>
            $"{WebUtility.UrlEncode(v.Key)}={WebUtility.UrlEncode(v.Value)}"));
    }
}
