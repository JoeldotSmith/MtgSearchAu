# MTG Decklist Price Search - .NET

C#/.NET 8 port of the Python command-line app in the repository root.

## Run

From the repository root:

```bash
dotnet run --project mtg-search-au
```

The app reads `decklist.txt` from the current directory or a nearby parent directory.

## Flags

```bash
dotnet run --project mtg-search-au -- --filter-price 5
dotnet run --project mtg-search-au -- --filter-diff 1.50
dotnet run --project mtg-search-au -- --ignore-vendor ck gg
dotnet run --project mtg-search-au -- --open
```

Valid `--ignore-vendor` values are `ck`, `gg`, `mm`, and `ebay`.

## eBay Credentials

The Python app has eBay credentials in source. This port does not duplicate those credentials.

To enable eBay lookups, store them with .NET user secrets:

```bash
dotnet user-secrets set "Ebay:ClientId" "your-client-id" --project mtg-search-au
dotnet user-secrets set "Ebay:ClientSecret" "your-client-secret" --project mtg-search-au
```

Or skip eBay:

```bash
dotnet run --project mtg-search-au -- --ignore-vendor ebay
```

Environment variables still work too:

```bash
export EBAY_CLIENT_ID="..."
export EBAY_CLIENT_SECRET="..."
```
