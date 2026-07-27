namespace SalesForecast;

/// <summary>指数平滑模型类型。</summary>
public enum ForecastModelType
{
    /// <summary>简单指数平滑。</summary>
    Simple,
    /// <summary>Holt双指数平滑。</summary>
    Holt,
    /// <summary>Holt-Winters加法模型。</summary>
    HoltWintersAdditive,
    /// <summary>Holt-Winters乘法模型。</summary>
    HoltWintersMultiplicative
}

/// <summary>预测误差指标。</summary>
public sealed class ForecastMetrics
{
    /// <summary>对称平均绝对百分比误差。</summary>
    public double Smape { get; init; }
    /// <summary>加权绝对百分比误差。</summary>
    public double Wape { get; init; }
    /// <summary>平均绝对误差。</summary>
    public double Mae { get; init; }
}

/// <summary>最优模型、参数和预测结果。</summary>
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

/// <summary>指数平滑模型和参数搜索实现。</summary>
public static class HoltWintersForecaster
{
    private const double Epsilon = 1e-8;
    private const double SeasonalImprovementThreshold = 0.05;

    /// <summary>
    /// 使用传入的训练集和验证集搜索最优模型。
    /// values前trainLength个月用于训练，后horizon个月用于验证。
    /// </summary>
    public static ForecastResult SearchBest(
        IReadOnlyList<double> values,
        int trainLength,
        int horizon,
        int seasonLength = 12)
    {
        if (trainLength <= 0 || horizon <= 0 || values.Count < trainLength + horizon)
            throw new ArgumentException("训练集或验证集数据不足。", nameof(values));

        var candidates = new List<ForecastResult>();

        foreach (var alpha in Parameters())
        {
            candidates.Add(Evaluate(
                values, ForecastModelType.Simple,
                alpha, 0, 0, 0, trainLength, horizon));

            foreach (var beta in Parameters())
            {
                candidates.Add(Evaluate(
                    values, ForecastModelType.Holt,
                    alpha, beta, 0, 0, trainLength, horizon));

                // 短历史序列不启用季节模型，避免用不足两个周期的数据拟合季节性。
                if (trainLength < seasonLength * 2)
                    continue;

                foreach (var gamma in Parameters())
                {
                    candidates.Add(Evaluate(
                        values, ForecastModelType.HoltWintersAdditive,
                        alpha, beta, gamma,
                        seasonLength, trainLength, horizon));
                    candidates.Add(Evaluate(
                        values, ForecastModelType.HoltWintersMultiplicative,
                        alpha, beta, gamma,
                        seasonLength, trainLength, horizon));
                }
            }
        }

        var valid = candidates
            .Where(x => double.IsFinite(x.ValidationMetrics.Smape))
            .ToList();

        if (valid.Count == 0)
            throw new InvalidOperationException("没有找到有效的预测模型。");

        var nonSeasonal = valid
            .Where(x => !IsSeasonal(x.ModelType))
            .OrderBy(x => x.ValidationMetrics.Smape)
            .First();

        var seasonal = valid
            .Where(x => IsSeasonal(x.ModelType))
            .OrderBy(x => x.ValidationMetrics.Smape)
            .FirstOrDefault();

        var improvement = seasonal is null
            || nonSeasonal.ValidationMetrics.Smape <= Epsilon
            ? 0
            : (nonSeasonal.ValidationMetrics.Smape - seasonal.ValidationMetrics.Smape)
              / nonSeasonal.ValidationMetrics.Smape;

        // 季节模型必须至少改善5%，否则继续使用最佳非季节模型。
        var selected = seasonal is not null
            && improvement >= SeasonalImprovementThreshold
            ? seasonal
            : nonSeasonal;

        var forecast = Forecast(
            values.Take(trainLength).ToArray(),
            selected.ModelType,
            selected.Alpha,
            selected.Beta,
            selected.Gamma,
            selected.SeasonLength,
            horizon);

        return new ForecastResult
        {
            ModelType = selected.ModelType,
            Alpha = selected.Alpha,
            Beta = selected.Beta,
            Gamma = selected.Gamma,
            SeasonLength = selected.SeasonLength,
            ValidationMetrics = selected.ValidationMetrics,
            Forecast = forecast,
            SeasonalModelAccepted = selected == seasonal && seasonal is not null,
            SeasonalImprovement = improvement
        };
    }

    /// <summary>计算sMAPE、WAPE和MAE。</summary>
    public static ForecastMetrics CalculateMetrics(
        IReadOnlyList<double> actual,
        IReadOnlyList<double> predicted)
    {
        var count = Math.Min(actual.Count, predicted.Count);
        if (count == 0)
            return new ForecastMetrics
            {
                Smape = double.MaxValue,
                Wape = double.MaxValue,
                Mae = double.MaxValue
            };

        double absoluteError = 0;
        double actualTotal = 0;
        double smape = 0;
        var smapeCount = 0;

        for (var i = 0; i < count; i++)
        {
            var a = Math.Max(0, actual[i]);
            var p = Math.Max(0, predicted[i]);
            var error = Math.Abs(a - p);
            var denominator = a + p;

            absoluteError += error;
            actualTotal += a;

            if (denominator > Epsilon)
            {
                smape += 2 * error / denominator;
                smapeCount++;
            }
        }

        return new ForecastMetrics
        {
            // 实际值和预测值都为0时，认为sMAPE为0而不是无效。
            Smape = smapeCount == 0
                ? (absoluteError <= Epsilon ? 0 : double.MaxValue)
                : smape / smapeCount,
            Wape = actualTotal <= Epsilon
                ? (absoluteError <= Epsilon ? 0 : double.MaxValue)
                : absoluteError / actualTotal,
            Mae = absoluteError / count
        };
    }

