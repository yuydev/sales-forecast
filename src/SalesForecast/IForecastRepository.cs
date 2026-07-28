namespace SalesForecast;

public interface IForecastRepository
{
    Task UpsertMonthlySalesAsync(IEnumerable<MonthlySalesRecord> rows, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MonthlySalesRecord>> GetMonthlySalesAsync(string market, string sku, CancellationToken cancellationToken = default);
    Task<ForecastParameterRecord?> GetLatestParameterAsync(string market, string sku, CancellationToken cancellationToken = default);
    Task<ForecastParameterRecord> SaveParameterAsync(ForecastParameterRecord parameter, IReadOnlyCollection<ForecastCandidateRecord> candidates, CancellationToken cancellationToken = default);
    Task UpsertForecastResultsAsync(IEnumerable<ForecastPredictionRecord> rows, CancellationToken cancellationToken = default);
}
