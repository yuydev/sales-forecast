namespace SalesForecast;

/// <summary>
/// 指数平滑模型类型。
/// </summary>
public enum ForecastModelType
{
    /// <summary>简单指数平滑：适合没有明显趋势和季节性的序列。</summary>
    Simple,

    /// <summary>Holt 双指数平滑：适合存在趋势但没有明显季节性的序列。</summary>
    Holt,

    /// <summary>Holt-Winters 加法模型：季节波动幅度相对稳定。</summary>
    HoltWintersAdditive,

    /// <summary>Holt-Winters 乘法模型：季节波动随销量规模变化。</summary>
    HoltWintersMultiplicative
}

/// <summary>
/// 预测误差指标。指标值以小数表示，例如0.1代表10%。
/// </summary>
public sealed class ForecastMetrics
{
    /// <summary>对称平均绝对百分比误差。</summary>
    public double Smape { get; init; }

    /// <summary>加权绝对百分比误差：总绝对误差除以实际销量总和。</summary>
    public double Wape { get; init; }

    /// <summary>平均绝对误差，表示平均预测偏差数量。</summary>
    public double Mae { get; init; }
}

/// <summary>
/// 最优模型、参数和预测结果。
/// </summary>
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

/// <summary>
/// 指数平滑模型和参数搜索实现。
/// </summary>
public static class HoltWintersForecaster
{
    private const double Epsilon = 1e-8;

    // 季节模型至少比非季节模型改善5%时才采用，避免季节性过拟合。
    private const double SeasonalImprovementThreshold = 0.05;

