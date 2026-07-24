using SalesForecast;
using Xunit;

namespace SalesForecast.Tests;

public sealed class ParallelBatchForecastServiceTests
{
    [Fact]
    public async Task Groups_data_fills_missing_months_and_reports_completion()
    {
        var rows = new List<MonthlySalesRecord>();
        foreach (var group in new[] { "BU-A", "BU-B" })
        {
            for (var i = 0; i < 36; i++)
            {
                if (i == 3) continue;
                rows.Add(new MonthlySalesRecord
                {
                    BusinessUnit = group,
                    Sku = "SKU-1",
                    Month = new DateTime(2023, 1, 1).AddMonths(i),
                    Quantity = 10 + i
                });
            }
        }

        var reports = new List<ForecastProgress>();
        var progress = new Progress<ForecastProgress>(reports.Add);
        var result = await new ParallelBatchForecastService(maxDegreeOfParallelism: 2)
            .ProcessAsync(rows, progress: progress);

        Assert.Equal(2, result.Summaries.Count(x => x.Status == "Success"));
        Assert.Equal(72, result.Details.Count);
        Assert.Contains(result.Details, x => x.DataType == "Train" && x.Month == new DateTime(2023, 4, 1) && x.ActualQuantity == 0);
        Assert.Equal(100, reports.Max(x => x.Percentage));
        Assert.Equal(2, reports.Max(x => x.Completed));
    }

    [Fact]
    public async Task Insufficient_group_does_not_stop_other_groups()
    {
        var valid = Enumerable.Range(0, 36).Select(i => new MonthlySalesRecord
        {
            BusinessUnit = "BU-OK",
            Sku = "SKU-1",
            Month = new DateTime(2023, 1, 1).AddMonths(i),
            Quantity = 100 + i
        });
        var invalid = Enumerable.Range(0, 5).Select(i => new MonthlySalesRecord
        {
            BusinessUnit = "BU-BAD",
            Sku = "SKU-1",
            Month = new DateTime(2023, 1, 1).AddMonths(i),
            Quantity = 1
        });

        var result = await new ParallelBatchForecastService(maxDegreeOfParallelism: 2)
            .ProcessAsync(valid.Concat(invalid));

        Assert.Contains(result.Summaries, x => x.BusinessUnit == "BU-OK" && x.Status == "Success");
        Assert.Contains(result.Summaries, x => x.BusinessUnit == "BU-BAD" && x.Status == "Failed");
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new ParallelBatchForecastService().ProcessAsync(
                Array.Empty<MonthlySalesRecord>(),
                cancellationToken: cts.Token));
    }
}
