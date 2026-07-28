namespace SalesForecast;

public static class MarketKeyNormalizer
{
    public static string NormalizeMarket(string market, string businessUnit)
    {
        var normalizedMarket = (market ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedMarket))
            return normalizedMarket;
        return (businessUnit ?? string.Empty).Trim();
    }
}
