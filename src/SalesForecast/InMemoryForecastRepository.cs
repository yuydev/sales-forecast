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
            var market = MarketKeyNormalizer.NormalizeMarket(row.Market, row.BusinessUnit);
            var sku = row.Sku.Trim();
            var month = SalesValueNormalizer.NormalizeMonth(row.Month);
            var normalized = new MonthlySalesRecord
            {
                Market = market,
                BusinessUnit = row.BusinessUnit.Trim(),
                Sku = sku,
                Month = month,
                Quantity = SalesValueNormalizer.NormalizeQuantity(row.Quantity)
            };
            monthlySales[(market, sku, month)] = normalized;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MonthlySalesRecord>> GetMonthlySalesAsync(string market, string sku, CancellationToken cancellationToken = default)
    {
        var normalizedMarket = NormalizeRequiredKey(market, nameof(market));
        var normalizedSku = NormalizeRequiredKey(sku, nameof(sku));
        IReadOnlyList<MonthlySalesRecord> rows = monthlySales.Values
            .Where(x => string.Equals(x.Market, normalizedMarket, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Sku, normalizedSku, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Month)
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<ForecastParameterRecord?> GetLatestParameterAsync(string market, string sku, CancellationToken cancellationToken = default)
    {
        var key = (NormalizeRequiredKey(market, nameof(market)), NormalizeRequiredKey(sku, nameof(sku)));
        if (!parameters.TryGetValue(key, out var list) || list.Count == 0)
            return Task.FromResult<ForecastParameterRecord?>(null);
        return Task.FromResult<ForecastParameterRecord?>(list.OrderByDescending(x => x.ParameterVersion).First());
    }

    public Task<ForecastParameterRecord> SaveParameterAsync(ForecastParameterRecord parameter, IReadOnlyCollection<ForecastCandidateRecord> candidatesToSave, CancellationToken cancellationToken = default)
    {
        var market = NormalizeRequiredKey(parameter.Market, nameof(parameter.Market));
        var sku = NormalizeRequiredKey(parameter.Sku, nameof(parameter.Sku));
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
                TrainStartMonth = SalesValueNormalizer.NormalizeMonth(parameter.TrainStartMonth),
                TrainEndMonth = SalesValueNormalizer.NormalizeMonth(parameter.TrainEndMonth),
                ValidationStartMonth = SalesValueNormalizer.NormalizeMonth(parameter.ValidationStartMonth),
                ValidationEndMonth = SalesValueNormalizer.NormalizeMonth(parameter.ValidationEndMonth),
                TestStartMonth = SalesValueNormalizer.NormalizeMonth(parameter.TestStartMonth),
                TestEndMonth = SalesValueNormalizer.NormalizeMonth(parameter.TestEndMonth),
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
            var market = NormalizeRequiredKey(row.Market, nameof(row.Market));
            var sku = NormalizeRequiredKey(row.Sku, nameof(row.Sku));
            var forecastMonth = SalesValueNormalizer.NormalizeMonth(row.ForecastMonth);
            forecasts[(market, sku, forecastMonth)] = new ForecastPredictionRecord
            {
                Market = market,
                Sku = sku,
                StartForecastMonth = SalesValueNormalizer.NormalizeMonth(row.StartForecastMonth),
                ForecastMonth = forecastMonth,
                ForecastQuantity = SalesValueNormalizer.NormalizeQuantity(row.ForecastQuantity),
                ActualQuantity = row.ActualQuantity.HasValue ? SalesValueNormalizer.NormalizeQuantity(row.ActualQuantity.Value) : null,
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

    private static string NormalizeRequiredKey(string value, string paramName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("键值不能为空。", paramName);
        return normalized;
    }
}
