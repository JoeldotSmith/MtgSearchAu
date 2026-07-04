namespace MtgPriceSearch;

internal sealed record CardResult(
    string CardName,
    string SetName,
    string Condition,
    int Qty,
    int PriceCents,
    string? Url = null);

internal sealed record VendorFetchProgress(
    string? CardName,
    CardResult? Result,
    int CompletedCards,
    int TotalCards,
    string? Message = null,
    bool IsVendorAccessProblem = false);
