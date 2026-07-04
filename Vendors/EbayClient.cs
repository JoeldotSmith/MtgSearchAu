using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MtgPriceSearch.Vendors;

internal static class EbayClient
{
    private const string UserSecretsId = "mtg-search-au";
    private const string MarketplaceId = "EBAY_AU";
    private const string TokenUrl = "https://api.ebay.com/identity/v1/oauth2/token";
    private const string SearchUrl = "https://api.ebay.com/buy/browse/v1/item_summary/search";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly HashSet<string> ExcludeConditions = new(StringComparer.Ordinal)
    {
        "heavily played",
        "damaged",
        "heavily played foil",
        "damaged foil",
        "poor",
        "hp",
        "dmg",
    };

    private static readonly string[] ExcludeTitleKeywords =
    [
        "lot", "playset", "proxy", "sealed", "booster", "display",
        "case", "bundle", "token", "art card", "empty sleeve", "art",
    ];

    private static string? s_token;
    private static DateTimeOffset s_expiresAt;

    public static async Task<Dictionary<string, CardResult>> FetchAllAsync(IReadOnlyList<string> cards)
    {
        var results = new Dictionary<string, CardResult>();
        string token;
        try
        {
            token = await GetAccessTokenAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[eBay] Could not get access token — {ex.Message}");
            return results;
        }

        foreach (var cardName in cards)
        {
            Console.Write($"  [eBay] Searching: {cardName}... ");
            var result = await FetchCardAsync(cardName, token);
            if (result is not null)
            {
                results[cardName.ToLowerInvariant()] = result;
                Console.WriteLine("✓");
            }
            else
            {
                Console.WriteLine("not found");
            }
        }

        return results;
    }