    /// <summary>根据模型类型生成预测值。</summary>
    public static double[] Forecast(
        IReadOnlyList<double> values,
        ForecastModelType type,
        double alpha,
        double beta,
        double gamma,
        int seasonLength,
        int horizon) => type switch
        {
            ForecastModelType.Simple => Simple(values, alpha, horizon),
            ForecastModelType.Holt => Holt(values, alpha, beta, horizon),
            ForecastModelType.HoltWintersAdditive =>
                HoltWinters(values, alpha, beta, gamma, seasonLength, horizon, false),
            ForecastModelType.HoltWintersMultiplicative =>
                HoltWinters(values, alpha, beta, gamma, seasonLength, horizon, true),
            _ => throw new NotSupportedException()
        };

    /// <summary>用当前训练集拟合模型，并在固定验证窗口上计算指标。</summary>
    private static ForecastResult Evaluate(
        IReadOnlyList<double> values,
        ForecastModelType type,
        double alpha,
        double beta,
        double gamma,
        int seasonLength,
        int trainLength,
        int horizon)
    {
        try
        {
            var predicted = Forecast(
                values.Take(trainLength).ToArray(),
                type,
                alpha,
                beta,
                gamma,
                seasonLength,
                horizon);

            var actual = values
                .Skip(trainLength)
                .Take(horizon)
                .ToArray();

            return new ForecastResult
            {
                ModelType = type,
                Alpha = alpha,
                Beta = beta,
                Gamma = gamma,
                SeasonLength = seasonLength,
                ValidationMetrics = CalculateMetrics(actual, predicted)
            };
        }
        catch (ArgumentException)
        {
            return new ForecastResult
            {
                ModelType = type,
                Alpha = alpha,
                Beta = beta,
                Gamma = gamma,
                SeasonLength = seasonLength,
                ValidationMetrics = new ForecastMetrics { Smape = double.MaxValue }
            };
        }
    }

    private static IEnumerable<double> Parameters()
    {
        for (var i = 1; i <= 19; i++)
            yield return i * 0.05;
    }

    private static bool IsSeasonal(ForecastModelType type) =>
        type is ForecastModelType.HoltWintersAdditive
            or ForecastModelType.HoltWintersMultiplicative;

    private static double[] Simple(
        IReadOnlyList<double> values,
        double alpha,
        int horizon)
    {
        var level = values[0];
        for (var i = 1; i < values.Count; i++)
            level = alpha * values[i] + (1 - alpha) * level;

        return Enumerable.Repeat(Math.Max(0, level), horizon).ToArray();
    }

    private static double[] Holt(
        IReadOnlyList<double> values,
        double alpha,
        double beta,
        int horizon)
    {
        var level = values[0];
        var trend = values.Count > 1 ? values[1] - values[0] : 0;

        for (var i = 1; i < values.Count; i++)
        {
            var previousLevel = level;
            level = alpha * values[i] + (1 - alpha) * (level + trend);
            trend = beta * (level - previousLevel) + (1 - beta) * trend;
        }

        return Enumerable.Range(1, horizon)
            .Select(x => Math.Max(0, level + x * trend))
            .ToArray();
    }

    private static double[] HoltWinters(
        IReadOnlyList<double> values,
        double alpha,
        double beta,
        double gamma,
        int seasonLength,
        int horizon,
        bool multiplicative)
    {
        if (values.Count < seasonLength * 2
            || values.Any(x => x < 0 || double.IsNaN(x) || double.IsInfinity(x)))
            throw new ArgumentException("季节模型数据无效。");

        var season = new double[seasonLength];
        var firstAverage = values.Take(seasonLength).Average();
        var secondAverage = values.Skip(seasonLength).Take(seasonLength).Average();

        for (var i = 0; i < seasonLength; i++)
        {
            season[i] = multiplicative
                ? Safe(values[i] / Math.Max(firstAverage, Epsilon)) * 0.5
                  + Safe(values[i + seasonLength]
                         / Math.Max(secondAverage, Epsilon)) * 0.5
                : ((values[i] - firstAverage)
                   + (values[i + seasonLength] - secondAverage)) / 2;
        }

        var level = firstAverage;
        var trend = (secondAverage - firstAverage) / seasonLength;

        for (var i = seasonLength; i < values.Count; i++)
        {
            var seasonIndex = i % seasonLength;
            var previousLevel = level;

            level = multiplicative
                ? alpha * (values[i] / Math.Max(season[seasonIndex], Epsilon))
                  + (1 - alpha) * (level + trend)
                : alpha * (values[i] - season[seasonIndex])
                  + (1 - alpha) * (level + trend);

            trend = beta * (level - previousLevel) + (1 - beta) * trend;

            var component = multiplicative
                ? values[i] / Math.Max(level, Epsilon)
                : values[i] - level;

            season[seasonIndex] = Math.Max(
                multiplicative ? Epsilon : double.MinValue,
                gamma * component + (1 - gamma) * season[seasonIndex]);
        }

        return Enumerable.Range(1, horizon)
            .Select(x =>
            {
                var seasonIndex = (values.Count + x - 1) % seasonLength;
                var baseline = level + x * trend;
                return Math.Max(
                    0,
                    multiplicative
                        ? baseline * season[seasonIndex]
                        : baseline + season[seasonIndex]);
            })
            .ToArray();
    }

    private static double Safe(double value) =>
        double.IsFinite(value) ? value : 1;
}
