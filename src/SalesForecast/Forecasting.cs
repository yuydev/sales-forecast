namespace SalesForecast;

public enum ForecastModelType
{
    Simple,
    Holt,
    HoltWintersAdditive,
    HoltWintersMultiplicative
}

public sealed class ForecastMetrics
{
    public double Smape { get; init; }
    public double Wape { get; init; }
    public double Mae { get; init; }
}

public sealed class ForecastResult
{
    public ForecastModelType ModelType { get; init; }
    public double Alpha { get; init; }
    public double Beta { get; init; }
    public double Gamma { get; init; }
    public int SeasonLength { get; init; }
    public ForecastMetrics ValidationMetrics { get; init; } = new();
    public double[] Forecast { get; init; } = Array.Empty<double>();
    public bool SeasonalModelAccepted { get; init; }
    public double SeasonalImprovement { get; init; }
}

public static class HoltWintersForecaster
{
    private const double Epsilon = 1e-8;
    private const double SeasonalImprovementThreshold = 0.05;

    public static ForecastResult SearchBest(IReadOnlyList<double> values, int trainLength = 30, int horizon = 6, int seasonLength = 12)
    {
        if (values.Count < trainLength + horizon) throw new ArgumentException("Insufficient observations.");
        var candidates = new List<ForecastResult>();

        foreach (var alpha in Parameters())
        {
            candidates.Add(Evaluate(values, ForecastModelType.Simple, alpha, 0, 0, 0, horizon, trainLength));
            foreach (var beta in Parameters())
            {
                candidates.Add(Evaluate(values, ForecastModelType.Holt, alpha, beta, 0, 0, horizon, trainLength));
                if (trainLength < seasonLength * 2) continue;
                foreach (var gamma in Parameters())
                {
                    candidates.Add(Evaluate(values, ForecastModelType.HoltWintersAdditive, alpha, beta, gamma, seasonLength, horizon, trainLength));
                    candidates.Add(Evaluate(values, ForecastModelType.HoltWintersMultiplicative, alpha, beta, gamma, seasonLength, horizon, trainLength));
                }
            }
        }

        var valid = candidates.Where(x => x.ValidationMetrics.Smape < double.MaxValue).ToList();
        if (valid.Count == 0) throw new InvalidOperationException("No valid forecasting model was found.");
        var nonSeasonal = valid.Where(x => !IsSeasonal(x.ModelType)).OrderBy(x => x.ValidationMetrics.Smape).First();
        var seasonal = valid.Where(x => IsSeasonal(x.ModelType)).OrderBy(x => x.ValidationMetrics.Smape).FirstOrDefault();
        var improvement = seasonal is null || nonSeasonal.ValidationMetrics.Smape <= Epsilon
            ? 0
            : (nonSeasonal.ValidationMetrics.Smape - seasonal.ValidationMetrics.Smape) / nonSeasonal.ValidationMetrics.Smape;
        var selected = seasonal is not null && improvement >= SeasonalImprovementThreshold ? seasonal : nonSeasonal;
        var forecast = Forecast(values.Take(trainLength).ToArray(), selected.ModelType, selected.Alpha, selected.Beta, selected.Gamma, selected.SeasonLength, horizon);
        return new ForecastResult
        {
            ModelType = selected.ModelType, Alpha = selected.Alpha, Beta = selected.Beta, Gamma = selected.Gamma,
            SeasonLength = selected.SeasonLength, ValidationMetrics = selected.ValidationMetrics, Forecast = forecast,
            SeasonalModelAccepted = selected == seasonal && seasonal is not null, SeasonalImprovement = improvement
        };
    }

    public static ForecastMetrics CalculateMetrics(IReadOnlyList<double> actual, IReadOnlyList<double> predicted)
    {
        var count = Math.Min(actual.Count, predicted.Count);
        if (count == 0) return new ForecastMetrics { Smape = double.MaxValue, Wape = double.MaxValue, Mae = double.MaxValue };
        double abs = 0, actualTotal = 0, smape = 0; var smapeCount = 0;
        for (var i = 0; i < count; i++)
        {
            var a = Math.Max(0, actual[i]); var p = Math.Max(0, predicted[i]); var e = Math.Abs(a - p);
            abs += e; actualTotal += a; var d = a + p;
            if (d > Epsilon) { smape += 2 * e / d; smapeCount++; }
        }
        return new ForecastMetrics { Smape = smapeCount == 0 ? double.MaxValue : smape / smapeCount, Wape = actualTotal <= Epsilon ? double.MaxValue : abs / actualTotal, Mae = abs / count };
    }

