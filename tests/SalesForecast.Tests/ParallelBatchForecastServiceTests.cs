using SalesForecast;
using Xunit;

namespace SalesForecast.Tests;

/// <summary>并行批量预测服务的核心行为测试。</summary>
public sealed class ParallelBatchForecastServiceTests
{
    [Fact]
    public async Task Groups_data_fills_missing_months_and_reports_completion()
    {
        var rows = new List<MonthlySalesRecord>();
        foreach (var businessUnit in new[] { "BU-A", "BU-B" })
        {
            for (var i = 0; i < 36; i++)
            {
                if (i == 3)
                    continue;

                rows.Add(new MonthlySalesRecord
                {
                    BusinessUnit = businessUnit,
                    Sku = "SKU-1",
                    Month = new DateTime(2023, 1, 1).AddMonths(i),
                    Quantity = 10 + i
                });
            }
        }

        var reports = new List<ForecastProgress>();
        var result = await new ParallelBatchForecastService(maxDegreeOfParallelism: 2)
            .ProcessAsync(rows, progress: new Progress<ForecastProgress>(reports.Add));

        Assert.Equal(2, result.Summaries.Count(x => x.Status == "Success"));
        Assert.Equal(72, result.Details.Count);
        Assert.Contains(result.Details, x => x.DataType == "Train"
            && x.Month == new DateTime(2023, 4, 1)
            && x.ActualQuantity == 0);
        Assert.Equal(100, reports.Max(x => x.Percentage));
        Assert.Equal(2, reports.Max(x => x.Completed));
    }

    [Fact]
    public async Task Short_history_is_split_dynamically()
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
        Assert.Equal(new DateTime(2024, 1, 1), summary.TrainStartMonth);
        Assert.Equal(new DateTime(2024, 3, 1), summary.TrainEndMonth);
        Assert.Equal(new DateTime(2024, 4, 1), summary.TestStartMonth);
        Assert.Equal(new DateTime(2024, 5, 1), summary.TestEndMonth);
        Assert.Equal(5, result.Details.Count);
        Assert.Equal(3, result.Details.Count(x => x.DataType == "Train"));
        Assert.Equal(2, result.Details.Count(x => x.DataType == "Test"));
    }

    [Fact]
    public async Task Very_short_history_is_skipped_without_stopping_other_groups()
    {
        var valid = Enumerable.Range(0, 6).Select(i => new MonthlySalesRecord
        {
            BusinessUnit = "BU-OK",
            Sku = "SKU-1",
            Month = new DateTime(2024, 1, 1).AddMonths(i),
            Quantity = 100 + i
        });
        var skipped = Enumerable.Range(0, 2).Select(i => new MonthlySalesRecord
        {
            BusinessUnit = "BU-SKIP",
            Sku = "SKU-1",
            Month = new DateTime(2024, 1, 1).AddMonths(i),
            Quantity = 1
        });

        var result = await new ParallelBatchForecastService(maxDegreeOfParallelism: 2)
            .ProcessAsync(valid.Concat(skipped));

        Assert.Contains(result.Summaries, x => x.BusinessUnit == "BU-OK"
            && x.Status == "Success");
        Assert.Contains(result.Summaries, x => x.BusinessUnit == "BU-SKIP"
            && x.Status == "Skipped");
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new ParallelBatchForecastService().ProcessAsync(
                Array.Empty<MonthlySalesRecord>(),
                cancellationToken: cancellation.Token));
    }
}
