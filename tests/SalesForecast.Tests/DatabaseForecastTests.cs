using SalesForecast;
using Xunit;

namespace SalesForecast.Tests;

public sealed class DatabaseForecastTests
{
    [Fact]
    public async Task History_upsert_is_idempotent()
    {
        var repository = new InMemoryForecastRepository();
        await repository.UpsertMonthlySalesAsync(
        [
            new MonthlySalesRecord { Market = "CN", BusinessUnit = "BU1", Sku = "SKU1", Month = new DateTime(2024, 1, 15), Quantity = 10 },
            new MonthlySalesRecord { Market = "CN", BusinessUnit = "BU1", Sku = "SKU1", Month = new DateTime(2024, 1, 20), Quantity = -5 },
            new MonthlySalesRecord { Market = "CN", BusinessUnit = "BU1", Sku = "SKU1", Month = new DateTime(2024, 1, 1), Quantity = 12 }
        ]);

        var rows = await repository.GetMonthlySalesAsync("CN", "SKU1");
        var only = Assert.Single(rows);
        Assert.Equal(new DateTime(2024, 1, 1), only.Month);
        Assert.Equal(12, only.Quantity);
    }

    [Fact]
    public async Task Save_parameter_records_top_100_candidates()
    {
        var repository = new InMemoryForecastRepository();
        var parameter = new ForecastParameterRecord
        {
            Market = "CN",
            Sku = "SKU1",
            ModelType = ForecastModelType.Holt,
            Alpha = 0.2,
            Beta = 0.3,
            Gamma = 0,
            SeasonLength = 12,
            TrainStartMonth = new DateTime(2022, 1, 1),
            TrainEndMonth = new DateTime(2023, 6, 1),
            ValidationStartMonth = new DateTime(2023, 7, 1),
            ValidationEndMonth = new DateTime(2023, 12, 1),
            TestStartMonth = new DateTime(2023, 7, 1),
            TestEndMonth = new DateTime(2023, 12, 1),
            ValidationSmape = 0.1,
            ValidationWape = 0.2,
            ValidationMae = 1.5,
            CompositeScore = 0.3
        };

        var candidates = Enumerable.Range(1, 120)
            .Select(i => new ForecastCandidateRecord
            {
                Market = "CN",
                BusinessUnit = "BU1",
                Sku = "SKU1",
                Rank = i,
                ModelType = ForecastModelType.Holt,
                Alpha = 0.1,
                Beta = 0.2,
                Gamma = 0,
                SeasonLength = 12,
                ValidationSmape = 0.2,
                ValidationWape = 0.3,
                ValidationMae = 1.2,
                Score = i,
                IsSelected = i == 1
            })
            .ToList();

        var saved = await repository.SaveParameterAsync(parameter, candidates);
        var savedCandidates = repository.GetSavedCandidates(saved.Id);
        Assert.Equal(100, savedCandidates.Count);
        Assert.Equal(1, saved.ParameterVersion);
    }

