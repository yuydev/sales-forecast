namespace SalesForecast;

public sealed class ForecastDatabaseService(
    IHistoricalSalesRepository historicalSalesRepository,
    IBestParameterRepository bestParameterRepository,
    IForecastResultRepository forecastResultRepository)
{
    public async Task ImportHistoryAsync(IEnumerable<MonthlySalesRecord> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var normalized = rows
            .Where(x => !string.IsNullOrWhiteSpace(x.BusinessUnit) && !string.IsNullOrWhiteSpace(x.Sku))
            .Select(x => new MonthlySalesRecord
            {
                BusinessUnit = x.BusinessUnit.Trim(),
                Market = string.IsNullOrWhiteSpace(x.Market) ? x.BusinessUnit.Trim() : x.Market.Trim(),
                Sku = x.Sku.Trim(),
                Month = NormalizeMonth(x.Month),
                Quantity = Math.Max(0, x.Quantity)
            })
            .ToList();
        await historicalSalesRepository.UpsertAsync(normalized, cancellationToken);
    }

    public async Task PersistBatchResultAsync(IEnumerable<MonthlySalesRecord> sourceRows, BatchForecastResult result, string parameterVersion = "v1", CancellationToken cancellationToken = default)
    {
        await ImportHistoryAsync(sourceRows, cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var summary in result.Summaries.Where(x => x.Status == "Success"))
        {
            var parameter = new ForecastParameterRecord
            {
                BusinessUnit = summary.BusinessUnit,
                Market = NormalizeMarket(summary.BusinessUnit, summary.Market),
                Sku = summary.Sku,
                ParameterVersion = parameterVersion,
                ModelType = summary.ModelType,
                Alpha = summary.Alpha,
                Beta = summary.Beta,
                Gamma = summary.Gamma,
                SeasonLength = summary.SeasonLength,
                TrainStartMonth = summary.TrainStartMonth,
                TrainEndMonth = summary.TrainEndMonth,
                ValidationStartMonth = summary.TestStartMonth.AddMonths(-1),
                ValidationEndMonth = summary.TestStartMonth.AddMonths(-1),
                TestStartMonth = summary.TestStartMonth,
                TestEndMonth = summary.TestEndMonth,
                ValidationSmape = summary.ValidationSmape,
                ValidationWape = summary.ValidationWape,
                ValidationMae = summary.ValidationMae,
                ValidationScore = 0.5 * summary.ValidationSmape + 0.3 * summary.ValidationWape + 0.2 * summary.ValidationMae,
                GeneratedAt = now
            };

            var parameterId = await bestParameterRepository.UpsertAsync(parameter, cancellationToken);
            var candidates = result.Candidates
                .Where(x => x.BusinessUnit == summary.BusinessUnit && NormalizeMarket(x.BusinessUnit, x.Market) == parameter.Market && x.Sku == summary.Sku)
                .OrderBy(x => x.Rank)
                .Take(100)
                .ToList();
            await bestParameterRepository.UpsertCandidatesAsync(parameterId, candidates, cancellationToken);

            var details = result.Details
                .Where(x => x.BusinessUnit == summary.BusinessUnit && NormalizeMarket(x.BusinessUnit, x.Market) == parameter.Market && x.Sku == summary.Sku && x.DataType == "Test")
                .OrderBy(x => x.Month)
                .Select(x => new PersistedForecastResultRecord
                {
                    BusinessUnit = summary.BusinessUnit,
                    Market = parameter.Market,
                    Sku = summary.Sku,
                    ForecastMonth = NormalizeMonth(x.Month),
                    ForecastQuantity = Math.Max(0, x.ForecastQuantity),
                    ActualQuantity = x.ActualQuantity,
                    AbsoluteError = x.AbsoluteError,
                    AbsolutePercentageError = x.AbsolutePercentageError,
                    Status = x.ActualQuantity > 0 ? "HasActual" : "ForecastOnly",
                    ParameterId = parameterId,
                    ParameterVersion = parameterVersion,
                    CreatedAt = now
                })
                .ToList();
            await forecastResultRepository.UpsertAsync(details, cancellationToken);
        }
    }

    public async Task<SkuForecastResponse> ForecastBySkuAsync(SkuForecastRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var market = request.Market.Trim();
        var sku = request.Sku.Trim();
        var startMonth = NormalizeMonth(request.StartMonth);

        var history = await historicalSalesRepository.GetByMarketSkuAsync(market, sku, startMonth, cancellationToken);
        if (history.Count < 3)
            throw new InvalidOperationException($"市场[{market}] SKU[{sku}] 在起始月份之前的历史数据不足3个月。");

        var parameter = await bestParameterRepository.GetByMarketSkuAsync(market, sku, request.ParameterVersion, cancellationToken);
        if (parameter is null)
        {
            if (!request.SearchParameterIfMissing)
                throw new InvalidOperationException($"未找到市场[{market}] SKU[{sku}] 参数版本[{request.ParameterVersion}]的最优参数。可开启 SearchParameterIfMissing 重新搜索。");

            parameter = await SearchAndPersistParameterAsync(market, sku, request.ParameterVersion, history, cancellationToken);
        }

        var normalized = NormalizeHistory(history);
        var forecast = HoltWintersForecaster.Forecast(
            normalized.Select(x => x.Quantity).ToArray(),
            parameter.ModelType,
            parameter.Alpha,
            parameter.Beta,
            parameter.Gamma,
            parameter.SeasonLength,
            request.Horizon);

        var endMonth = startMonth.AddMonths(request.Horizon - 1);
        var actuals = await historicalSalesRepository.GetActualQuantitiesAsync(market, sku, startMonth, endMonth, cancellationToken);
        var now = DateTime.UtcNow;
        var persisted = Enumerable.Range(0, request.Horizon).Select(index =>
        {
            var month = startMonth.AddMonths(index);
            var predicted = Math.Max(0, forecast[index]);
            var hasActual = actuals.TryGetValue(month, out var actual);
            var absoluteError = hasActual ? Math.Abs(predicted - actual) : (double?)null;
            return new PersistedForecastResultRecord
            {
                BusinessUnit = parameter.BusinessUnit,
                Market = market,
                Sku = sku,
                ForecastMonth = month,
                ForecastQuantity = predicted,
                ActualQuantity = hasActual ? actual : null,
                AbsoluteError = absoluteError,
                AbsolutePercentageError = hasActual && actual > 0 ? absoluteError / actual : null,
                Status = hasActual ? "HasActual" : "ForecastOnly",
                ParameterId = parameter.Id,
                ParameterVersion = parameter.ParameterVersion,
                CreatedAt = now
            };
        }).ToList();

        await forecastResultRepository.UpsertAsync(persisted, cancellationToken);
        return new SkuForecastResponse
        {
            Parameter = parameter,
            Results = persisted
        };
    }

    private async Task<ForecastParameterRecord> SearchAndPersistParameterAsync(
        string market,
        string sku,
        string parameterVersion,
        IReadOnlyList<MonthlySalesRecord> history,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeHistory(history);
        var split = GetSplit(normalized.Count);
        var values = normalized.Select(x => x.Quantity).ToArray();
        var model = HoltWintersForecaster.SearchBest(values, split.TrainLength, split.HorizonLength, 12, true);
        var parameter = new ForecastParameterRecord
        {
            BusinessUnit = normalized[0].BusinessUnit,
            Market = market,
            Sku = sku,
            ParameterVersion = parameterVersion,
            ModelType = model.ModelType,
            Alpha = model.Alpha,
            Beta = model.Beta,
            Gamma = model.Gamma,
            SeasonLength = model.SeasonLength,
            TrainStartMonth = normalized[0].Month,
            TrainEndMonth = normalized[split.TrainLength - 1].Month,
            ValidationStartMonth = normalized[split.TrainLength].Month,
            ValidationEndMonth = normalized[split.TrainLength + split.HorizonLength - 1].Month,
            TestStartMonth = normalized[split.TrainLength].Month,
            TestEndMonth = normalized[split.TrainLength + split.HorizonLength - 1].Month,
            ValidationSmape = model.ValidationMetrics.Smape,
            ValidationWape = model.ValidationMetrics.Wape,
            ValidationMae = model.ValidationMetrics.Mae,
            ValidationScore = model.ValidationScore,
            GeneratedAt = DateTime.UtcNow
        };
        var parameterId = await bestParameterRepository.UpsertAsync(parameter, cancellationToken);
        await bestParameterRepository.UpsertCandidatesAsync(parameterId, model.Candidates.Select(x => x with
        {
        }).Take(100).ToList(), cancellationToken);
        return parameter with { Id = parameterId };
    }

    private static (int TrainLength, int HorizonLength) GetSplit(int monthCount)
    {
        if (monthCount < 4)
            throw new InvalidOperationException("历史数据不足，无法搜索最优参数。");
        var horizon = Math.Max(2, Math.Min(6, monthCount / 4));
        var train = monthCount - horizon;
        return (train, horizon);
    }

    private static List<MonthlySalesRecord> NormalizeHistory(IEnumerable<MonthlySalesRecord> source)
    {
        var list = source.OrderBy(x => x.Month).ToList();
        var values = list
            .GroupBy(x => NormalizeMonth(x.Month))
            .ToDictionary(x => x.Key, x => x.Sum(y => Math.Max(0, y.Quantity)));
        var first = list.Min(x => NormalizeMonth(x.Month));
        var last = list.Max(x => NormalizeMonth(x.Month));
        var businessUnit = list[0].BusinessUnit;
        var market = list[0].Market;
        var sku = list[0].Sku;
        var normalized = new List<MonthlySalesRecord>();
        for (var month = first; month <= last; month = month.AddMonths(1))
        {
            normalized.Add(new MonthlySalesRecord
            {
                BusinessUnit = businessUnit,
                Market = market,
                Sku = sku,
                Month = month,
                Quantity = values.GetValueOrDefault(month)
            });
        }

        return normalized;
    }

    private static void ValidateRequest(SkuForecastRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.Market))
            throw new ArgumentException("市场不能为空。", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Sku))
            throw new ArgumentException("SKU不能为空。", nameof(request));
        if (request.Horizon <= 0 || request.Horizon > 60)
            throw new ArgumentOutOfRangeException(nameof(request), "预测horizon必须在1到60之间。");
        if (string.IsNullOrWhiteSpace(request.ParameterVersion))
            throw new ArgumentException("参数版本不能为空。", nameof(request));
    }

    private static DateTime NormalizeMonth(DateTime month) => new(month.Year, month.Month, 1);
    private static string NormalizeMarket(string businessUnit, string market) => string.IsNullOrWhiteSpace(market) ? businessUnit : market;
}
