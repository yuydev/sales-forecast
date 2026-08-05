using SalesForecast;
using Xunit;

namespace SalesForecast.Tests;

/// <summary>
/// 验证新算法约束：Gamma上界、LastValue/MovingAverage3行为、乘法季节因子边界、
/// 异常尖峰惩罚、阻尼趋势、预测裁剪、唯一IsSelected。
/// </summary>
public sealed class ForecastingTests
{
    // 36个月递增数据（稳定趋势，无季节性）
    private static double[] StableTrend(int months = 36) =>
        Enumerable.Range(1, months).Select(i => (double)(100 + i * 2)).ToArray();

    // 36个月数据，末尾一个月有极大尖峰
    private static double[] SpikeData()
    {
        var data = StableTrend(36);
        data[^1] = 100_000; // 极大尖峰
        return data;
    }

    // 构造有明显12个月季节性的数据（至少24个月）
    private static double[] SeasonalData(int months = 36)
    {
        var seasonal = new double[] { 100, 90, 95, 105, 120, 130, 140, 125, 110, 100, 90, 85 };
        return Enumerable.Range(0, months).Select(i => seasonal[i % 12] + i * 0.5).ToArray();
    }

    [Fact]
    public void Gamma_095_never_appears_in_candidates()
    {
        var values = SeasonalData(36);
        var result = HoltWintersForecaster.SearchBest(values, 30, 6, 12);
        foreach (var c in result.Candidates)
        {
            if (c.ModelType is ForecastModelType.HoltWintersAdditive
                or ForecastModelType.HoltWintersMultiplicative)
            {
                Assert.True(c.Gamma <= 0.50 + 1e-9,
                    $"Gamma={c.Gamma} 超出上界 0.50，出现在候选 Rank={c.Rank}");
            }
        }
    }

    [Fact]
    public void Alpha_never_exceeds_080_in_all_model_types()
    {
        var values = StableTrend(36);
        var result = HoltWintersForecaster.SearchBest(values, 30, 6, 12);
        foreach (var c in result.Candidates)
            Assert.True(c.Alpha <= 0.80 + 1e-9,
                $"Alpha={c.Alpha} 超出上界 0.80，出现在候选 Rank={c.Rank}");
    }

    [Fact]
    public void LastValue_forecast_equals_last_history_value()
    {
        var values = new double[] { 10, 20, 30, 40, 50 };
        var forecast = HoltWintersForecaster.Forecast(values, ForecastModelType.LastValue, 0, 0, 0, 1.0, 0, 3);
        Assert.All(forecast, v => Assert.Equal(50.0, v));
    }

    [Fact]
    public void MovingAverage3_forecast_equals_last_three_average()
    {
        var values = new double[] { 10, 20, 30, 40, 50 };
        var expected = (30 + 40 + 50) / 3.0;
        var forecast = HoltWintersForecaster.Forecast(values, ForecastModelType.MovingAverage3, 0, 0, 0, 1.0, 0, 3);
        Assert.All(forecast, v => Assert.Equal(expected, v, 6));
    }

    [Fact]
    public void Multiplicative_seasonal_factors_are_clamped()
    {
        // 构造极端数据：前12月全是1，后12月全是1000（使初始季节因子极大）
        var values = Enumerable.Repeat(1.0, 12)
            .Concat(Enumerable.Repeat(1000.0, 12))
            .ToArray();
        // 直接调用 HoltWinters 预测，验证不会产生 NaN/Infinity 以及极大值
        var forecast = HoltWintersForecaster.Forecast(values, ForecastModelType.HoltWintersMultiplicative, 0.3, 0.1, 0.3, 1.0, 12, 6);
        Assert.All(forecast, v =>
        {
            Assert.True(double.IsFinite(v), $"预测值不是有限数：{v}");
            Assert.True(v >= 0, $"预测值为负：{v}");
        });
    }

