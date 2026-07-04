using System.Diagnostics;
using System.Globalization;
using System.Text;
using MtgPriceSearch.Vendors;

namespace MtgPriceSearch;

internal static class Program
{
    private static readonly Dictionary<string, int> Postage = new(StringComparer.Ordinal)
    {
        ["goodgames"] = 6_50,
        ["mtgmate"] = 6_00,
        ["cardkingdom"] = 15_00,
        ["ebay"] = 0,
    };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (!TryParseArgs(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            PrintUsage();
            return 2;
        }

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        var decklistPath = FindDecklistFile();
        var cards = GetCards(decklistPath);
        Console.WriteLine($"Total cards: {cards.Count}\n");

        var ckResults = options.IgnoreVendors.Contains("ck")
            ? new Dictionary<string, CardResult>()
            : await CardKingdomClient.FetchAllAsync(cards);
        Console.WriteLine();

        Dictionary<string, CardResult> ggResults;
        Dictionary<string, int> ggNm;
        if (options.IgnoreVendors.Contains("gg"))
        {
            ggResults = new Dictionary<string, CardResult>();
            ggNm = new Dictionary<string, int>();
        }
        else
        {
            (ggResults, ggNm) = await GoodGamesClient.FetchAllAsync(cards);
        }

        Console.WriteLine();

        var mmResults = options.IgnoreVendors.Contains("mm")
            ? new Dictionary<string, CardResult>()
            : await MtgMateClient.FetchAllAsync(cards);
        Console.WriteLine();

        var ebayResults = options.IgnoreVendors.Contains("ebay")
            ? new Dictionary<string, CardResult>()
            : await EbayClient.FetchAllAsync(cards);

        var vendorAliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gg"] = "goodgames",
            ["mm"] = "mtgmate",
            ["ck"] = "cardkingdom",
            ["ebay"] = "ebay",
        };

        var ignored = options.IgnoreVendors.Select(v => vendorAliases[v]).ToHashSet(StringComparer.Ordinal);
        var vendorResults = new Dictionary<string, Dictionary<string, CardResult>>(StringComparer.Ordinal)
        {
            ["goodgames"] = ggResults,
            ["mtgmate"] = mmResults,
            ["cardkingdom"] = ckResults,
            ["ebay"] = ebayResults,
        }
        .Where(kvp => !ignored.Contains(kvp.Key))
        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

        var (rows, notFound) = Merge(cards, vendorResults, ggNm);
        var (mainRows, overRows) = ApplyFilters(rows, options.FilterPrice, options.FilterDiff);

        DrawTable("MTG Card Price Search — GoodGames + MTGMate", mainRows, cards.Count);
        if (overRows.Count > 0)
        {
            DrawTable("Filtered Out", overRows, cards.Count);
        }

        if (options.Open && mainRows.Count > 0)
        {
            Console.WriteLine("\nOpening results in browser...");
            foreach (var row in mainRows)
            {
                if (!string.IsNullOrWhiteSpace(row.Url))
                {
                    OpenUrl(row.Url);
                    await Task.Delay(300);
                }
            }
        }

        if (notFound.Count > 0)
        {
            Console.WriteLine($"\n── Not Found / Out of Stock [{notFound.Count}/{cards.Count}] ──");
            foreach (var card in notFound)
            {
                Console.WriteLine($"  1 {card}");
            }
        }

        var excluded = notFound.Select(c => c.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        if (options.FilterPrice is not null || options.FilterDiff is not null)
        {
            foreach (var row in overRows)
            {
                excluded.Add(row.Title.ToLowerInvariant());
            }
        }

        OptimiseOrder(cards, vendorResults, excluded);
        return 0;
    }

    private static List<string> GetCards(string decklistFile)
    {
        var cards = new List<string>();
        foreach (var rawLine in File.ReadLines(decklistFile))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var cardName = System.Text.RegularExpressions.Regex.Replace(line, @"^\d+\s+", "");
            cards.Add(cardName);
        }

