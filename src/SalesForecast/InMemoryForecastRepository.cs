using System.Collections.Concurrent;

namespace SalesForecast;

public sealed class InMemoryForecastRepository : IForecastRepository
{
    private readonly ConcurrentDictionary<(string Market, string Sku, DateTime Month), MonthlySalesRecord> monthlySales = new();
    private readonly ConcurrentDictionary<(string Market, string Sku), List<ForecastParameterRecord>> parameters = new();
    private readonly ConcurrentDictionary<(long ParameterId, int Rank), ForecastCandidateRecord> candidates = new();
    private readonly ConcurrentDictionary<(string Market, string Sku, DateTime ForecastMonth), ForecastPredictionRecord> forecasts = new();
    private long parameterId;

    public Task UpsertMonthlySalesAsync(IEnumerable<MonthlySalesRecord> rows, CancellationToken cancellationToken = default)
    {
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var market = ParallelBatchForecastService.NormalizeMarket(row.Market, row.BusinessUnit);
            var sku = row.Sku.Trim();
            var month = new DateTime(row.Month.Year, row.Month.Month, 1);
            var normalized = new MonthlySalesRecord
            {
                Market = market,
                BusinessUnit = row.BusinessUnit.Trim(),
                Sku = sku,
                Month = month,
                Quantity = Math.Max(0, row.Quantity)
            };
            monthlySales[(market, sku, month)] = normalized;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MonthlySalesRecord>> GetMonthlySalesAsync(string market, string sku, CancellationToken cancellationToken = default)
    {
        var normalizedMarket = ParallelBatchForecastService.NormalizeMarket(market, market);
        var normalizedSku = sku.Trim();
        IReadOnlyList<MonthlySalesRecord> rows = monthlySales.Values
            .Where(x => string.Equals(x.Market, normalizedMarket, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Sku, normalizedSku, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Month)
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<ForecastParameterRecord?> GetLatestParameterAsync(string market, string sku, CancellationToken cancellationToken = default)
    {
        var key = (ParallelBatchForecastService.NormalizeMarket(market, market), sku.Trim());
        if (!parameters.TryGetValue(key, out var list) || list.Count == 0)
            return Task.FromResult<ForecastParameterRecord?>(null);
        return Task.FromResult<ForecastParameterRecord?>(list.OrderByDescending(x => x.ParameterVersion).First());
    }

    public Task<ForecastParameterRecord> SaveParameterAsync(ForecastParameterRecord parameter, IReadOnlyCollection<ForecastCandidateRecord> candidatesToSave, CancellationToken cancellationToken = default)
    {
        var market = ParallelBatchForecastService.NormalizeMarket(parameter.Market, parameter.Market);
        var sku = parameter.Sku.Trim();
        var key = (market, sku);
        var list = parameters.GetOrAdd(key, _ => []);
        lock (list)
        {
            var nextVersion = list.Count == 0 ? 1 : list.Max(x => x.ParameterVersion) + 1;
            var saved = new ForecastParameterRecord
            {
                Id = Interlocked.Increment(ref parameterId),
                Market = market,
                Sku = sku,
                ParameterVersion = nextVersion,
                ModelType = parameter.ModelType,
                Alpha = parameter.Alpha,
                Beta = parameter.Beta,
                Gamma = parameter.Gamma,
                SeasonLength = parameter.SeasonLength,
                TrainStartMonth = NormalizeMonth(parameter.TrainStartMonth),
                TrainEndMonth = NormalizeMonth(parameter.TrainEndMonth),
                ValidationStartMonth = NormalizeMonth(parameter.ValidationStartMonth),
                ValidationEndMonth = NormalizeMonth(parameter.ValidationEndMonth),
                TestStartMonth = NormalizeMonth(parameter.TestStartMonth),
                TestEndMonth = NormalizeMonth(parameter.TestEndMonth),
                ValidationSmape = parameter.ValidationSmape,
                ValidationWape = parameter.ValidationWape,
                ValidationMae = parameter.ValidationMae,
                CompositeScore = parameter.CompositeScore,
                GeneratedAt = parameter.GeneratedAt
            };
            list.Add(saved);

            foreach (var item in candidatesToSave
                .OrderBy(x => x.Rank)
                .Take(100))
            {
                this.candidates[(saved.Id, item.Rank)] = new ForecastCandidateRecord
                {
                    Market = market,
                    BusinessUnit = item.BusinessUnit,
                    Sku = sku,
                    Rank = item.Rank,
                    ModelType = item.ModelType,
                    Alpha = item.Alpha,
                    Beta = item.Beta,
                    Gamma = item.Gamma,
                    SeasonLength = item.SeasonLength,
                    ValidationSmape = item.ValidationSmape,
                    ValidationWape = item.ValidationWape,
                    ValidationMae = item.ValidationMae,
                    Score = item.Score,
                    IsSelected = item.IsSelected
                };
            }

            return Task.FromResult(saved);
        }
    }

    public Task UpsertForecastResultsAsync(IEnumerable<ForecastPredictionRecord> rows, CancellationToken cancellationToken = default)
    {
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var market = ParallelBatchForecastService.NormalizeMarket(row.Market, row.Market);
            var sku = row.Sku.Trim();
            var forecastMonth = NormalizeMonth(row.ForecastMonth);
            forecasts[(market, sku, forecastMonth)] = new ForecastPredictionRecord
            {
                Market = market,
                Sku = sku,
                StartForecastMonth = NormalizeMonth(row.StartForecastMonth),
                ForecastMonth = forecastMonth,
                ForecastQuantity = Math.Max(0, row.ForecastQuantity),
                ActualQuantity = row.ActualQuantity.HasValue ? Math.Max(0, row.ActualQuantity.Value) : null,
                Error = row.Error,
                Status = row.Status,
                ParameterRecordId = row.ParameterRecordId,
                ParameterVersion = row.ParameterVersion,
                CreatedAt = row.CreatedAt
            };
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<ForecastCandidateRecord> GetSavedCandidates(long parameterRecordId) => candidates
        .Where(x => x.Key.ParameterId == parameterRecordId)
        .Select(x => x.Value)
        .OrderBy(x => x.Rank)
        .ToList();

    public IReadOnlyList<ForecastPredictionRecord> GetSavedForecasts(string market, string sku) => forecasts.Values
        .Where(x => string.Equals(x.Market, market, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Sku, sku, StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.ForecastMonth)
        .ToList();

    private static DateTime NormalizeMonth(DateTime month) => new(month.Year, month.Month, 1);
}