    [Fact]
    public async Task Forecast_uses_market_sku_and_start_month()
    {
        var repository = new InMemoryForecastRepository();
        await repository.UpsertMonthlySalesAsync(
            Enumerable.Range(0, 12).Select(i => new MonthlySalesRecord
            {
                Market = "CN",
                BusinessUnit = "BU1",
                Sku = "SKU1",
                Month = new DateTime(2024, 1, 1).AddMonths(i),
                Quantity = 100 + i
            }));
        await repository.UpsertMonthlySalesAsync(
            Enumerable.Range(0, 12).Select(i => new MonthlySalesRecord
            {
                Market = "US",
                BusinessUnit = "BU2",
                Sku = "SKU1",
                Month = new DateTime(2024, 1, 1).AddMonths(i),
                Quantity = 500 + i
            }));

        var parameter = await repository.SaveParameterAsync(
            new ForecastParameterRecord
            {
                Market = "CN",
                Sku = "SKU1",
                ModelType = ForecastModelType.Simple,
                Alpha = 0.5,
                Beta = 0,
                Gamma = 0,
                SeasonLength = 12,
                TrainStartMonth = new DateTime(2024, 1, 1),
                TrainEndMonth = new DateTime(2024, 10, 1),
                ValidationStartMonth = new DateTime(2024, 11, 1),
                ValidationEndMonth = new DateTime(2024, 12, 1),
                TestStartMonth = new DateTime(2024, 11, 1),
                TestEndMonth = new DateTime(2024, 12, 1),
                ValidationSmape = 0.1,
                ValidationWape = 0.2,
                ValidationMae = 1.2,
                CompositeScore = 0.3
            },
            [new ForecastCandidateRecord { Market = "CN", BusinessUnit = "BU1", Sku = "SKU1", Rank = 1, ModelType = ForecastModelType.Simple, Alpha = 0.5, SeasonLength = 12, IsSelected = true }]);

        var service = new SkuForecastService(repository);
        var rows = await service.ForecastAsync(new SkuForecastRequest
        {
            Market = "CN",
            Sku = "SKU1",
            StartForecastMonth = new DateTime(2025, 1, 15),
            Horizon = 3,
            SearchParameterIfMissing = false
        });

        Assert.Equal(3, rows.Count);
        Assert.All(rows, x =>
        {
            Assert.Equal("CN", x.Market);
            Assert.Equal("SKU1", x.Sku);
            Assert.Equal(parameter.ParameterVersion, x.ParameterVersion);
        });
        Assert.Equal(new DateTime(2025, 1, 1), rows[0].ForecastMonth);
        var savedForecasts = repository.GetSavedForecasts("CN", "SKU1");
        Assert.Equal(3, savedForecasts.Count);
    }

    [Fact]
    public async Task Missing_parameter_without_search_throws_clear_error()
    {
        var repository = new InMemoryForecastRepository();
        await repository.UpsertMonthlySalesAsync(
            Enumerable.Range(0, 8).Select(i => new MonthlySalesRecord
            {
                Market = "CN",
                BusinessUnit = "BU1",
                Sku = "SKU1",
                Month = new DateTime(2024, 1, 1).AddMonths(i),
                Quantity = 20 + i
            }));

        var service = new SkuForecastService(repository);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ForecastAsync(new SkuForecastRequest
        {
            Market = "CN",
            Sku = "SKU1",
            StartForecastMonth = new DateTime(2024, 9, 1),
            Horizon = 2,
            SearchParameterIfMissing = false
        }));

        Assert.Contains("SearchParameterIfMissing", ex.Message);
    }

    [Fact]
    public async Task Database_unavailable_error_is_clear()
    {
        var service = new SkuForecastService(new ThrowingRepository());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ForecastAsync(new SkuForecastRequest
        {
            Market = "CN",
            Sku = "SKU1",
            StartForecastMonth = new DateTime(2024, 9, 1),
            Horizon = 2,
            SearchParameterIfMissing = false
        }));

        Assert.NotNull(ex.InnerException);
        Assert.Contains("database down", ex.InnerException!.Message);
    }

    private sealed class ThrowingRepository : IForecastRepository
    {
        public Task<IReadOnlyList<MonthlySalesRecord>> GetMonthlySalesAsync(string market, string sku, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MonthlySalesRecord>>(
            [
                new MonthlySalesRecord { Market = market, BusinessUnit = "BU-TEST", Sku = sku, Month = new DateTime(2024, 1, 1), Quantity = 10 },
                new MonthlySalesRecord { Market = market, BusinessUnit = "BU-TEST", Sku = sku, Month = new DateTime(2024, 2, 1), Quantity = 11 },
                new MonthlySalesRecord { Market = market, BusinessUnit = "BU-TEST", Sku = sku, Month = new DateTime(2024, 3, 1), Quantity = 12 }
            ]);

        public Task<ForecastParameterRecord?> GetLatestParameterAsync(string market, string sku, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("database down");
        public Task<ForecastParameterRecord> SaveParameterAsync(ForecastParameterRecord parameter, IReadOnlyCollection<ForecastCandidateRecord> candidates, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task UpsertForecastResultsAsync(IEnumerable<ForecastPredictionRecord> rows, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task UpsertMonthlySalesAsync(IEnumerable<MonthlySalesRecord> rows, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
