namespace SalesForecast;

public sealed class SkuForecastService(IForecastRepository repository)
{
    private readonly IForecastRepository repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<IReadOnlyList<ForecastPredictionRecord>> ForecastAsync(SkuForecastRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var market = request.Market.Trim();
        var sku = request.Sku.Trim();
        if (string.IsNullOrWhiteSpace(market))
            throw new ArgumentException("市场不能为空。", nameof(request));
        if (string.IsNullOrWhiteSpace(sku))
            throw new ArgumentException("SKU不能为空。", nameof(request));
        if (request.Horizon <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "预测月数必须大于0。");
        if (request.SearchTrainLength <= 0 || request.SearchHorizon <= 0 || request.SeasonLength <= 1)
            throw new ArgumentOutOfRangeException(nameof(request), "参数搜索配置无效。");

        var startMonth = NormalizeMonth(request.StartForecastMonth);
        var history = await repository.GetMonthlySalesAsync(market, sku, cancellationToken);
        if (history.Count == 0)
            throw new InvalidOperationException($"未找到市场[{market}] SKU[{sku}]的历史销售数据。");

        var normalized = NormalizeHistory(history.Where(x => x.Month < startMonth));
        if (normalized.Count < 2)
            throw new InvalidOperationException($"市场[{market}] SKU[{sku}]在预测起始月份[{startMonth:yyyy-MM}]之前的历史数据不足。");

        ForecastParameterRecord? parameter;
        try
        {
            parameter = await repository.GetLatestParameterAsync(market, sku, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"读取参数失败：{ex.Message}", ex);
        }

        if (parameter is null)
        {
            if (!request.SearchParameterIfMissing)
                throw new InvalidOperationException($"市场[{market}] SKU[{sku}]缺少已保存参数。请先保存参数，或将 SearchParameterIfMissing 设为 true。");
            parameter = await SearchAndSaveParameterAsync(market, sku, normalized, request, cancellationToken);
        }

        var valueMap = history
            .GroupBy(x => NormalizeMonth(x.Month))
            .ToDictionary(x => x.Key, x => x.Sum(y => Math.Max(0, y.Quantity)));
        var values = normalized.Select(x => x.Quantity).ToArray();
        var forecast = HoltWintersForecaster.Forecast(
            values,
            parameter.ModelType,
            parameter.Alpha,
            parameter.Beta,
            parameter.Gamma,
            parameter.SeasonLength,
            request.Horizon);

        var createdAt = DateTime.UtcNow;
        var rows = new List<ForecastPredictionRecord>();
        for (var i = 0; i < request.Horizon; i++)
        {
            var month = startMonth.AddMonths(i);
            var predicted = Math.Max(0, forecast[i]);
            var hasActual = valueMap.TryGetValue(month, out var actual);
            rows.Add(new ForecastPredictionRecord
            {
                Market = market,
                Sku = sku,
                StartForecastMonth = startMonth,
                ForecastMonth = month,
                ForecastQuantity = predicted,
                ActualQuantity = hasActual ? actual : null,
                Error = hasActual ? predicted - actual : null,
                Status = hasActual ? "Scored" : "ForecastOnly",
                ParameterRecordId = parameter.Id,
                ParameterVersion = parameter.ParameterVersion,
                CreatedAt = createdAt
            });
        }

        try
        {
            await repository.UpsertForecastResultsAsync(rows, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"保存预测结果失败：{ex.Message}", ex);
        }

        return rows;
    }

    private async Task<ForecastParameterRecord> SearchAndSaveParameterAsync(
        string market,
        string sku,
        IReadOnlyList<MonthlySalesRecord> history,
        SkuForecastRequest request,
        CancellationToken cancellationToken)
    {
        if (history.Count < 3)
            throw new InvalidOperationException($"市场[{market}] SKU[{sku}]历史月份只有{history.Count}个月，无法搜索参数。");

        var selectedLength = Math.Min(history.Count, request.SearchTrainLength + request.SearchHorizon);
        var selected = history.OrderByDescending(x => x.Month).Take(selectedLength).OrderBy(x => x.Month).ToList();
        var split = ForecastSplitPolicy.GetSplit(selected.Count, request.SearchTrainLength, request.SearchHorizon);
        var validationLength = split.ValidationLength > 0 ? split.ValidationLength : split.TestLength;

        var model = HoltWintersForecaster.SearchBest(
            selected.Select(x => x.Quantity).ToArray(),
            split.TrainLength,
            validationLength,
            request.SeasonLength,
            split.TestLength > 0);

        var validationMonths = selected.Skip(split.TrainLength).Take(validationLength).ToList();
        var testMonths = split.TestLength > 0
            ? selected.Skip(split.TrainLength + split.ValidationLength).Take(split.TestLength).ToList()
            : validationMonths;
        var score = model.Candidates.FirstOrDefault(x => x.IsSelected)?.Score ?? model.ValidationScore;

        var parameter = new ForecastParameterRecord
        {
            Market = market,
            Sku = sku,
            ModelType = model.ModelType,
            Alpha = model.Alpha,
            Beta = model.Beta,
            Gamma = model.Gamma,
            SeasonLength = model.SeasonLength,
            TrainStartMonth = selected.First().Month,
            TrainEndMonth = selected[split.TrainLength - 1].Month,
            ValidationStartMonth = validationMonths.First().Month,
            ValidationEndMonth = validationMonths.Last().Month,
            TestStartMonth = testMonths.First().Month,
            TestEndMonth = testMonths.Last().Month,
            ValidationSmape = model.ValidationMetrics.Smape,
            ValidationWape = model.ValidationMetrics.Wape,
            ValidationMae = model.ValidationMetrics.Mae,
            CompositeScore = score,
            GeneratedAt = DateTime.UtcNow
        };
        var candidates = model.Candidates.Select(x => new ForecastCandidateRecord
        {
            Market = market,
            BusinessUnit = market,
            Sku = sku,
            Rank = x.Rank,
            ModelType = x.ModelType,
            Alpha = x.Alpha,
            Beta = x.Beta,
            Gamma = x.Gamma,
            SeasonLength = x.SeasonLength,
            ValidationSmape = x.ValidationSmape,
            ValidationWape = x.ValidationWape,
            ValidationMae = x.ValidationMae,
            Score = x.Score,
            IsSelected = x.IsSelected
        }).ToList();

        try
        {
            return await repository.SaveParameterAsync(parameter, candidates, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"保存参数失败：{ex.Message}", ex);
        }
    }

    private static List<MonthlySalesRecord> NormalizeHistory(IEnumerable<MonthlySalesRecord> history)
    {
        var values = history
            .GroupBy(x => NormalizeMonth(x.Month))
            .ToDictionary(x => x.Key, x => x.Sum(y => Math.Max(0, y.Quantity)));
        if (values.Count == 0)
            return [];

        var result = new List<MonthlySalesRecord>();
        var start = values.Keys.Min();
        var end = values.Keys.Max();
        for (var month = start; month <= end; month = month.AddMonths(1))
        {
            result.Add(new MonthlySalesRecord
            {
                Month = month,
                Quantity = values.GetValueOrDefault(month)
            });
        }
        return result;
    }

    private static DateTime NormalizeMonth(DateTime month) => new(month.Year, month.Month, 1);
}