    [Fact]
    public void Spike_data_causes_higher_stability_penalty()
    {
        var history = StableTrend(30);
        var forecastNormal = HoltWintersForecaster.Forecast(history, ForecastModelType.Simple, 0.3, 0, 0, 1.0, 0, 6);
        var penaltyNormal = HoltWintersForecaster.CalculateStabilityPenalty(history, forecastNormal);

        // 将预测最大值强制设为历史最大值的3倍，期望产生较大惩罚
        var bigForecast = Enumerable.Repeat(history.Max() * 3.0, 6).ToArray();
        var penaltyBig = HoltWintersForecaster.CalculateStabilityPenalty(history, bigForecast);

        Assert.True(penaltyBig > penaltyNormal,
            $"大预测值应有更高稳定性惩罚，但 penaltyBig={penaltyBig}, penaltyNormal={penaltyNormal}");
    }

    [Fact]
    public void Damped_trend_phi_less_than_one_reduces_long_horizon_forecast()
    {
        // 强上升趋势数据
        var values = Enumerable.Range(1, 24).Select(i => (double)(i * 10)).ToArray();
        // phi=0.80 应该比 phi=1.00 的长期预测更保守
        var dampedForecast = HoltWintersForecaster.Forecast(values, ForecastModelType.Holt, 0.3, 0.3, 0, 0.80, 0, 12);
        var fullForecast = HoltWintersForecaster.Forecast(values, ForecastModelType.Holt, 0.3, 0.3, 0, 1.00, 0, 12);

        // 长期（最后几个月）的预测值，阻尼版本应更小
        Assert.True(dampedForecast[^1] < fullForecast[^1],
            $"阻尼趋势应比全趋势预测更保守，但 dampedForecast={dampedForecast[^1]}, fullForecast={fullForecast[^1]}");
    }

    [Fact]
    public void Forecast_is_clamped_to_upper_bound()
    {
        // 历史最大值100，中位数~50；上界 = min(100*1.5, 50*4) = min(150, 200) = 150
        var history = Enumerable.Range(1, 30).Select(i => (double)i * 3 + 10).ToArray();
        var upperBound = HoltWintersForecaster.ComputeClampUpperBound(history);
        Assert.True(upperBound > 0);

        var hugeForecast = Enumerable.Repeat(upperBound * 10, 6).ToArray();
        var clamped = HoltWintersForecaster.ClampForecast(hugeForecast, upperBound);
        Assert.All(clamped, v => Assert.True(v <= upperBound + 1e-9, $"裁剪后预测值 {v} 超过上界 {upperBound}"));
    }

    [Fact]
    public void Only_one_IsSelected_candidate_per_search()
    {
        var values = StableTrend(36);
        var result = HoltWintersForecaster.SearchBest(values, 30, 6, 12);
        var selectedCount = result.Candidates.Count(c => c.IsSelected);
        Assert.Equal(1, selectedCount);
    }

    [Fact]
    public void Seasonal_model_requires_two_full_seasons()
    {
        // 训练长度 < seasonLength * 2，季节模型不应出现在候选中
        var values = StableTrend(30);
        var result = HoltWintersForecaster.SearchBest(values, 20, 4, 12);
        var seasonalCandidates = result.Candidates
            .Where(c => c.ModelType is ForecastModelType.HoltWintersAdditive or ForecastModelType.HoltWintersMultiplicative)
            .ToList();
        Assert.Empty(seasonalCandidates);
    }

    [Fact]
    public void SearchBest_result_clamp_info_is_populated()
    {
        var values = StableTrend(36);
        var result = HoltWintersForecaster.SearchBest(values, 30, 6, 12);
        Assert.True(result.ClampUpperBound > 0);
        Assert.True(result.RawForecastMax >= 0);
        Assert.True(result.ClampedForecastMax >= 0);
        Assert.True(result.ClampedForecastMax <= result.ClampUpperBound + 1e-9);
    }

    [Fact]
    public void Spike_at_end_does_not_select_explosive_model()
    {
        // 前35月稳定，最后一月极大尖峰；期望不选出预测极大值的模型
        var values = SpikeData();
        var result = HoltWintersForecaster.SearchBest(values, 30, 6, 12);
        // 裁剪后预测最大值不超过正常历史（前35个月）的合理倍数
        var normalHistory = values.Take(35).Where(x => x > 0).ToArray();
        var normalMax = normalHistory.Max();
        Assert.True(result.ClampedForecastMax <= normalMax * 3.0 + 1e-3,
            $"异常尖峰导致预测值过大：ClampedForecastMax={result.ClampedForecastMax}，normalMax={normalMax}");
    }
}
