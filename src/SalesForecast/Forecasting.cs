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
    public double ValidationScore { get; init; }
    public double[] Forecast { get; init; } = Array.Empty<double>();
    public bool SeasonalModelAccepted { get; init; }
    public double SeasonalImprovement { get; init; }
    public List<ForecastCandidateRecord> Candidates { get; init; } = new();
}

public static class HoltWintersForecaster
{
    private const double Epsilon = 1e-8;
    private const double SeasonalImprovementThreshold = 0.05;

    public static ForecastResult SearchBest(IReadOnlyList<double> values, int trainLength, int horizon, int seasonLength = 12, bool refitOnValidation = true)
    {
        if (trainLength <= 0 || horizon <= 0 || values.Count < trainLength + horizon)
            throw new ArgumentException("训练集或验证集数据不足。", nameof(values));

        var candidates = new List<ForecastResult>();
        foreach (var alpha in Parameters())
        {
            candidates.Add(Evaluate(values, ForecastModelType.Simple, alpha, 0, 0, 0, trainLength, horizon));
            foreach (var beta in Parameters())
            {
                candidates.Add(Evaluate(values, ForecastModelType.Holt, alpha, beta, 0, 0, trainLength, horizon));
                if (trainLength < seasonLength * 2)
                    continue;

                foreach (var gamma in Parameters())
                {
                    candidates.Add(Evaluate(values, ForecastModelType.HoltWintersAdditive, alpha, beta, gamma, seasonLength, trainLength, horizon));
                    candidates.Add(Evaluate(values, ForecastModelType.HoltWintersMultiplicative, alpha, beta, gamma, seasonLength, trainLength, horizon));
                }
            }
        }

        var valid = candidates.Where(x => double.IsFinite(x.ValidationScore)).ToList();
        if (valid.Count == 0)
            throw new InvalidOperationException("没有找到有效的预测模型。");

        var nonSeasonal = valid.Where(x => !IsSeasonal(x.ModelType)).OrderBy(x => x.ValidationScore).First();
        var seasonal = valid.Where(x => IsSeasonal(x.ModelType)).OrderBy(x => x.ValidationScore).FirstOrDefault();
        var improvement = seasonal is null || nonSeasonal.ValidationMetrics.Smape <= Epsilon
            ? 0
            : (nonSeasonal.ValidationMetrics.Smape - seasonal.ValidationMetrics.Smape) / nonSeasonal.ValidationMetrics.Smape;
        var selected = seasonal is not null && improvement >= SeasonalImprovementThreshold ? seasonal : nonSeasonal;
        var ranked = valid.OrderBy(x => x.ValidationScore).ToList();

        var candidateRecords = ranked.Select((candidate, index) => new ForecastCandidateRecord
        {
            Rank = index + 1,
            ModelType = candidate.ModelType,
            Alpha = candidate.Alpha,
            Beta = candidate.Beta,
            Gamma = candidate.Gamma,
            SeasonLength = candidate.SeasonLength,
            ValidationSmape = candidate.ValidationMetrics.Smape,
            ValidationWape = candidate.ValidationMetrics.Wape,
            ValidationMae = candidate.ValidationMetrics.Mae,
            Score = candidate.ValidationScore,
            IsSelected = candidate.ModelType == selected.ModelType
                && Math.Abs(candidate.Alpha - selected.Alpha) < Epsilon
                && Math.Abs(candidate.Beta - selected.Beta) < Epsilon
                && Math.Abs(candidate.Gamma - selected.Gamma) < Epsilon
        }).ToList();

        var forecastLength = refitOnValidation ? trainLength + horizon : trainLength;
        var forecast = Forecast(values.Take(forecastLength).ToArray(), selected.ModelType, selected.Alpha, selected.Beta, selected.Gamma, selected.SeasonLength, horizon);
        return new ForecastResult
        {
            ModelType = selected.ModelType,
            Alpha = selected.Alpha,
            Beta = selected.Beta,
            Gamma = selected.Gamma,
            SeasonLength = selected.SeasonLength,
            ValidationMetrics = selected.ValidationMetrics,
            ValidationScore = selected.ValidationScore,
            Forecast = forecast,
            SeasonalModelAccepted = selected == seasonal && seasonal is not null,
            SeasonalImprovement = improvement,
            Candidates = candidateRecords
        };
    }