    public static double[] Forecast(IReadOnlyList<double> values, ForecastModelType type, double alpha, double beta, double gamma, int seasonLength, int horizon) => type switch
    {
        ForecastModelType.Simple => Simple(values, alpha, horizon),
        ForecastModelType.Holt => Holt(values, alpha, beta, horizon),
        ForecastModelType.HoltWintersAdditive => HoltWinters(values, alpha, beta, gamma, seasonLength, horizon, false),
        ForecastModelType.HoltWintersMultiplicative => HoltWinters(values, alpha, beta, gamma, seasonLength, horizon, true),
        _ => throw new NotSupportedException()
    };

    private static ForecastResult Evaluate(IReadOnlyList<double> values, ForecastModelType type, double alpha, double beta, double gamma, int seasonLength, int horizon, int finalTrainLength)
    {
        var actual = new List<double>(); var predicted = new List<double>();
        foreach (var end in new[] { 18, 20, 22, 24 })
        {
            if (end < (IsSeasonal(type) ? seasonLength * 2 : 2) || end + horizon > finalTrainLength) continue;
            try { actual.AddRange(values.Skip(end).Take(horizon)); predicted.AddRange(Forecast(values.Take(end).ToArray(), type, alpha, beta, gamma, seasonLength, horizon)); }
            catch (ArgumentException) { return new ForecastResult { ModelType = type, Alpha = alpha, Beta = beta, Gamma = gamma, SeasonLength = seasonLength, ValidationMetrics = new ForecastMetrics { Smape = double.MaxValue } }; }
        }
        return new ForecastResult { ModelType = type, Alpha = alpha, Beta = beta, Gamma = gamma, SeasonLength = seasonLength, ValidationMetrics = CalculateMetrics(actual, predicted) };
    }

    private static IEnumerable<double> Parameters() { for (var i = 1; i <= 19; i++) yield return i * 0.05; }
    private static bool IsSeasonal(ForecastModelType type) => type is ForecastModelType.HoltWintersAdditive or ForecastModelType.HoltWintersMultiplicative;
    private static double[] Simple(IReadOnlyList<double> v, double a, int h) { var l = v[0]; for (var i = 1; i < v.Count; i++) l = a * v[i] + (1 - a) * l; return Enumerable.Repeat(Math.Max(0, l), h).ToArray(); }
    private static double[] Holt(IReadOnlyList<double> v, double a, double b, int h) { var l = v[0]; var t = v.Count > 1 ? v[1] - v[0] : 0; for (var i = 1; i < v.Count; i++) { var old = l; l = a * v[i] + (1 - a) * (l + t); t = b * (l - old) + (1 - b) * t; } return Enumerable.Range(1, h).Select(x => Math.Max(0, l + x * t)).ToArray(); }
    private static double[] HoltWinters(IReadOnlyList<double> v, double a, double b, double g, int s, int h, bool multiplicative)
    {
        if (v.Count < s * 2 || v.Any(x => x < 0 || double.IsNaN(x) || double.IsInfinity(x))) throw new ArgumentException("Invalid seasonal data.");
        var season = new double[s]; var first = v.Take(s).Average(); var second = v.Skip(s).Take(s).Average();
        for (var i = 0; i < s; i++) season[i] = multiplicative ? Safe(v[i] / Math.Max(first, Epsilon)) * 0.5 + Safe(v[i + s] / Math.Max(second, Epsilon)) * 0.5 : ((v[i] - first) + (v[i + s] - second)) / 2;
        var level = first; var trend = (second - first) / s;
        for (var i = s; i < v.Count; i++) { var j = i % s; var old = level; if (multiplicative) level = a * (v[i] / Math.Max(season[j], Epsilon)) + (1 - a) * (level + trend); else level = a * (v[i] - season[j]) + (1 - a) * (level + trend); trend = b * (level - old) + (1 - b) * trend; var component = multiplicative ? v[i] / Math.Max(level, Epsilon) : v[i] - level; season[j] = Math.Max(multiplicative ? Epsilon : double.MinValue, g * component + (1 - g) * season[j]); }
        return Enumerable.Range(1, h).Select(x => { var j = (v.Count + x - 1) % s; return Math.Max(0, multiplicative ? (level + x * trend) * season[j] : level + x * trend + season[j]); }).ToArray();
    }
    private static double Safe(double x) => double.IsFinite(x) ? x : 1;
}
