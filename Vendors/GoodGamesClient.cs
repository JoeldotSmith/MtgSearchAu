using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MtgPriceSearch.Vendors;

internal static class GoodGamesClient
{
    private const string DecklistUrl =
        "https://portal.binderpos.com/external/shopify/decklist?storeUrl=good-games-townhall.myshopify.com&type=mtg";

    private const string ProductBaseUrl = "https://tcg.goodgames.com.au/products/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly HashSet<string> ExcludeConditions = new(StringComparer.Ordinal)
    {
        "heavily played",
        "damaged",
        "heavily played foil",
        "damaged foil",
    };

    public static async Task<(Dictionary<string, CardResult> Results, Dictionary<string, int> NmPrices)> FetchAllAsync(
        IReadOnlyList<string> cards,
        Action<VendorFetchProgress>? progress = null)
    {
        Console.Write($"  [GoodGames] Fetching {cards.Count} cards (bulk)... ");

        var fetchResult = await FetchDecklistAsync(cards);
        if (fetchResult.Data is null)
        {
            if (fetchResult.IsVendorAccessProblem)
            {
                progress?.Invoke(new VendorFetchProgress(
                    null,
                    null,
                    0,
                    cards.Count,
                    fetchResult.Message,
                    true));
            }

            return (
                new Dictionary<string, CardResult>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal));
        }

        using var data = fetchResult.Data;
        var (results, nmPrices) = Parse(data.RootElement);
        Console.WriteLine($"✓  ({results.Count} found)");

        var completedCards = 0;
        foreach (var cardName in cards)
        {
            completedCards++;
            results.TryGetValue(cardName.ToLowerInvariant(), out var result);
            progress?.Invoke(new VendorFetchProgress(
                cardName,
                result,
                completedCards,
                cards.Count));
        }

        return (results, nmPrices);
    }

    private static async Task<JsonFetchResult> FetchDecklistAsync(IReadOnlyList<string> cards)
    {
        var payload = JsonSerializer.Serialize(cards.Select(cardName => new
        {
            card = cardName,
            quantity = "1",
        }));

        using var request = new HttpRequestMessage(HttpMethod.Post, DecklistUrl);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:152.0) Gecko/20100101 Firefox/152.0");
        request.Headers.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
        request.Headers.AcceptLanguage.ParseAdd("en-AU");
        request.Headers.AcceptLanguage.ParseAdd("en;q=0.9");
        request.Headers.Referrer = new Uri("https://tcg.goodgames.com.au/");
        request.Headers.TryAddWithoutValidation("Origin", "https://tcg.goodgames.com.au");

        try
        {
            using var response = await Http.SendAsync(request);
            if (VendorAccessIssue.IsLikelyBlocked(response.StatusCode))
            {
                var message = VendorAccessIssue.MessageFor("Good Games");
                Console.WriteLine($"\n  [GoodGames] Request failed: {message}");
                return new JsonFetchResult(null, message, true);
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            if (VendorAccessIssue.IsLikelyBlocked(body))
            {
                var message = VendorAccessIssue.MessageFor("Good Games");
                Console.WriteLine($"\n  [GoodGames] Request failed: {message}");
                return new JsonFetchResult(null, message, true);
            }

            var data = JsonDocument.Parse(body);
            return new JsonFetchResult(data, null, false);
        }
        catch (Exception ex)
        {
            if (VendorAccessIssue.IsLikelyBlocked(ex))
            {
                var message = VendorAccessIssue.MessageFor("Good Games");
                Console.WriteLine($"\n  [GoodGames] Request failed: {message}");
                return new JsonFetchResult(null, message, true);
            }

            Console.WriteLine($"\n  [GoodGames] Request failed: {ex.Message}");
            return new JsonFetchResult(null, null, false);
        }
    }

    private static (Dictionary<string, CardResult> Results, Dictionary<string, int> NmPrices) Parse(JsonElement data)
    {
        var results = new Dictionary<string, CardResult>(StringComparer.Ordinal);
        var nmPrices = new Dictionary<string, int>(StringComparer.Ordinal);

        if (data.ValueKind != JsonValueKind.Array)
        {
            return (results, nmPrices);
        }

        foreach (var decklistItem in data.EnumerateArray())
        {
            var searchName = GetString(decklistItem, "searchName").Trim();
            if (searchName.Length == 0 || GetInt(decklistItem, "found") <= 0)
            {
                continue;
            }

            if (!decklistItem.TryGetProperty("products", out var products) ||
                products.ValueKind != JsonValueKind.Array ||
                products.GetArrayLength() == 0)
            {
                continue;
            }

            CardResult? best = null;
            int? bestNm = null;

            foreach (var product in products.EnumerateArray())
            {
                var productTitle = GetString(product, "title");
                var productName = FirstNonEmpty(
                    GetString(product, "name"),
                    CleanProductTitle(productTitle),
                    searchName);
                var setName = FirstNonEmpty(GetString(product, "setName"), ExtractSetName(productTitle));
                var handle = GetString(product, "handle");
                var cardUrl = handle.Length > 0 ? ProductBaseUrl + handle : null;

                if (!product.TryGetProperty("variants", out var variants) ||
                    variants.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var variant in variants.EnumerateArray())
                {
                    var condition = FirstNonEmpty(GetString(variant, "title"), "Unknown");
                    var qty = GetInt(variant, "quantity");
                    var price = GetDecimal(variant, "price");
                    if (qty <= 0 || price is null)
                    {
                        continue;
                    }

                    if (ExcludeConditions.Contains(condition.ToLowerInvariant()))
                    {
                        continue;
                    }

                    var priceCents = (int)Math.Round(price.Value * 100m, MidpointRounding.AwayFromZero);
                    if (best is null || priceCents < best.PriceCents)
                    {
                        best = new CardResult(
                            productName,
                            setName,
                            condition,
                            qty,
                            priceCents,
                            cardUrl);
                    }

                    if (condition.Equals("Near Mint", StringComparison.OrdinalIgnoreCase) &&
                        (bestNm is null || priceCents < bestNm))
                    {
                        bestNm = priceCents;
                    }
                }
            }

            var cardKey = searchName.ToLowerInvariant();
            if (best is not null)
            {
                results[cardKey] = best;
            }

            if (bestNm is not null)
            {
                nmPrices[cardKey] = bestNm.Value;
            }
        }

        return (results, nmPrices);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            var trimmed = value.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return "";
    }

    private static string CleanProductTitle(string title)
    {
        var setStart = title.LastIndexOf(" [", StringComparison.Ordinal);
        return setStart > 0 ? title[..setStart].Trim() : title.Trim();
    }

    private static string ExtractSetName(string title)
    {
        var start = title.LastIndexOf('[');
        var end = title.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return "";
        }

        return title[(start + 1)..end].Trim();
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return "";
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? "",
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => 0,
        };
    }

    private static decimal? GetDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDecimal(out var value) => value,
            JsonValueKind.String when decimal.TryParse(property.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) => value,
            _ => null,
        };
    }

    private sealed record JsonFetchResult(
        JsonDocument? Data,
        string? Message,
        bool IsVendorAccessProblem);
}