    /// <summary>
    /// 搜索最优模型和Alpha、Beta、Gamma参数。
    /// </summary>
    public static ForecastResult SearchBest(
        IReadOnlyList<double> values,
        int trainLength = 30,
        int horizon = 6,
        int seasonLength = 12)
    {
        if (values.Count < trainLength + horizon)
            throw new ArgumentException("历史数据不足。", nameof(values));

        var candidates = new List<ForecastResult>();

        // 遍历Alpha，并在需要时继续遍历Beta和Gamma。
        foreach (var alpha in Parameters())
        {
            // 简单指数平滑只需要Alpha。
            candidates.Add(Evaluate(
                values, ForecastModelType.Simple,
                alpha, 0, 0, 0, horizon, trainLength));

            foreach (var beta in Parameters())
            {
                // Holt模型使用Alpha和Beta。
                candidates.Add(Evaluate(
                    values, ForecastModelType.Holt,
                    alpha, beta, 0, 0, horizon, trainLength));

                // 至少需要两个完整季节周期才能评估季节模型。
                if (trainLength < seasonLength * 2)
                    continue;

                foreach (var gamma in Parameters())
                {
                    // 同时测试加法和乘法季节模型。
                    candidates.Add(Evaluate(
                        values, ForecastModelType.HoltWintersAdditive,
                        alpha, beta, gamma,
                        seasonLength, horizon, trainLength));

                    candidates.Add(Evaluate(
                        values, ForecastModelType.HoltWintersMultiplicative,
                        alpha, beta, gamma,
                        seasonLength, horizon, trainLength));
                }
            }
        }

        // 过滤掉因数据不足或数据无效而无法计算的参数组合。
        var valid = candidates
            .Where(x => x.ValidationMetrics.Smape < double.MaxValue)
            .ToList();

        if (valid.Count == 0)
            throw new InvalidOperationException("没有找到有效的预测模型。");

        // 先分别找出最优的非季节模型和季节模型。
        var nonSeasonal = valid
            .Where(x => !IsSeasonal(x.ModelType))
            .OrderBy(x => x.ValidationMetrics.Smape)
            .First();

        var seasonal = valid
            .Where(x => IsSeasonal(x.ModelType))
            .OrderBy(x => x.ValidationMetrics.Smape)
            .FirstOrDefault();

        // 计算季节模型相对非季节模型的误差改善比例。
        var improvement = seasonal is null
            || nonSeasonal.ValidationMetrics.Smape <= Epsilon
            ? 0
            : (nonSeasonal.ValidationMetrics.Smape
                - seasonal.ValidationMetrics.Smape)
              / nonSeasonal.ValidationMetrics.Smape;

        // 只有改善达到5%才接受季节模型。
        var selected = seasonal is not null
            && improvement >= SeasonalImprovementThreshold
            ? seasonal
            : nonSeasonal;

        // 使用训练集重新拟合所选模型，并生成未来预测值。
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

    /// <summary>
    /// 计算sMAPE、WAPE和MAE。
    /// </summary>
    public static ForecastMetrics CalculateMetrics(
        IReadOnlyList<double> actual,
        IReadOnlyList<double> predicted)
    {
        var count = Math.Min(actual.Count, predicted.Count);

        if (count == 0)
        {
            return new ForecastMetrics
            {
                Smape = double.MaxValue,
                Wape = double.MaxValue,
                Mae = double.MaxValue
            };
        }

        double absoluteError = 0;
        double actualTotal = 0;
        double smape = 0;
        var smapeCount = 0;

        for (var i = 0; i < count; i++)
        {
            // 销量不能为负数，预测结果也不能为负数。
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
            Smape = smapeCount == 0
                ? double.MaxValue
                : smape / smapeCount,
            Wape = actualTotal <= Epsilon
                ? double.MaxValue
                : absoluteError / actualTotal,
            Mae = absoluteError / count
        };
    }

    /// <summary>
    /// 根据模型类型生成预测值。
    /// </summary>
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

    /// <summary>
    /// 使用多个滚动窗口评估当前参数组合。
    /// </summary>
    private static ForecastResult Evaluate(
        IReadOnlyList<double> values,
        ForecastModelType type,
        double alpha,
        double beta,
        double gamma,
        int seasonLength,
        int horizon,
        int finalTrainLength)
    {
        var actual = new List<double>();
        var predicted = new List<double>();

        // 训练窗口越靠近末尾，越能反映近期销售规律。
        foreach (var end in new[] { 18, 20, 22, 24 })
        {
            if (end < (IsSeasonal(type) ? seasonLength * 2 : 2)
                || end + horizon > finalTrainLength)
                continue;

            try
            {
                actual.AddRange(values.Skip(end).Take(horizon));
                predicted.AddRange(Forecast(
                    values.Take(end).ToArray(),
                    type, alpha, beta, gamma,
                    seasonLength, horizon));
            }
            catch (ArgumentException)
            {
                // 某一组参数无法处理当前序列时，将其标记为无效。
                return new ForecastResult
                {
                    ModelType = type,
                    Alpha = alpha,
                    Beta = beta,
                    Gamma = gamma,
                    SeasonLength = seasonLength,
                    ValidationMetrics = new ForecastMetrics
                    {
                        Smape = double.MaxValue
                    }
                };
            }
        }

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

    // 参数搜索范围为0.05到0.95，步长为0.05。
    private static IEnumerable<double> Parameters()
    {
        for (var i = 1; i <= 19; i++)
            yield return i * 0.05;
    }

    private static bool IsSeasonal(ForecastModelType type) =>
        type is ForecastModelType.HoltWintersAdditive
            or ForecastModelType.HoltWintersMultiplicative;

    /// <summary>
    /// 简单指数平滑：只更新当前水平值。
    /// </summary>
    private static double[] Simple(
        IReadOnlyList<double> values,
        double alpha,
        int horizon)
    {
        var level = values[0];

        for (var i = 1; i < values.Count; i++)
            level = alpha * values[i] + (1 - alpha) * level;

        return Enumerable
            .Repeat(Math.Max(0, level), horizon)
            .ToArray();
    }

    /// <summary>
    /// Holt双指数平滑：同时更新水平和趋势。
    /// </summary>
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

        return Enumerable
            .Range(1, horizon)
            .Select(x => Math.Max(0, level + x * trend))
            .ToArray();
    }

    /// <summary>
    /// Holt-Winters加法或乘法模型。
    /// </summary>
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

        // 使用前两个完整周期初始化季节因子。
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

            // 乘法模型先除以季节因子，加法模型则减去季节项。
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

        return Enumerable
            .Range(1, horizon)
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
