using SalesForecast;
using Xunit;

namespace SalesForecast.Tests;

public sealed class ParallelBatchForecastServiceTests
{
    [Fact]
    public async Task Thirty_six_month_history_uses_independent_test_window()
    {
        var rows = Enumerable.Range(0, 36).Select(i => new MonthlySalesRecord
        {
            BusinessUnit = "BU-1",
            Sku = "SKU-1",
            Month = new DateTime(2021, 1, 1).AddMonths(i),
            Quantity = 100 + i
        });

        var result = await new ParallelBatchForecastService().ProcessAsync(rows);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal("Success", summary.Status);
        Assert.Equal(new DateTime(2023, 7, 1), summary.TestStartMonth);
        Assert.Equal(new DateTime(2023, 12, 1), summary.TestEndMonth);
        Assert.Equal(24, result.Details.Count(x => x.DataType == "Train"));
        Assert.Equal(6, result.Details.Count(x => x.DataType == "Validation"));
        Assert.Equal(6, result.Details.Count(x => x.DataType == "Test"));
    }

    [Fact]
    public async Task Short_history_is_still_processed_without_independent_test_window()
    {
        var rows = Enumerable.Range(0, 5).Select(i => new MonthlySalesRecord
        {
            BusinessUnit = "BU-SHORT",
            Sku = "SKU-1",
            Month = new DateTime(2024, 1, 1).AddMonths(i),
            Quantity = 10 + i
        });

        var result = await new ParallelBatchForecastService().ProcessAsync(rows);
        var summary = Assert.Single(result.Summaries);

        Assert.Equal("Success", summary.Status);
        Assert.Equal(3, result.Details.Count(x => x.DataType == "Train"));
        Assert.Equal(2, result.Details.Count(x => x.DataType == "Test"));
    }

    [Fact]
    public async Task Two_month_history_is_skipped()
    {
        var rows = Enumerable.Range(0, 2).Select(i => new MonthlySalesRecord
        {
            BusinessUnit = "BU-SKIP",
            Sku = "SKU-1",
            Month = new DateTime(2024, 1, 1).AddMonths(i),
            Quantity = 1
        });

        var result = await new ParallelBatchForecastService().ProcessAsync(rows);
        var summary = Assert.Single(result.Summaries);
        Assert.Equal("Skipped", summary.Status);
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ParallelBatchForecastService().ProcessAsync(Array.Empty<MonthlySalesRecord>(), cancellationToken: cancellation.Token));
    }
}