    private static async Task<string> GetAccessTokenAsync()
    {
        if (!string.IsNullOrWhiteSpace(s_token) && DateTimeOffset.UtcNow < s_expiresAt)
        {
            return s_token;
        }

        var (clientId, clientSecret) = GetCredentials();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "set eBay credentials with dotnet user-secrets, or run with --ignore-vendor ebay");
        }

        var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
        request.Content = new StringContent(
            FormEncode(
                ("grant_type", "client_credentials"),
                ("scope", "https://api.ebay.com/oauth/api_scope")),
            Encoding.UTF8,
            "application/x-www-form-urlencoded");

        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var payload = await JsonDocument.ParseAsync(stream);

        s_token = payload.RootElement.GetProperty("access_token").GetString()
                  ?? throw new InvalidOperationException("eBay token response did not include access_token");
        var expiresIn = payload.RootElement.GetProperty("expires_in").GetInt32();
        s_expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60);
        return s_token;
    }

    private static (string? ClientId, string? ClientSecret) GetCredentials()
    {
        var clientId = Environment.GetEnvironmentVariable("EBAY_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("EBAY_CLIENT_SECRET");
        if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
        {
            return (clientId, clientSecret);
        }

        var secrets = ReadUserSecrets();
        clientId = FirstNonEmpty(
            clientId,
            GetSecret(secrets, "Ebay", "ClientId"),
            GetSecret(secrets, "Ebay:ClientId"),
            GetSecret(secrets, "EBAY_CLIENT_ID"));
        clientSecret = FirstNonEmpty(
            clientSecret,
            GetSecret(secrets, "Ebay", "ClientSecret"),
            GetSecret(secrets, "Ebay:ClientSecret"),
            GetSecret(secrets, "EBAY_CLIENT_SECRET"));

        return (clientId, clientSecret);
    }

    private static JsonDocument? ReadUserSecrets()
    {
        var path = GetUserSecretsPath();
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonDocument.Parse(stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[eBay] Could not read user secrets — {ex.Message}");
            return null;
        }
    }

    private static string? GetUserSecretsPath()
    {
        string basePath;
        if (OperatingSystem.IsWindows())
        {
            basePath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrWhiteSpace(basePath)
                ? null
                : Path.Combine(basePath, "Microsoft", "UserSecrets", UserSecretsId, "secrets.json");
        }

        basePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(basePath)
            ? null
            : Path.Combine(basePath, ".microsoft", "usersecrets", UserSecretsId, "secrets.json");
    }

    private static string? GetSecret(JsonDocument? secrets, params string[] path)
    {
        if (secrets is null)
        {
            return null;
        }

        var current = secrets.RootElement;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static async Task<CardResult?> FetchCardAsync(string cardName, string token, bool retried = false)
    {
        var query = FormEncode(
            ("q", $"{cardName} mtg"),
            ("filter", "buyingOptions:{FIXED_PRICE}"),
            ("sort", "price"),
            ("limit", "25"));
        var url = $"{SearchUrl}?{query}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-EBAY-C-MARKETPLACE-ID", MarketplaceId);
        request.Headers.Accept.ParseAdd("application/json");

        JsonDocument data;
        try
        {
            using var response = await Http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized && !retried)
            {
                s_token = null;
                var newToken = await GetAccessTokenAsync();
                return await FetchCardAsync(cardName, newToken, retried: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                Console.Write($"ERROR — HTTP {(int)response.StatusCode} ");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            data = await JsonDocument.ParseAsync(stream);
        }
        catch (Exception ex)
        {
            Console.Write($"ERROR — {ex.Message} ");
            return null;
        }

        using (data)
        {
            if (!data.RootElement.TryGetProperty("itemSummaries", out var items) ||
                items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() == 0)
            {
                return null;
            }

            var nameKey = cardName.ToLowerInvariant().Split(',')[0].Trim();
            CardResult? best = null;

            foreach (var item in items.EnumerateArray())
            {
                var title = GetString(item, "title");
                var titleLower = title.ToLowerInvariant();

                if (!titleLower.Contains(nameKey, StringComparison.Ordinal))
                {
                    continue;
                }

                if (ExcludeTitleKeywords.Any(keyword => titleLower.Contains(keyword, StringComparison.Ordinal)))
                {
                    continue;
                }

                var condition = ExtractCondition(title, GetString(item, "condition"));
                if (ExcludeConditions.Contains(condition.ToLowerInvariant()))
                {
                    continue;
                }

                if (!item.TryGetProperty("price", out var priceElement))
                {
                    continue;
                }

                var price = GetDecimal(priceElement, "value");
                if (price is null)
                {
                    continue;
                }

                var priceCents = (int)Math.Round(price.Value * 100m);
                if (item.TryGetProperty("shippingOptions", out var shippingOptions) &&
                    shippingOptions.ValueKind == JsonValueKind.Array &&
                    shippingOptions.GetArrayLength() > 0)
                {
                    var firstShippingOption = shippingOptions[0];
                    if (firstShippingOption.TryGetProperty("shippingCost", out var shippingCost))
                    {
                        var shipping = GetDecimal(shippingCost, "value");
                        if (shipping is not null)
                        {
                            priceCents += (int)Math.Round(shipping.Value * 100m);
                        }
                    }
                }

                var cleanTitle = Regex.Replace(
                    title,
                    @"\s*[\[\(][^\]\)]*(NM|LP|MP|HP|DMG|Near Mint|Lightly Played)[^\]\)]*[\]\)]\s*",
                    "",
                    RegexOptions.IgnoreCase).Trim();

                if (best is null || priceCents < best.PriceCents)
                {
                    best = new CardResult(
                        cleanTitle.Length > 0 ? cleanTitle : title,
                        "",
                        condition,
                        1,
                        priceCents,
                        GetString(item, "itemWebUrl"));
                }
            }

            return best;
        }
    }

    private static string ExtractCondition(string title, string ebayCondition)
    {
        var titleLower = title.ToLowerInvariant();
        foreach (var (tag, label) in new[]
                 {
                     ("nm", "Near Mint"),
                     ("near mint", "Near Mint"),
                     ("lp", "Lightly Played"),
                     ("lightly played", "Lightly Played"),
                     ("mp", "Moderately Played"),
                     ("moderately played", "Moderately Played"),
                     ("hp", "Heavily Played"),
                     ("heavily played", "Heavily Played"),
                     ("dmg", "Damaged"),
                     ("damaged", "Damaged"),
                 })
        {
            if (Regex.IsMatch(titleLower, $@"\b{Regex.Escape(tag)}\b"))
            {
                return label;
            }
        }

        return ebayCondition.Length > 0 ? ebayCondition : "Unknown";
    }

    private static string FormEncode(params (string Key, string Value)[] values)
    {
        return string.Join("&", values.Select(v =>
            $"{WebUtility.UrlEncode(v.Key)}={WebUtility.UrlEncode(v.Value)}"));
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
            _ => "",
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
