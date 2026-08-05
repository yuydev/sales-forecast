namespace SalesForecast;

public enum ForecastModelType
{
    Simple,
    Holt,
    HoltWintersAdditive,
    HoltWintersMultiplicative,
    LastValue,
    MovingAverage3
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
    public double Phi { get; init; }
    public int SeasonLength { get; init; }
    public ForecastMetrics ValidationMetrics { get; init; } = new();
    public double ValidationScore { get; init; }
    public double[] Forecast { get; init; } = Array.Empty<double>();
    public bool SeasonalModelAccepted { get; init; }
    public double SeasonalImprovement { get; init; }
    public List<ForecastCandidateRecord> Candidates { get; init; } = new();

    // 诊断字段
    public double StabilityPenalty { get; init; }
    public double RawForecastMax { get; init; }
    public double ClampedForecastMax { get; init; }
    public bool WasClamped { get; init; }
    public int ClampedPointCount { get; init; }
    public double ClampUpperBound { get; init; }
}

public static class HoltWintersForecaster
{
    private const double Epsilon = 1e-8;
    private const double SeasonalImprovementThreshold = 0.05;

    // ── 参数搜索范围 ──────────────────────────────────────────────
    // Alpha：0.05 ~ 0.80（步长0.05，共16个值）
    private static IEnumerable<double> AlphaParameters()
    {
        for (var i = 1; i <= 16; i++) yield return i * 0.05;
    }

    // Beta（非季节）：0.05 ~ 0.80
    private static IEnumerable<double> BetaParameters()
    {
        for (var i = 1; i <= 16; i++) yield return i * 0.05;
    }

    // Beta（季节模型）：0.05 ~ 0.50
    private static IEnumerable<double> SeasonalBetaParameters()
    {
        for (var i = 1; i <= 10; i++) yield return i * 0.05;
    }

    // Gamma（季节模型）：0.05 ~ 0.50
    private static IEnumerable<double> GammaParameters()
    {
        for (var i = 1; i <= 10; i++) yield return i * 0.05;
    }

    // Phi（阻尼趋势，Holt）：0.80, 0.90, 0.95, 0.98, 1.00（1.00 = 无阻尼）
    private static readonly double[] PhiCandidates = [0.80, 0.90, 0.95, 0.98, 1.00];

    // ── 乘法季节因子边界 ──────────────────────────────────────────
    private const double MultiplicativeSeasonMin = 0.65;
    private const double MultiplicativeSeasonMax = 1.50;

    // ── 综合评分权重 ──────────────────────────────────────────────
    private const double WeightSmape = 0.45;
    private const double WeightWape = 0.30;
    private const double WeightNormalizedMae = 0.15;
    private const double WeightStability = 0.10;

