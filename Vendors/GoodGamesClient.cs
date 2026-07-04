using System.Text.RegularExpressions;

namespace MtgPriceSearch.Vendors;

internal static class GoodGamesClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly HashSet<string> ExcludeConditions = new(StringComparer.Ordinal)
    {
        "heavily played",
        "damaged",
        "heavily played foil",
        "damaged foil",
    };

    public static async Task<(Dictionary<string, CardResult> Results, Dictionary<string, int> NmPrices)> FetchAllAsync(
        IReadOnlyList<string> cards)
    {
        var results = new Dictionary<string, CardResult>();
        var nmPrices = new Dictionary<string, int>();

        foreach (var cardName in cards)
        {
            Console.Write($"  [GoodGames] Searching: {cardName}... ");
            var (result, nmPrice) = await FetchCardAsync(cardName);

            if (result is not null)
            {
                results[cardName.ToLowerInvariant()] = result;
                Console.WriteLine("✓");
            }
            else
            {
                Console.WriteLine("not found");
            }

            if (nmPrice is not null)
            {
                nmPrices[cardName.ToLowerInvariant()] = nmPrice.Value;
            }
        }

        return (results, nmPrices);
    }

    private static async Task<(CardResult? Result, int? NmPrice)> FetchCardAsync(string cardName)
    {
        var encoded = Uri.EscapeDataString(cardName.Replace(",", "", StringComparison.Ordinal));
        var url = "https://tcg.goodgames.com.au/search?q=" + encoded +
                  "&f_Availability=Exclude%20Out%20Of%20Stock" +
                  "&f_Product%20Type=mtg%20single";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");

        string html;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await Http.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();
            html = await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Console.Write($"ERROR — {ex.Message} ");
            return (null, null);
        }

        var pattern = new Regex(
            @"Spurit\.Preorder2\.snippet\.products\['[^']+'\]\s*=\s*(\{.*?\});",
            RegexOptions.Singleline);
        var matches = pattern.Matches(html);

        CardResult? best = null;
        int? bestNm = null;

        foreach (Match match in matches)
        {
            var block = match.Groups[1].Value;
            var handleMatch = Regex.Match(block, "handle:\"([^\"]+)\"");
            var handle = handleMatch.Success ? handleMatch.Groups[1].Value : null;

            var titleMatch = Regex.Match(block, "title:\"([^\"]+)\"");
            if (!titleMatch.Success)
            {
                continue;
            }

            var title = titleMatch.Groups[1].Value.Replace("\\/\\/", "//", StringComparison.Ordinal);
            var cardNeedle = cardName.ToLowerInvariant().Split(',')[0].Trim();
            var titleLower = title.ToLowerInvariant();

            if (!titleLower.Contains(cardNeedle, StringComparison.Ordinal))
            {
                continue;
            }

            if (titleLower.Contains("art series", StringComparison.Ordinal))
            {
                continue;
            }

            if (Regex.IsMatch(title, @"\[[A-Z]{2,}[0-9]+\]"))
            {
                continue;
            }

            var setMatch = Regex.Match(title, @"\[([^\]]+)\]$");
            var setName = setMatch.Success ? setMatch.Groups[1].Value : "";
            var cleanTitle = setMatch.Success ? title[..setMatch.Index].Trim() : title;

            foreach (var variantBlock in Regex.Split(block, @"(?=\{id:\d+,title:)"))
            {
                var conditionMatch = Regex.Match(variantBlock, "title:\"([^\"]+)\"");
                var qtyMatch = Regex.Match(variantBlock, @"inventory_quantity:(\d+)");
                var priceMatch = Regex.Match(variantBlock, @",price:(\d+),");

                if (!conditionMatch.Success || !qtyMatch.Success || !priceMatch.Success)
                {
                    continue;
                }

                var condition = conditionMatch.Groups[1].Value;
                var qty = int.Parse(qtyMatch.Groups[1].Value);
                var price = int.Parse(priceMatch.Groups[1].Value);

                if (qty > 0 && !ExcludeConditions.Contains(condition.ToLowerInvariant()))
                {
                    if (best is null || price < best.PriceCents)
                    {
                        best = new CardResult(
                            cleanTitle,
                            setName,
                            condition,
                            qty,
                            price,
                            handle is not null ? $"https://tcg.goodgames.com.au/products/{handle}" : null);
                    }
                }

                if (condition == "Near Mint")
                {
                    if (bestNm is null || price < bestNm)
                    {
                        bestNm = price;
                    }
                }
            }
        }

        return (best, bestNm);
    }
}
