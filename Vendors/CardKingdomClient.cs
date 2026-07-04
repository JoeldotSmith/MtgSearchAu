using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MtgPriceSearch.Vendors;

internal static class CardKingdomClient
{
    private const int PostageUsdCents = 10_50;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly HashSet<string> ExcludeStyles = new(StringComparer.Ordinal)
    {
        "G",
    };

    public static async Task<Dictionary<string, CardResult>> FetchAllAsync(IReadOnlyList<string> cards)
    {
        Console.Write($"  [CardKingdom] Fetching {cards.Count} cards (bulk)... ");

        var audRate = await GetAudRateAsync();
        using var data = await FetchAsync(cards);
        if (data is null)
        {
            return new Dictionary<string, CardResult>();
        }

        var results = Parse(data.RootElement, audRate);
        Console.WriteLine($"✓  ({results.Count} found)");
        return results;
    }

    public static int PostageCents(double audRate)
    {
        return (int)Math.Round(PostageUsdCents * audRate);
    }

    private static async Task<double> GetAudRateAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://open.er-api.com/v6/latest/USD");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var data = await JsonDocument.ParseAsync(stream);
            var rate = data.RootElement.GetProperty("rates").GetProperty("AUD").GetDouble();
            Console.Write($"  [CardKingdom] USD→AUD rate: {rate:F4} ");
            return rate;
        }
        catch (Exception ex)
        {
            const double fallback = 1.58;
            Console.Write($"  [CardKingdom] Rate fetch failed ({ex.Message}), using fallback {fallback} ");
            return fallback;
        }
    }

    private static async Task<JsonDocument?> FetchAsync(IReadOnlyList<string> cards)
    {
        var cardData = string.Join("\r\n", cards.Select(name => $"1 {name}"));
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["submit"] = 1,
            ["cardData"] = cardData,
            ["autofill_lp"] = "1",
            ["NM"] = "1",
            ["EX"] = "1",
            ["VG"] = "1",
            ["G"] = "1",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.cardkingdom.com/api/builder");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            return await JsonDocument.ParseAsync(stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n  [CardKingdom] Request failed: {ex.Message}");
            return null;
        }
    }

    private static Dictionary<string, CardResult> Parse(JsonElement data, double audRate)
    {
        var results = new Dictionary<string, CardResult>();
        if (!data.TryGetProperty("results", out var cardGroups) || cardGroups.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var cardGroup in cardGroups.EnumerateArray())
        {
            if (cardGroup.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var printing in cardGroup.EnumerateArray())
            {
                if (GetBool(printing, "is_shiny") || GetString(printing, "model") == "mtg_foil")
                {
                    continue;
                }

                var cardName = GetString(printing, "core_name").Trim();
                if (cardName.Length == 0)
                {
                    continue;
                }

                var cardKey = cardName.ToLowerInvariant();
                var setName = "";
                var editionSlug = "";
                if (printing.TryGetProperty("edition", out var edition) && edition.ValueKind == JsonValueKind.Object)
                {
                    setName = GetString(edition, "name");
                    editionSlug = GetString(edition, "slug");
                }

                var cleanSlug = GetString(printing, "clean_slug");
                var cardUrl = cleanSlug.Length > 0 && editionSlug.Length > 0
                    ? $"https://www.cardkingdom.com/mtg/{editionSlug}/{cleanSlug}"
                    : null;

                if (!printing.TryGetProperty("style_qty", out var styleQuantities) ||
                    styleQuantities.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var style in styleQuantities.EnumerateArray())
                {
                    var condition = GetString(style, "style");
                    if (ExcludeStyles.Contains(condition))
                    {
                        continue;
                    }

                    var qty = GetInt(style, "qty");
                    if (qty == 0)
                    {
                        continue;
                    }

                    var price = GetDecimal(style, "price");
                    if (price is null)
                    {
                        continue;
                    }

                    var priceUsdCents = (int)Math.Round(price.Value * 100m);
                    var priceAudCents = (int)Math.Round(priceUsdCents * audRate);

                    if (!results.TryGetValue(cardKey, out var current) || priceAudCents < current.PriceCents)
                    {
                        results[cardKey] = new CardResult(
                            cardName,
                            setName,
                            StyleToCondition(condition),
                            qty,
                            priceAudCents,
                            cardUrl);
                    }
                }
            }
        }

        return results;
    }

    private static string StyleToCondition(string style)
    {
        return style switch
        {
            "NM" => "Near Mint",
            "EX" => "Lightly Played",
            "VG" => "Moderately Played",
            "G" => "Heavily Played",
            _ => style,
        };
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

    private static bool GetBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.True;
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
}
