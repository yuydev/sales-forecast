namespace SalesForecast;

internal static class ForecastSplitPolicy
{
    public static (int TrainLength, int ValidationLength, int TestLength) GetSplit(int monthCount, int defaultTrainLength, int defaultHorizon)
    {
        if (monthCount >= defaultTrainLength + defaultHorizon)
            return (defaultTrainLength - defaultHorizon, defaultHorizon, defaultHorizon);
        if (monthCount < 6)
            return (monthCount - 2, 0, 2);
        var validationLength = Math.Max(2, (int)Math.Round(monthCount / 4d, MidpointRounding.AwayFromZero));
        return (monthCount - validationLength, 0, validationLength);
    }
}