        return cards;
    }

    private static (List<DisplayRow> Rows, List<string> NotFound) Merge(
        IReadOnlyList<string> cards,
        IReadOnlyDictionary<string, Dictionary<string, CardResult>> vendorResults,
        IReadOnlyDictionary<string, int> goodGamesNmPrices)
    {
        var rows = new List<DisplayRow>();
        var notFound = new List<string>();

        foreach (var cardName in cards)
        {
            var key = cardName.ToLowerInvariant();
            CardResult? bestResult = null;
            string? bestVendor = null;
            var availableVendors = new List<string>();

            foreach (var (vendorName, results) in vendorResults)
            {
                if (!results.TryGetValue(key, out var result))
                {
                    continue;
                }

                availableVendors.Add(vendorName);
                if (bestResult is null || result.PriceCents < bestResult.PriceCents)
                {
                    bestResult = result;
                    bestVendor = vendorName;
                }
            }

            if (bestResult is null || bestVendor is null)
            {
                notFound.Add(cardName);
                continue;
            }

            var otherVendorCount = availableVendors.Count(v => v != bestVendor);
            var source = bestVendor + (otherVendorCount > 0 ? $" (+{otherVendorCount})" : "");

            goodGamesNmPrices.TryGetValue(key, out var nmCents);
            var hasNmPrice = goodGamesNmPrices.ContainsKey(key);
            var nmString = hasNmPrice ? FormatMoney(nmCents) : "N/A";
            var diffCents = hasNmPrice ? bestResult.PriceCents - nmCents : 0;
            var sign = diffCents >= 0 ? "+" : "-";

            rows.Add(new DisplayRow(
                bestResult.CardName,
                bestResult.SetName,
                bestResult.Condition,
                bestResult.Qty.ToString(CultureInfo.InvariantCulture),
                bestResult.PriceCents,
                FormatMoney(bestResult.PriceCents),
                nmString,
                $"{sign}{FormatMoney(Math.Abs(diffCents))}",
                source,
                bestResult.Url));
        }

        rows.Sort((left, right) => left.Price.CompareTo(right.Price));
        return (rows, notFound);
    }

    private static (List<DisplayRow> MainRows, List<DisplayRow> OverRows) ApplyFilters(
        IReadOnlyList<DisplayRow> rows,
        double? filterPrice,
        double? filterDiff)
    {
        var mainRows = rows.ToList();
        var overRows = new List<DisplayRow>();

        if (filterPrice is not null)
        {
            var threshold = (int)(filterPrice.Value * 100);
            overRows.AddRange(mainRows.Where(r => r.Price >= threshold));
            mainRows = mainRows.Where(r => r.Price < threshold).ToList();
        }

        if (filterDiff is not null)
        {
            var threshold = (int)(filterDiff.Value * 100);
            overRows.AddRange(mainRows.Where(r => DiffCents(r.DiffString) >= threshold));
            mainRows = mainRows.Where(r => DiffCents(r.DiffString) < threshold).ToList();
        }

        return (mainRows, overRows);
    }

    private static int DiffCents(string diffString)
    {
        var normalized = diffString
            .Replace("+", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("$", "", StringComparison.Ordinal)
            .Replace(".", "", StringComparison.Ordinal);
        return int.Parse(normalized, CultureInfo.InvariantCulture);
    }

    private static void DrawTable(string title, IReadOnlyList<DisplayRow> rows, int totalCards)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var headers = new[]
        {
            "Card Title", "Set", "Condition", "Qty", "Cheapest Available", "Cheapest NM", "Diff", "Source",
        };
        var display = rows
            .Select(r => new[]
            {
                r.Title, r.Set, r.Condition, r.Qty, r.PriceString, r.NmString, r.DiffString, r.Source,
            })
            .ToList();

        var colWidths = GetColumnWidths(headers, display);
        var totalWidth = colWidths.Sum() + 3 * colWidths.Count + 1;
        var titlePadded = Center($" {title} ({rows.Count}/{totalCards} cards) ", totalWidth - 2);

        Console.WriteLine();
        Console.WriteLine("┌" + titlePadded + "┐");
        Console.WriteLine(Divider("├", "┬", "┤", colWidths));
        Console.WriteLine(FormatRow(headers, colWidths));
        Console.WriteLine(Divider("├", "┼", "┤", colWidths));
        foreach (var row in display)
        {
            Console.WriteLine(FormatRow(row, colWidths));
        }

        Console.WriteLine(Divider("├", "┼", "┤", colWidths));

        var totalPrice = rows.Sum(r => r.Price);
        var totalNm = rows
            .Where(r => r.NmString != "N/A")
            .Sum(r => int.Parse(r.NmString.Replace("$", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal), CultureInfo.InvariantCulture));
        var totalRow = new[] { "", "", "", "Total", FormatMoney(totalPrice), FormatMoney(totalNm), "", "" };
        Console.WriteLine(FormatRow(totalRow, colWidths));
        Console.WriteLine(Divider("└", "┴", "┘", colWidths));
    }

    private static void OptimiseOrder(
        IReadOnlyList<string> cards,
        IReadOnlyDictionary<string, Dictionary<string, CardResult>> vendorResults,
        IReadOnlySet<string> excludedCards)
    {
        var vendorNames = vendorResults.Keys.ToList();
        var activeCards = cards.Where(c => !excludedCards.Contains(c.ToLowerInvariant())).ToList();
        var sourceable = activeCards
            .Where(c => vendorNames.Any(v => vendorResults[v].ContainsKey(c.ToLowerInvariant())))
            .Select(c => c.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        OrderSolution? bestSolution = null;

        for (var size = 1; size <= vendorNames.Count; size++)
        {
            foreach (var vendorSubset in Combinations(vendorNames, size))
            {
                var config = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var cardKey in sourceable)
                {
                    string? cheapestVendor = null;
                    int? cheapestPrice = null;

                    foreach (var vendor in vendorSubset)
                    {
                        if (!vendorResults[vendor].TryGetValue(cardKey, out var result))
                        {
                            continue;
                        }

                        if (cheapestPrice is null || result.PriceCents < cheapestPrice)
                        {
                            cheapestPrice = result.PriceCents;
                            cheapestVendor = vendor;
                        }
                    }

                    if (cheapestVendor is not null)
                    {
                        config[cardKey] = cheapestVendor;
                    }
                }

                var vendorsUsed = config.Values.ToHashSet(StringComparer.Ordinal);
                var cardsTotal = config.Sum(kvp => vendorResults[kvp.Value][kvp.Key].PriceCents);
                var postageTotal = vendorsUsed.Sum(v => Postage[v]);
                var total = cardsTotal + postageTotal;
                var coverage = config.Count;

                if (bestSolution is null ||
                    coverage > bestSolution.Coverage ||
                    (coverage == bestSolution.Coverage && total < bestSolution.TotalCost))
                {
                    bestSolution = new OrderSolution(coverage, total, config);
                }
            }
        }

        if (bestSolution is null)
        {
            Console.WriteLine("\nNo cards could be sourced from any vendor.");
            return;
        }

        var vendorCards = vendorNames.ToDictionary(v => v, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (cardKey, vendor) in bestSolution.Config)
        {
            vendorCards[vendor].Add(cardKey);
        }

        var unsourceable = activeCards.Where(c => !bestSolution.Config.ContainsKey(c.ToLowerInvariant())).ToList();

        Console.WriteLine("\n" + new string('═', 60));
        Console.WriteLine($"  OPTIMAL ORDER  —  total incl. postage: {FormatMoney(bestSolution.TotalCost)}");
        Console.WriteLine(new string('═', 60));

        foreach (var vendor in vendorNames)
        {
            var cardList = vendorCards[vendor];
            if (cardList.Count == 0)
            {
                continue;
            }

            var results = vendorResults[vendor];
            var postage = Postage[vendor];
            var cardsCost = cardList.Sum(cardKey => results[cardKey].PriceCents);
            var orderTotal = cardsCost + postage;
            var rows = cardList
                .OrderBy(cardKey => results[cardKey].PriceCents)
                .Select(cardKey =>
                {
                    var result = results[cardKey];
                    return new[]
                    {
                        result.CardName,
                        result.SetName,
                        result.Condition,
                        result.Qty.ToString(CultureInfo.InvariantCulture),
                        FormatMoney(result.PriceCents),
                    };
                })
                .ToList();

            DrawOrderTable(
                $"Order from {CultureInfo.InvariantCulture.TextInfo.ToTitleCase(vendor)}",
                rows,
                postage,
                cardsCost,
                orderTotal);
        }

        if (unsourceable.Count > 0)
        {
            Console.WriteLine($"\n── Still unavailable ({unsourceable.Count}/{cards.Count}) ──");
            foreach (var card in unsourceable)
            {
                Console.WriteLine($"  1 {card}");
            }
        }
    }

    private static void DrawOrderTable(
        string title,
        IReadOnlyList<string[]> rows,
        int postage,
        int cardsTotal,
        int orderTotal)
    {
        var headers = new[] { "Card Title", "Set", "Condition", "Qty", "Price" };
        var colWidths = GetColumnWidths(headers, rows);
        var totalWidth = colWidths.Sum() + 3 * colWidths.Count + 1;
        var titlePadded = Center($" {title} ({rows.Count} cards) ", totalWidth - 2);

        Console.WriteLine();
        Console.WriteLine("┌" + titlePadded + "┐");
        Console.WriteLine(Divider("├", "┬", "┤", colWidths));
        Console.WriteLine(FormatRow(headers, colWidths));
        Console.WriteLine(Divider("├", "┼", "┤", colWidths));
        foreach (var row in rows)
        {
            Console.WriteLine(FormatRow(row, colWidths));
        }

        Console.WriteLine(Divider("├", "┼", "┤", colWidths));
        Console.WriteLine(FormatRow(new[] { "", "", "", "Cards", FormatMoney(cardsTotal) }, colWidths));
        Console.WriteLine(FormatRow(new[] { "", "", "", "Post", FormatMoney(postage) }, colWidths));
        Console.WriteLine(Divider("├", "┼", "┤", colWidths));
        Console.WriteLine(FormatRow(new[] { "", "", "", "Total", FormatMoney(orderTotal) }, colWidths));
        Console.WriteLine(Divider("└", "┴", "┘", colWidths));
    }

    private static List<int> GetColumnWidths(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var widths = headers.Select(h => h.Length).ToList();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        return widths.Select(w => w + 2).ToList();
    }

    private static string FormatRow(IReadOnlyList<string> row, IReadOnlyList<int> colWidths)
    {
        var cells = row
            .Select((cell, index) => index < row.Count - 1
                ? cell.PadRight(colWidths[index])
                : cell.PadLeft(colWidths[index]));
        return "│ " + string.Join(" │ ", cells) + " │";
    }

    private static string Divider(string left, string mid, string right, IReadOnlyList<int> colWidths)
    {
        return left + string.Join(mid, colWidths.Select(width => new string('─', width + 2))) + right;
    }

    private static IEnumerable<IReadOnlyList<string>> Combinations(IReadOnlyList<string> values, int size)
    {
        var selected = new string[size];

        IEnumerable<IReadOnlyList<string>> Walk(int start, int depth)
        {
            if (depth == size)
            {
                yield return selected.ToArray();
                yield break;
            }

            for (var i = start; i <= values.Count - (size - depth); i++)
            {
                selected[depth] = values[i];
                foreach (var combination in Walk(i + 1, depth + 1))
                {
                    yield return combination;
                }
            }
        }

        return Walk(0, 0);
    }

    private static string FormatMoney(int cents)
    {
        return (cents / 100.0).ToString("$0.00", CultureInfo.InvariantCulture);
    }

    private static string Center(string value, int width)
    {
        if (value.Length >= width)
        {
            return value;
        }

        var left = (width - value.Length) / 2;
        var right = width - value.Length - left;
        return new string(' ', left) + value + new string(' ', right);
    }

    private static string FindDecklistFile()
    {
        var current = Directory.GetCurrentDirectory();
        for (var i = 0; i < 5; i++)
        {
            var candidate = Path.Combine(current, "decklist.txt");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new FileNotFoundException("Could not find decklist.txt in the current directory or nearby parent directories.");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"") { CreateNoWindow = true });
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not open {url}: {ex.Message}");
        }
    }

    private static bool TryParseArgs(string[] args, out Options options, out string? error)
    {
        options = new Options();
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    break;

                case "--filter-price":
                    if (!ReadDoubleValue(args, ref i, out var filterPrice, out error))
                    {
                        return false;
                    }

                    options.FilterPrice = filterPrice;
                    break;

                case "--filter-diff":
                    if (!ReadDoubleValue(args, ref i, out var filterDiff, out error))
                    {
                        return false;
                    }

                    options.FilterDiff = filterDiff;
                    break;

                case "--open":
                    options.Open = true;
                    break;

                case "--ignore-vendor":
                    var foundVendor = false;
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        var vendor = args[++i];
                        if (!Options.ValidVendors.Contains(vendor))
                        {
                            error = $"Unknown vendor '{vendor}'. Valid values are: ck, gg, mm, ebay.";
                            return false;
                        }

                        options.IgnoreVendors.Add(vendor);
                        foundVendor = true;
                    }

                    if (!foundVendor)
                    {
                        error = "--ignore-vendor requires at least one vendor: ck, gg, mm, ebay.";
                        return false;
                    }
                    break;

                default:
                    error = $"Unknown argument '{arg}'.";
                    return false;
            }
        }

        return true;
    }

    private static bool ReadDoubleValue(string[] args, ref int index, out double? value, out string? error)
    {
        value = null;
        error = null;

        if (index + 1 >= args.Length)
        {
            error = $"{args[index]} requires a numeric value.";
            return false;
        }

        var rawValue = args[++index];
        if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"{args[index - 1]} value '{rawValue}' is not a number.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
Usage:
  dotnet run --project mtg-search-au -- [--filter-price AMOUNT] [--filter-diff AMOUNT] [--open] [--ignore-vendor ck gg mm ebay]

Examples:
  dotnet run --project mtg-search-au
  dotnet run --project mtg-search-au -- --filter-price 5
  dotnet run --project mtg-search-au -- --ignore-vendor ck ebay
""");
    }
}

internal sealed class Options
{
    public static readonly HashSet<string> ValidVendors = ["ck", "gg", "mm", "ebay"];

    public double? FilterPrice { get; set; }
    public double? FilterDiff { get; set; }
    public bool Open { get; set; }
    public bool ShowHelp { get; set; }
    public HashSet<string> IgnoreVendors { get; } = new(StringComparer.Ordinal);
}

internal sealed record DisplayRow(
    string Title,
    string Set,
    string Condition,
    string Qty,
    int Price,
    string PriceString,
    string NmString,
    string DiffString,
    string Source,
    string? Url);

internal sealed record OrderSolution(
    int Coverage,
    int TotalCost,
    Dictionary<string, string> Config);
