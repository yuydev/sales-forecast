namespace SalesForecast;

public static class SalesValueNormalizer
{
    public static double NormalizeQuantity(double quantity) => Math.Max(0, quantity);
    public static DateTime NormalizeMonth(DateTime month) => new(month.Year, month.Month, 1);
}