    public static ForecastMetrics CalculateMetrics(IReadOnlyList<double> actual, IReadOnlyList<double> predicted)
    {
        var count = Math.Min(actual.Count, predicted.Count);
        if (count == 0)
            return new ForecastMetrics { Smape = double.MaxValue, Wape = double.MaxValue, Mae = double.MaxValue };

        double absoluteError = 0, actualTotal = 0, smape = 0;
        var smapeCount = 0;
        for (var i = 0; i < count; i++)
        {
            var a = Math.Max(0, actual[i]);
            var p = Math.Max(0, predicted[i]);
            var error = Math.Abs(a - p);
            absoluteError += error;
            actualTotal += a;
            var denominator = a + p;
            if (denominator > Epsilon)
            {
                smape += 2 * error / denominator;
                smapeCount++;
            }
        }

        return new ForecastMetrics
        {
            Smape = smapeCount == 0 ? (absoluteError <= Epsilon ? 0 : double.MaxValue) : smape / smapeCount,
            Wape = actualTotal <= Epsilon ? (absoluteError <= Epsilon ? 0 : double.MaxValue) : absoluteError / actualTotal,
            Mae = absoluteError / count
        };
    }

    public static double[] Forecast(IReadOnlyList<double> values, ForecastModelType type, double alpha, double beta, double gamma, int seasonLength, int horizon) => type switch
    {
        ForecastModelType.Simple => Simple(values, alpha, horizon),
        ForecastModelType.Holt => Holt(values, alpha, beta, horizon),
        ForecastModelType.HoltWintersAdditive => HoltWinters(values, alpha, beta, gamma, seasonLength, horizon, false),
        ForecastModelType.HoltWintersMultiplicative => HoltWinters(values, alpha, beta, gamma, seasonLength, horizon, true),
        _ => throw new NotSupportedException()
    };

    private static ForecastResult Evaluate(IReadOnlyList<double> values, ForecastModelType type, double alpha, double beta, double gamma, int seasonLength, int trainLength, int horizon)
    {
        var metrics = new List<ForecastMetrics>();
        var minimumTrain = IsSeasonal(type) ? seasonLength * 2 : 2;
        foreach (var end in BuildValidationEnds(trainLength, horizon, minimumTrain))
        {
            try
            {
                var predicted = Forecast(values.Take(end).ToArray(), type, alpha, beta, gamma, seasonLength, horizon);
                metrics.Add(CalculateMetrics(values.Skip(end).Take(horizon).ToArray(), predicted));
            }
            catch (ArgumentException)
            {
                return InvalidResult(type, alpha, beta, gamma, seasonLength);
            }
        }

        if (metrics.Count == 0)
            return InvalidResult(type, alpha, beta, gamma, seasonLength);

        var average = new ForecastMetrics
        {
            Smape = metrics.Average(x => x.Smape),
            Wape = metrics.Average(x => x.Wape),
            Mae = metrics.Average(x => x.Mae)
        };

        return new ForecastResult
        {
            ModelType = type,
            Alpha = alpha,
            Beta = beta,
            Gamma = gamma,
            SeasonLength = seasonLength,
            ValidationMetrics = average,
            ValidationScore = Score(average, values.Take(trainLength).DefaultIfEmpty().Average())
        };
    }

    private static IEnumerable<int> BuildValidationEnds(int trainLength, int horizon, int minimumTrain)
    {
        var ends = new List<int>();
        for (var end = minimumTrain; end + horizon <= trainLength; end += horizon)
            ends.Add(end);

        if (ends.Count == 0 && trainLength >= minimumTrain)
            ends.Add(trainLength);
        else if (ends.Count > 0 && ends[^1] != trainLength)
            ends.Add(trainLength);

        return ends;
    }

