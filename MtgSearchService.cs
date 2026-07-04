using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using MtgPriceSearch.Vendors;

namespace MtgPriceSearch;

public sealed class MtgSearchService
{
    public static readonly IReadOnlyDictionary<string, string> VendorLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["gg"] = "Good Games",
        ["mm"] = "MTGMate",
        ["ck"] = "Card Kingdom",
        ["ebay"] = "eBay",
    };

    private static readonly IReadOnlyDictionary<string, string> VendorAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["gg"] = "goodgames",
        ["mm"] = "mtgmate",
        ["ck"] = "cardkingdom",
        ["ebay"] = "ebay",
    };

    private static readonly IReadOnlyDictionary<string, string> VendorDisplayNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["goodgames"] = "Good Games",
        ["mtgmate"] = "MTGMate",
        ["cardkingdom"] = "Card Kingdom",
        ["ebay"] = "eBay",
    };

    private static readonly IReadOnlyDictionary<string, int> Postage = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["goodgames"] = 6_50,
        ["mtgmate"] = 6_00,
        ["cardkingdom"] = 15_00,
        ["ebay"] = 0,
    };

    public async Task<MtgSearchResponse> SearchAsync(MtgSearchRequest request)
    {
        var cards = request.Cards
            .Select(card => card.Trim())
            .Where(card => card.Length > 0)
            .ToList();

        var ignoredVendorCodes = request.IgnoreVendors
            .Where(VendorAliases.ContainsKey)
            .ToHashSet(StringComparer.Ordinal);

        var activeVendors = VendorAliases
            .Where(kvp => !ignoredVendorCodes.Contains(kvp.Key))
            .Select(kvp => new VendorColumn(kvp.Value, VendorDisplayNames[kvp.Value]))
            .ToList();

        if (cards.Count == 0)
        {
            return new MtgSearchResponse([], [], [], [], activeVendors, []);
        }

        var vendorAccessProblems = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        var ckTask = ignoredVendorCodes.Contains("ck")
            ? Task.FromResult(EmptyResults())
            : FetchBulkVendorAsync("ck", () => CardKingdomClient.FetchAllAsync(cards, CreateVendorProgress("ck")));

        var ggTask = ignoredVendorCodes.Contains("gg")
            ? Task.FromResult<(Dictionary<string, CardResult> Results, Dictionary<string, int> NmPrices)>(
                (EmptyResults(), new Dictionary<string, int>(StringComparer.Ordinal)))
            : FetchGoodGamesAsync();

        var mmTask = ignoredVendorCodes.Contains("mm")
            ? Task.FromResult(EmptyResults())
            : FetchBulkVendorAsync("mm", () => MtgMateClient.FetchAllAsync(cards, CreateVendorProgress("mm")));

        var ebayTask = ignoredVendorCodes.Contains("ebay")
            ? Task.FromResult(EmptyResults())
            : FetchCardByCardVendorAsync("ebay", () => EbayClient.FetchAllAsync(cards, CreateVendorProgress("ebay")));

        await Task.WhenAll(ckTask, ggTask, mmTask, ebayTask);

        var ckResults = await ckTask;
        var (ggResults, ggNm) = await ggTask;
        var mmResults = await mmTask;
        var ebayResults = await ebayTask;

        var ignoredVendorNames = ignoredVendorCodes
            .Select(code => VendorAliases[code])
            .ToHashSet(StringComparer.Ordinal);

        var vendorResults = new Dictionary<string, Dictionary<string, CardResult>>(StringComparer.Ordinal)
        {
            ["goodgames"] = ggResults,
            ["mtgmate"] = mmResults,
            ["cardkingdom"] = ckResults,
            ["ebay"] = ebayResults,
        }
        .Where(kvp => !ignoredVendorNames.Contains(kvp.Key))
        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

        var (rows, notFound) = Merge(cards, vendorResults, ggNm);
        var (mainRows, filteredRows) = ApplyFilters(rows, request.FilterPrice, request.FilterDiff);

        var excluded = notFound
            .Select(card => card.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        if (request.FilterPrice is not null || request.FilterDiff is not null)
        {
            foreach (var row in filteredRows)
            {
                excluded.Add(row.Title.ToLowerInvariant());
            }
        }

        var orders = BuildOptimizedOrders(cards, vendorResults, excluded, request.ReturnCount);
        return new MtgSearchResponse(cards, mainRows, filteredRows, notFound, activeVendors, orders);

        async Task<Dictionary<string, CardResult>> FetchBulkVendorAsync(
            string vendorCode,
            Func<Task<Dictionary<string, CardResult>>> fetch)
        {
            ReportVendorStatus(vendorCode, $"Fetching {cards.Count} cards", 0, cards.Count);
            var results = await fetch();
            if (vendorAccessProblems.ContainsKey(vendorCode))
            {
                ReportVendorStatus(
                    vendorCode,
                    "Temporarily blocked; skipped this vendor",
                    cards.Count,
                    cards.Count,
                    isVendorAccessProblem: true);
                return results;
            }

            ReportBulkVendorResults(vendorCode, cards, results);
            ReportVendorStatus(vendorCode, "Complete", cards.Count, cards.Count);
            return results;
        }

        async Task<(Dictionary<string, CardResult> Results, Dictionary<string, int> NmPrices)> FetchGoodGamesAsync()
        {
            ReportVendorStatus("gg", $"Searching {cards.Count} cards", 0, cards.Count);
            var results = await GoodGamesClient.FetchAllAsync(cards, CreateVendorProgress("gg"));
            ReportVendorStatus(
                "gg",
                vendorAccessProblems.ContainsKey("gg")
                    ? "Temporarily blocked; skipped remaining cards"
                    : "Complete",
                cards.Count,
                cards.Count,
                vendorAccessProblems.ContainsKey("gg"));
            return results;
        }

        async Task<Dictionary<string, CardResult>> FetchCardByCardVendorAsync(
            string vendorCode,
            Func<Task<Dictionary<string, CardResult>>> fetch)
        {
            ReportVendorStatus(vendorCode, $"Searching {cards.Count} cards", 0, cards.Count);
            var results = await fetch();
            ReportVendorStatus(
                vendorCode,
                vendorAccessProblems.ContainsKey(vendorCode)
                    ? "Temporarily blocked; skipped remaining cards"
                    : "Complete",
                cards.Count,
                cards.Count,
                vendorAccessProblems.ContainsKey(vendorCode));
            return results;
        }

        Action<VendorFetchProgress>? CreateVendorProgress(string vendorCode)
        {
            return request.Progress is null
                ? null
                : progress => ReportVendorProgress(vendorCode, progress);
        }

        void ReportBulkVendorResults(
            string vendorCode,
            IReadOnlyList<string> vendorCards,
            IReadOnlyDictionary<string, CardResult> results)
        {
            var completedCards = 0;
            foreach (var cardName in vendorCards)
            {
                completedCards++;
                results.TryGetValue(cardName.ToLowerInvariant(), out var result);
                ReportVendorProgress(
                    vendorCode,
                    new VendorFetchProgress(cardName, result, completedCards, vendorCards.Count));
            }
        }

        void ReportVendorProgress(string vendorCode, VendorFetchProgress progress)
        {
            if (request.Progress is null)
            {
                return;
            }

            var vendorKey = VendorAliases[vendorCode];
            var result = progress.Result;
            var message = progress.Message ?? (result is null ? "Not found" : "Found");
            if (progress.IsVendorAccessProblem)
            {
                vendorAccessProblems[vendorCode] = message;
            }

            request.Progress.Report(new MtgSearchProgressUpdate(
                vendorKey,
                VendorDisplayNames[vendorKey],
                progress.CardName,
                message,
                progress.CompletedCards,
                progress.TotalCards,
                result is not null,
                result is null ? null : FormatMoney(result.PriceCents),
                result?.SetName,
                result?.Condition,
                result?.Url,
                progress.IsVendorAccessProblem));
        }

        void ReportVendorStatus(
            string vendorCode,
            string message,
            int completedCards,
            int totalCards,
            bool isVendorAccessProblem = false)
        {
            if (request.Progress is null)
            {
                return;
            }

            var vendorKey = VendorAliases[vendorCode];
            request.Progress.Report(new MtgSearchProgressUpdate(
                vendorKey,
                VendorDisplayNames[vendorKey],
                null,
                message,
                completedCards,
                totalCards,
                false,
                null,
                null,
                null,
                null,
                isVendorAccessProblem));
        }
    }

    public static IReadOnlyList<string> ParseDecklist(string decklist)
    {
        return decklist
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => Regex.Replace(line, @"^\d+\s+", ""))
            .Where(cardName => cardName.Length > 0)
            .ToList();
    }

    public static string FormatMoney(int cents)
    {
        return (cents / 100.0).ToString("$0.00", CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, CardResult> EmptyResults()
    {
        return new Dictionary<string, CardResult>(StringComparer.Ordinal);
    }

    private static (List<SearchResultRow> Rows, List<string> NotFound) Merge(
        IReadOnlyList<string> cards,
        IReadOnlyDictionary<string, Dictionary<string, CardResult>> vendorResults,
        IReadOnlyDictionary<string, int> goodGamesNmPrices)
    {
        var rows = new List<SearchResultRow>();
        var notFound = new List<string>();

        foreach (var cardName in cards)
        {
            var key = cardName.ToLowerInvariant();
            CardResult? bestResult = null;
            string? bestVendor = null;
            var availablePrices = new List<(string VendorName, CardResult Result)>();

            foreach (var (vendorName, results) in vendorResults)
            {
                if (!results.TryGetValue(key, out var result))
                {
                    continue;
                }

                availablePrices.Add((vendorName, result));
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

            var rankedPrices = availablePrices
                .OrderBy(price => price.Result.PriceCents)
                .ThenBy(price => VendorDisplayNames[price.VendorName], StringComparer.Ordinal)
                .Select((price, index) => new VendorPriceOption(
                    price.VendorName,
                    VendorDisplayNames[price.VendorName],
                    price.Result.CardName,
                    price.Result.SetName,
                    price.Result.Condition,
                    price.Result.Qty,
                    price.Result.PriceCents,
                    FormatMoney(price.Result.PriceCents),
                    price.Result.Url,
                    index + 1,
                    price.Result.PriceCents == bestResult.PriceCents))
                .ToList();

            var otherVendorCount = availablePrices.Count(price => price.VendorName != bestVendor);
            var source = VendorDisplayNames[bestVendor] + (otherVendorCount > 0 ? $" (+{otherVendorCount})" : "");

            goodGamesNmPrices.TryGetValue(key, out var nmCents);
            var hasNmPrice = goodGamesNmPrices.ContainsKey(key);
            var nmString = hasNmPrice ? FormatMoney(nmCents) : "N/A";
            var diffCents = hasNmPrice ? bestResult.PriceCents - nmCents : 0;
            var sign = diffCents >= 0 ? "+" : "-";

            rows.Add(new SearchResultRow(
                bestResult.CardName,
                bestResult.SetName,
                bestResult.Condition,
                bestResult.Qty,
                bestResult.PriceCents,
                FormatMoney(bestResult.PriceCents),
                nmString,
                $"{sign}{FormatMoney(Math.Abs(diffCents))}",
                source,
                rankedPrices,
                bestResult.Url));
        }

        rows.Sort((left, right) => left.PriceCents.CompareTo(right.PriceCents));
        return (rows, notFound);
    }

    private static (List<SearchResultRow> MainRows, List<SearchResultRow> FilteredRows) ApplyFilters(
        IReadOnlyList<SearchResultRow> rows,
        double? filterPrice,
        double? filterDiff)
    {
        var mainRows = rows.ToList();
        var filteredRows = new List<SearchResultRow>();

        if (filterPrice is not null)
        {
            var threshold = (int)(filterPrice.Value * 100);
            filteredRows.AddRange(mainRows.Where(r => r.PriceCents >= threshold));
            mainRows = mainRows.Where(r => r.PriceCents < threshold).ToList();
        }

        if (filterDiff is not null)
        {
            var threshold = (int)(filterDiff.Value * 100);
            filteredRows.AddRange(mainRows.Where(r => DiffCents(r.DiffString) >= threshold));
            mainRows = mainRows.Where(r => DiffCents(r.DiffString) < threshold).ToList();
        }

        return (mainRows, filteredRows);
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

    private static IReadOnlyList<OptimizedOrder> BuildOptimizedOrders(
        IReadOnlyList<string> cards,
        IReadOnlyDictionary<string, Dictionary<string, CardResult>> vendorResults,
        IReadOnlySet<string> excludedCards,
        int returnCount)
    {
        if (returnCount <= 0)
        {
            return [];
        }

        var vendorNames = vendorResults.Keys.ToList();
        var activeCards = cards.Where(c => !excludedCards.Contains(c.ToLowerInvariant())).ToList();
        var sourceable = activeCards
            .Where(c => vendorNames.Any(v => vendorResults[v].ContainsKey(c.ToLowerInvariant())))
            .Select(c => c.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var solutions = new Dictionary<string, SearchOrderSolution>(StringComparer.Ordinal);

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
                var solution = new SearchOrderSolution(coverage, total, config);
                var signature = BuildSolutionSignature(config);

                if (!solutions.TryGetValue(signature, out var existing) ||
                    IsBetterOrderSolution(solution, existing))
                {
                    solutions[signature] = solution;
                }
            }
        }

        if (solutions.Count == 0)
        {
            return [];
        }

        return solutions.Values
            .OrderByDescending(solution => solution.Coverage)
            .ThenBy(solution => solution.TotalCostCents)
            .ThenBy(solution => CountVendors(solution.Config))
            .ThenBy(solution => BuildSolutionSignature(solution.Config), StringComparer.Ordinal)
            .Take(returnCount)
            .Select(solution => BuildOptimizedOrder(vendorNames, vendorResults, activeCards, solution))
            .ToList();
    }

    private static OptimizedOrder BuildOptimizedOrder(
        IReadOnlyList<string> vendorNames,
        IReadOnlyDictionary<string, Dictionary<string, CardResult>> vendorResults,
        IReadOnlyList<string> activeCards,
        SearchOrderSolution solution)
    {
        var vendorCards = vendorNames.ToDictionary(v => v, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (cardKey, vendor) in solution.Config)
        {
            vendorCards[vendor].Add(cardKey);
        }

        var vendorOrders = new List<VendorOrder>();
        foreach (var vendor in vendorNames)
        {
            var cardList = vendorCards[vendor];
            if (cardList.Count == 0)
            {
                continue;
            }

            var results = vendorResults[vendor];
            var postage = Postage[vendor];
            var orderLines = cardList
                .OrderBy(cardKey => results[cardKey].PriceCents)
                .Select(cardKey =>
                {
                    var result = results[cardKey];
                    return new VendorOrderLine(
                        result.CardName,
                        result.SetName,
                        result.Condition,
                        result.Qty,
                        result.PriceCents,
                        FormatMoney(result.PriceCents),
                        result.Url);
                })
                .ToList();

            var cardsTotal = orderLines.Sum(line => line.PriceCents);
            var orderTotal = cardsTotal + postage;

            vendorOrders.Add(new VendorOrder(
                vendor,
                VendorDisplayNames[vendor],
                orderLines,
                postage,
                FormatMoney(postage),
                cardsTotal,
                FormatMoney(cardsTotal),
                orderTotal,
                FormatMoney(orderTotal)));
        }

        var unsourceable = activeCards
            .Where(c => !solution.Config.ContainsKey(c.ToLowerInvariant()))
            .ToList();

        return new OptimizedOrder(
            solution.Coverage,
            solution.TotalCostCents,
            FormatMoney(solution.TotalCostCents),
            vendorOrders,
            unsourceable);
    }

    private static bool IsBetterOrderSolution(SearchOrderSolution candidate, SearchOrderSolution existing)
    {
        return candidate.Coverage > existing.Coverage ||
            (candidate.Coverage == existing.Coverage && candidate.TotalCostCents < existing.TotalCostCents);
    }

    private static int CountVendors(IReadOnlyDictionary<string, string> config)
    {
        return config.Values.ToHashSet(StringComparer.Ordinal).Count;
    }

    private static string BuildSolutionSignature(IReadOnlyDictionary<string, string> config)
    {
        return string.Join(
            "\n",
            config
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Select(kvp => $"{kvp.Key}={kvp.Value}"));
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

    private sealed record SearchOrderSolution(
        int Coverage,
        int TotalCostCents,
        Dictionary<string, string> Config);
}

public sealed record MtgSearchRequest(IReadOnlyList<string> Cards)
{
    public double? FilterPrice { get; init; }
    public double? FilterDiff { get; init; }
    public int ReturnCount { get; init; } = 1;
    public IReadOnlySet<string> IgnoreVendors { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IProgress<MtgSearchProgressUpdate>? Progress { get; init; }
}

public sealed record MtgSearchProgressUpdate(
    string VendorKey,
    string VendorName,
    string? CardName,
    string Message,
    int CompletedCards,
    int TotalCards,
    bool IsFound,
    string? PriceString,
    string? Set,
    string? Condition,
    string? Url,
    bool IsVendorAccessProblem);

public sealed record MtgSearchResponse(
    IReadOnlyList<string> Cards,
    IReadOnlyList<SearchResultRow> Rows,
    IReadOnlyList<SearchResultRow> FilteredRows,
    IReadOnlyList<string> NotFound,
    IReadOnlyList<VendorColumn> Vendors,
    IReadOnlyList<OptimizedOrder> OptimizedOrders)
{
    public OptimizedOrder? OptimizedOrder => OptimizedOrders.FirstOrDefault();
    public int TotalCards => Cards.Count;
    public int VisibleTotalCents => Rows.Sum(row => row.PriceCents);
    public string VisibleTotalString => MtgSearchService.FormatMoney(VisibleTotalCents);
}

public sealed record VendorColumn(string Key, string Name);

public sealed record SearchResultRow(
    string Title,
    string Set,
    string Condition,
    int Quantity,
    int PriceCents,
    string PriceString,
    string NmString,
    string DiffString,
    string Source,
    IReadOnlyList<VendorPriceOption> VendorPrices,
    string? Url)
{
    public VendorPriceOption? PriceForVendor(string vendorKey)
    {
        return VendorPrices.FirstOrDefault(price => price.VendorKey == vendorKey);
    }

    public VendorPriceOption? RankedPrice(int rank)
    {
        return VendorPrices.FirstOrDefault(price => price.Rank == rank);
    }
}

public sealed record VendorPriceOption(
    string VendorKey,
    string VendorName,
    string CardName,
    string Set,
    string Condition,
    int Quantity,
    int PriceCents,
    string PriceString,
    string? Url,
    int Rank,
    bool IsCheapest);

public sealed record OptimizedOrder(
    int Coverage,
    int TotalCostCents,
    string TotalCostString,
    IReadOnlyList<VendorOrder> VendorOrders,
    IReadOnlyList<string> UnsourceableCards);

public sealed record VendorOrder(
    string VendorKey,
    string VendorName,
    IReadOnlyList<VendorOrderLine> Lines,
    int PostageCents,
    string PostageString,
    int CardsTotalCents,
    string CardsTotalString,
    int OrderTotalCents,
    string OrderTotalString);

public sealed record VendorOrderLine(
    string Title,
    string Set,
    string Condition,
    int Quantity,
    int PriceCents,
    string PriceString,
    string? Url);