    public static ForecastResult SearchBest(
        IReadOnlyList<double> values,
        int trainLength,
        int horizon,
        int seasonLength = 12,
        bool refitOnValidation = true)
    {
        if (trainLength <= 0 || horizon <= 0 || values.Count < trainLength + horizon)
            throw new ArgumentException("训练集或验证集数据不足。", nameof(values));

        var trainSlice = values.Take(trainLength).ToArray();
        var clampBound = ComputeClampUpperBound(trainSlice);

        var candidates = new List<ForecastResult>();

        // 基准模型：LastValue、MovingAverage3
        foreach (var alpha in AlphaParameters())
            candidates.Add(Evaluate(values, ForecastModelType.Simple, alpha, 0, 0, 1.0, 0, trainLength, horizon));

        candidates.Add(Evaluate(values, ForecastModelType.LastValue, 0, 0, 0, 1.0, 0, trainLength, horizon));
        candidates.Add(Evaluate(values, ForecastModelType.MovingAverage3, 0, 0, 0, 1.0, 0, trainLength, horizon));

        // Holt（带阻尼趋势 Phi）
        foreach (var alpha in AlphaParameters())
        foreach (var beta in BetaParameters())
        foreach (var phi in PhiCandidates)
            candidates.Add(Evaluate(values, ForecastModelType.Holt, alpha, beta, 0, phi, 0, trainLength, horizon));

        // 季节模型：至少需要两个完整季节周期
        if (trainLength >= seasonLength * 2)
        {
            foreach (var alpha in AlphaParameters())
            foreach (var beta in SeasonalBetaParameters())
            foreach (var gamma in GammaParameters())
            {
                candidates.Add(Evaluate(values, ForecastModelType.HoltWintersAdditive, alpha, beta, gamma, 1.0, seasonLength, trainLength, horizon));
                candidates.Add(Evaluate(values, ForecastModelType.HoltWintersMultiplicative, alpha, beta, gamma, 1.0, seasonLength, trainLength, horizon));
            }
        }

        var valid = candidates.Where(x => double.IsFinite(x.ValidationScore)).ToList();
        if (valid.Count == 0)
            throw new InvalidOperationException("没有找到有效的预测模型。");

        // 找出最优基准（LastValue 或 MovingAverage3）
        var baseline = valid
            .Where(x => IsBaseline(x.ModelType))
            .OrderBy(x => x.ValidationScore)
            .FirstOrDefault();

        // 找出最优复杂模型（排除无效评分）
        var complex = valid
            .Where(x => !IsBaseline(x.ModelType) && x.ValidationScore < double.MaxValue)
            .OrderBy(x => x.ValidationScore)
            .FirstOrDefault();

        // 选择最终模型：复杂模型必须比最佳基准改善至少5%
        ForecastResult selected;
        if (baseline is null)
        {
            selected = complex ?? valid.OrderBy(x => x.ValidationScore).First();
        }
        else if (complex is null)
        {
            selected = baseline;
        }
        else
        {
            var improvement = baseline.ValidationScore <= Epsilon
                ? 0
                : (baseline.ValidationScore - complex.ValidationScore) / baseline.ValidationScore;
            selected = improvement >= SeasonalImprovementThreshold ? complex : baseline;
        }

        // 季节模型采用情况
        var seasonal = valid.Where(x => IsSeasonal(x.ModelType)).OrderBy(x => x.ValidationScore).FirstOrDefault();
        var nonSeasonal = valid.Where(x => !IsSeasonal(x.ModelType)).OrderBy(x => x.ValidationScore).FirstOrDefault();
        double seasonalImprovement = 0;
        if (seasonal is not null && nonSeasonal is not null && nonSeasonal.ValidationMetrics.Smape > Epsilon)
            seasonalImprovement = (nonSeasonal.ValidationMetrics.Smape - seasonal.ValidationMetrics.Smape) / nonSeasonal.ValidationMetrics.Smape;

        var ranked = valid.OrderBy(x => x.ValidationScore).ToList();
        var candidateRecords = ranked.Select((c, idx) => new ForecastCandidateRecord
        {
            Rank = idx + 1,
            ModelType = c.ModelType,
            Alpha = c.Alpha,
            Beta = c.Beta,
            Gamma = c.Gamma,
            Phi = c.Phi,
            SeasonLength = c.SeasonLength,
            ValidationSmape = c.ValidationMetrics.Smape,
            ValidationWape = c.ValidationMetrics.Wape,
            ValidationMae = c.ValidationMetrics.Mae,
            Score = c.ValidationScore,
            StabilityPenalty = c.StabilityPenalty,
            IsSelected = IsSameCandidate(c, selected)
        }).ToList();

        // 最终预测（使用选中模型的完整历史，与搜索阶段参数完全一致）
        var forecastLength = refitOnValidation ? trainLength + horizon : trainLength;
        var rawForecast = Forecast(values.Take(forecastLength).ToArray(), selected.ModelType, selected.Alpha, selected.Beta, selected.Gamma, selected.Phi, selected.SeasonLength, horizon);
        var rawMax = rawForecast.Where(x => double.IsFinite(x)).DefaultIfEmpty(0).Max();
        var clamped = ClampForecast(rawForecast, clampBound);
        var clampedMax = clamped.Where(x => double.IsFinite(x)).DefaultIfEmpty(0).Max();
        var clampedCount = rawForecast.Zip(clamped).Count(p => Math.Abs(p.First - p.Second) > Epsilon);

        return new ForecastResult
        {
            ModelType = selected.ModelType,
            Alpha = selected.Alpha,
            Beta = selected.Beta,
            Gamma = selected.Gamma,
            Phi = selected.Phi,
            SeasonLength = selected.SeasonLength,
            ValidationMetrics = selected.ValidationMetrics,
            ValidationScore = selected.ValidationScore,
            Forecast = clamped,
            SeasonalModelAccepted = seasonal is not null && IsSameCandidate(selected, seasonal),
            SeasonalImprovement = seasonalImprovement,
            Candidates = candidateRecords,
            StabilityPenalty = selected.StabilityPenalty,
            RawForecastMax = rawMax,
            ClampedForecastMax = clampedMax,
            WasClamped = clampedCount > 0,
            ClampedPointCount = clampedCount,
            ClampUpperBound = clampBound
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

    public static double[] Forecast(
        IReadOnlyList<double> values,
        ForecastModelType type,
        double alpha,
        double beta,
        double gamma,
        double phi,
        int seasonLength,
        int horizon) => type switch
    {
        ForecastModelType.Simple => Simple(values, alpha, horizon),
        ForecastModelType.Holt => HoltDamped(values, alpha, beta, phi, horizon),
        ForecastModelType.HoltWintersAdditive => HoltWinters(values, alpha, beta, gamma, seasonLength, horizon, false),
        ForecastModelType.HoltWintersMultiplicative => HoltWinters(values, alpha, beta, gamma, seasonLength, horizon, true),
        ForecastModelType.LastValue => LastValue(values, horizon),
        ForecastModelType.MovingAverage3 => MovingAverage(values, horizon, 3),
        _ => throw new NotSupportedException($"不支持的模型类型：{type}")
    };

    // ── 稳定性惩罚 ────────────────────────────────────────────────

    /// <summary>
    /// 计算预测稳定性惩罚。包含：超过历史最大值1.5倍、超过中位数4倍、
    /// 相邻预测增长过快、非有限值/负值。
    /// </summary>
    public static double CalculateStabilityPenalty(
        IReadOnlyList<double> history,
        IReadOnlyList<double> forecast)
    {
        var positive = history.Where(x => x > 0).OrderBy(x => x).ToArray();
        if (positive.Length == 0 || forecast.Count == 0)
            return 0;

        var histMax = positive[^1];
        var histMedian = Median(positive);
        var penalty = 0.0;

        var forecastMax = 0.0;
        var prevValue = double.NaN;
        foreach (var v in forecast)
        {
            // 非有限值或负值
            if (!double.IsFinite(v) || v < 0)
            {
                penalty += 2.0;
                continue;
            }

            if (v > forecastMax) forecastMax = v;

            // 相邻预测增长过快（月环比 > 3 倍）
            if (!double.IsNaN(prevValue) && prevValue > Epsilon && v / prevValue > 3.0)
                penalty += (v / prevValue - 3.0) * 0.3;

            prevValue = v;
        }

        // 超过历史最大值1.5倍
        if (forecastMax > histMax * 1.5)
            penalty += (forecastMax / histMax - 1.5) * 0.8;

        // 超过正数中位数4倍
        if (forecastMax > histMedian * 4)
            penalty += (forecastMax / histMedian - 4.0) * 0.2;

        return penalty;
    }

    // ── 预测裁剪 ─────────────────────────────────────────────────

    /// <summary>
    /// 计算预测上界：min(历史正数最大值×1.5, 历史正数中位数×4)。
    /// 若历史全为0则返回0。
    /// </summary>
    public static double ComputeClampUpperBound(IReadOnlyList<double> history)
    {
        var positive = history.Where(x => x > 0).OrderBy(x => x).ToArray();
        if (positive.Length == 0) return 0;
        var histMax = positive[^1];
        var histMedian = Median(positive);
        return Math.Min(histMax * 1.5, histMedian * 4.0);
    }

    public static double[] ClampForecast(IReadOnlyList<double> forecast, double upperBound)
    {
        return forecast.Select(x => Math.Clamp(double.IsFinite(x) ? x : 0, 0, upperBound > 0 ? upperBound : double.MaxValue)).ToArray();
    }

    // ── 内部评估 ─────────────────────────────────────────────────

    private static ForecastResult Evaluate(
        IReadOnlyList<double> values,
        ForecastModelType type,
        double alpha,
        double beta,
        double gamma,
        double phi,
        int seasonLength,
        int trainLength,
        int horizon)
    {
        var metrics = new List<ForecastMetrics>();
        var stabilityPenalties = new List<double>();
        var minimumTrain = IsSeasonal(type) ? seasonLength * 2 : (IsBaseline(type) ? 1 : 2);
        var trainValues = values.Take(trainLength).ToArray();

        foreach (var end in BuildValidationEnds(trainLength, horizon, minimumTrain))
        {
            try
            {
                var windowHistory = values.Take(end).ToArray();
                var predicted = Forecast(windowHistory, type, alpha, beta, gamma, phi, seasonLength, horizon);
                metrics.Add(CalculateMetrics(values.Skip(end).Take(horizon).ToArray(), predicted));
                stabilityPenalties.Add(CalculateStabilityPenalty(windowHistory, predicted));
            }
            catch (ArgumentException)
            {
                return InvalidResult(type, alpha, beta, gamma, phi, seasonLength);
            }
        }

        if (metrics.Count == 0)
            return InvalidResult(type, alpha, beta, gamma, phi, seasonLength);

        var avgMetrics = new ForecastMetrics
        {
            Smape = metrics.Average(x => x.Smape),
            Wape = metrics.Average(x => x.Wape),
            Mae = metrics.Average(x => x.Mae)
        };

        // 使用平均+最差窗口混合，避免单一异常窗口被平均掩盖
        var worstSmape = metrics.Max(x => x.Smape);
        var blendedSmape = 0.8 * avgMetrics.Smape + 0.2 * worstSmape;
        var blendedMetrics = new ForecastMetrics
        {
            Smape = blendedSmape,
            Wape = avgMetrics.Wape,
            Mae = avgMetrics.Mae
        };

        var avgStability = stabilityPenalties.Average();
        var score = Score(blendedMetrics, trainValues.DefaultIfEmpty(0).Average(), avgStability);

        return new ForecastResult
        {
            ModelType = type,
            Alpha = alpha,
            Beta = beta,
            Gamma = gamma,
            Phi = phi,
            SeasonLength = seasonLength,
            ValidationMetrics = avgMetrics,
            ValidationScore = score,
            StabilityPenalty = avgStability
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

    private static double Score(ForecastMetrics metrics, double actualAverage, double stabilityPenalty)
    {
        var normalizedMae = actualAverage <= Epsilon ? metrics.Mae : metrics.Mae / actualAverage;
        return WeightSmape * metrics.Smape
             + WeightWape * metrics.Wape
             + WeightNormalizedMae * normalizedMae
             + WeightStability * stabilityPenalty;
    }

    private static ForecastResult InvalidResult(ForecastModelType type, double alpha, double beta, double gamma, double phi, int seasonLength) => new()
    {
        ModelType = type,
        Alpha = alpha,
        Beta = beta,
        Gamma = gamma,
        Phi = phi,
        SeasonLength = seasonLength,
        ValidationMetrics = new ForecastMetrics { Smape = double.MaxValue, Wape = double.MaxValue, Mae = double.MaxValue },
        ValidationScore = double.MaxValue
    };

    private static bool IsSeasonal(ForecastModelType type) =>
        type is ForecastModelType.HoltWintersAdditive or ForecastModelType.HoltWintersMultiplicative;

    private static bool IsBaseline(ForecastModelType type) =>
        type is ForecastModelType.LastValue or ForecastModelType.MovingAverage3;

    private static bool IsSameCandidate(ForecastResult a, ForecastResult b) =>
        a.ModelType == b.ModelType
        && Math.Abs(a.Alpha - b.Alpha) < Epsilon
        && Math.Abs(a.Beta - b.Beta) < Epsilon
        && Math.Abs(a.Gamma - b.Gamma) < Epsilon
        && Math.Abs(a.Phi - b.Phi) < Epsilon;

    /// <summary>返回已排序数组的统计中位数（偶数长度取中间两个均值）。</summary>
    private static double Median(double[] sorted)
    {
        var n = sorted.Length;
        if (n == 0) return 0;
        return n % 2 == 1
            ? sorted[n / 2]
            : (sorted[(n - 1) / 2] + sorted[n / 2]) / 2.0;
    }

    // ── 预测算法实现 ──────────────────────────────────────────────

    private static double[] Simple(IReadOnlyList<double> values, double alpha, int horizon)
    {
        var level = values[0];
        for (var i = 1; i < values.Count; i++)
            level = alpha * values[i] + (1 - alpha) * level;
        return Enumerable.Repeat(Math.Max(0, level), horizon).ToArray();
    }

    /// <summary>
    /// Holt 带阻尼趋势。phi=1.00 等价于标准 Holt 线性趋势。
    /// phi < 1.00 时趋势逐步衰减，避免长期外推爆炸。
    /// </summary>
    private static double[] HoltDamped(IReadOnlyList<double> values, double alpha, double beta, double phi, int horizon)
    {
        var level = values[0];
        var trend = values.Count > 1 ? values[1] - values[0] : 0;
        for (var i = 1; i < values.Count; i++)
        {
            var prevLevel = level;
            level = alpha * values[i] + (1 - alpha) * (level + phi * trend);
            trend = beta * (level - prevLevel) + (1 - beta) * phi * trend;
        }

        var result = new double[horizon];
        var cumulativePhi = 0.0;
        for (var h = 1; h <= horizon; h++)
        {
            // 使用乘法递推：∑_{j=1}^{h} φ^j = φ × (1 + ∑_{j=1}^{h-1} φ^j)
            cumulativePhi = cumulativePhi * phi + phi;
            result[h - 1] = Math.Max(0, level + cumulativePhi * trend);
        }
        return result;
    }

    private static double[] LastValue(IReadOnlyList<double> values, int horizon)
    {
        var value = Math.Max(0, values[^1]);
        return Enumerable.Repeat(value, horizon).ToArray();
    }

    private static double[] MovingAverage(IReadOnlyList<double> values, int horizon, int window)
    {
        var avg = values.TakeLast(Math.Min(window, values.Count)).Average();
        return Enumerable.Repeat(Math.Max(0, avg), horizon).ToArray();
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
        var firstAvg = values.Take(seasonLength).Average();
        var secondAvg = values.Skip(seasonLength).Take(seasonLength).Average();

        for (var i = 0; i < seasonLength; i++)
        {
            if (multiplicative)
            {
                var s = Safe(values[i] / Math.Max(firstAvg, Epsilon)) * 0.5
                      + Safe(values[i + seasonLength] / Math.Max(secondAvg, Epsilon)) * 0.5;
                season[i] = Math.Clamp(s, MultiplicativeSeasonMin, MultiplicativeSeasonMax);
            }
            else
            {
                season[i] = ((values[i] - firstAvg) + (values[i + seasonLength] - secondAvg)) / 2;
            }
        }

        var level = firstAvg;
        var trend = (secondAvg - firstAvg) / seasonLength;

        for (var i = seasonLength; i < values.Count; i++)
        {
            var si = i % seasonLength;
            var prevLevel = level;

            if (multiplicative)
            {
                level = alpha * (values[i] / Math.Max(season[si], Epsilon))
                      + (1 - alpha) * (level + trend);
                trend = beta * (level - prevLevel) + (1 - beta) * trend;
                var component = values[i] / Math.Max(level, Epsilon);
                season[si] = Math.Clamp(
                    gamma * component + (1 - gamma) * season[si],
                    MultiplicativeSeasonMin,
                    MultiplicativeSeasonMax);
            }
            else
            {
                level = alpha * (values[i] - season[si]) + (1 - alpha) * (level + trend);
                trend = beta * (level - prevLevel) + (1 - beta) * trend;
                var component = values[i] - level;
                season[si] = gamma * component + (1 - gamma) * season[si];
            }
        }

        return Enumerable.Range(1, horizon).Select(h =>
        {
            var si = (values.Count + h - 1) % seasonLength;
            var baseline = level + h * trend;
            return Math.Max(0, multiplicative ? baseline * season[si] : baseline + season[si]);
        }).ToArray();
    }

    private static double Safe(double value) => double.IsFinite(value) ? value : 1;
}
