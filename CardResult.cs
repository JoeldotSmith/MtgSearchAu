namespace MtgPriceSearch;

internal sealed record CardResult(
    string CardName,
    string SetName,
    string Condition,
    int Qty,
    int PriceCents,
    string? Url = null);