    private static double Score(ForecastMetrics metrics, double actualAverage)
    {
        var normalizedMae = actualAverage <= Epsilon ? metrics.Mae : metrics.Mae / actualAverage;
        return 0.5 * metrics.Smape + 0.3 * metrics.Wape + 0.2 * normalizedMae;
    }

    private static ForecastResult InvalidResult(ForecastModelType type, double alpha, double beta, double gamma, int seasonLength) => new()
    {
        ModelType = type,
        Alpha = alpha,
        Beta = beta,
        Gamma = gamma,
        SeasonLength = seasonLength,
        ValidationMetrics = new ForecastMetrics { Smape = double.MaxValue, Wape = double.MaxValue, Mae = double.MaxValue },
        ValidationScore = double.MaxValue
    };

    private static IEnumerable<double> Parameters() { for (var i = 1; i <= 19; i++) yield return i * 0.05; }
    private static bool IsSeasonal(ForecastModelType type) => type is ForecastModelType.HoltWintersAdditive or ForecastModelType.HoltWintersMultiplicative;
    private static double[] Simple(IReadOnlyList<double> values, double alpha, int horizon) { var level = values[0]; for (var i = 1; i < values.Count; i++) level = alpha * values[i] + (1 - alpha) * level; return Enumerable.Repeat(Math.Max(0, level), horizon).ToArray(); }
    private static double[] Holt(IReadOnlyList<double> values, double alpha, double beta, int horizon) { var level = values[0]; var trend = values.Count > 1 ? values[1] - values[0] : 0; for (var i = 1; i < values.Count; i++) { var previousLevel = level; level = alpha * values[i] + (1 - alpha) * (level + trend); trend = beta * (level - previousLevel) + (1 - beta) * trend; } return Enumerable.Range(1, horizon).Select(x => Math.Max(0, level + x * trend)).ToArray(); }
    private static double[] HoltWinters(IReadOnlyList<double> values, double alpha, double beta, double gamma, int seasonLength, int horizon, bool multiplicative)
    {
        if (values.Count < seasonLength * 2 || values.Any(x => x < 0 || double.IsNaN(x) || double.IsInfinity(x))) throw new ArgumentException("季节模型数据无效。");
        var season = new double[seasonLength]; var firstAverage = values.Take(seasonLength).Average(); var secondAverage = values.Skip(seasonLength).Take(seasonLength).Average();
        for (var i = 0; i < seasonLength; i++) season[i] = multiplicative ? Safe(values[i] / Math.Max(firstAverage, Epsilon)) * 0.5 + Safe(values[i + seasonLength] / Math.Max(secondAverage, Epsilon)) * 0.5 : ((values[i] - firstAverage) + (values[i + seasonLength] - secondAverage)) / 2;
        var level = firstAverage; var trend = (secondAverage - firstAverage) / seasonLength;
        for (var i = seasonLength; i < values.Count; i++) { var seasonIndex = i % seasonLength; var previousLevel = level; level = multiplicative ? alpha * (values[i] / Math.Max(season[seasonIndex], Epsilon)) + (1 - alpha) * (level + trend) : alpha * (values[i] - season[seasonIndex]) + (1 - alpha) * (level + trend); trend = beta * (level - previousLevel) + (1 - beta) * trend; var component = multiplicative ? values[i] / Math.Max(level, Epsilon) : values[i] - level; season[seasonIndex] = Math.Max(multiplicative ? Epsilon : double.MinValue, gamma * component + (1 - gamma) * season[seasonIndex]); }
        return Enumerable.Range(1, horizon).Select(x => { var seasonIndex = (values.Count + x - 1) % seasonLength; var baseline = level + x * trend; return Math.Max(0, multiplicative ? baseline * season[seasonIndex] : baseline + season[seasonIndex]); }).ToArray();
    }
    private static double Safe(double value) => double.IsFinite(value) ? value : 1;
}
