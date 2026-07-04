using System.Net;

namespace MtgPriceSearch.Vendors;

internal static class VendorAccessIssue
{
    public static string MessageFor(string vendorName)
    {
        return $"{vendorName} is temporarily blocking requests from this connection. Try again later, or untick this vendor and run the search again.";
    }

    public static bool IsLikelyBlocked(HttpStatusCode statusCode)
    {
        return statusCode is
            HttpStatusCode.Forbidden or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.ServiceUnavailable;
    }

    public static bool IsLikelyBlocked(Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: { } statusCode } &&
            IsLikelyBlocked(statusCode))
        {
            return true;
        }

        return ContainsBlockText(exception.Message);
    }

    public static bool IsLikelyBlocked(string content)
    {
        return ContainsBlockText(content);
    }

    private static bool ContainsBlockText(string value)
    {
        var text = value.ToLowerInvariant();
        return text.Contains("access denied", StringComparison.Ordinal) ||
            text.Contains("captcha", StringComparison.Ordinal) ||
            text.Contains("cloudflare", StringComparison.Ordinal) ||
            text.Contains("forbidden", StringComparison.Ordinal) ||
            text.Contains("too many requests", StringComparison.Ordinal) ||
            text.Contains("temporarily blocked", StringComparison.Ordinal) ||
            text.Contains("rate limit", StringComparison.Ordinal) ||
            text.Contains("unusual traffic", StringComparison.Ordinal) ||
            text.Contains("automated queries", StringComparison.Ordinal);
    }
}
