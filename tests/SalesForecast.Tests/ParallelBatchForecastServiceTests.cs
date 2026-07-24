using SalesForecast;
using Xunit;

namespace SalesForecast.Tests;

/// <summary>
/// 并行批量预测服务的核心行为测试。
/// </summary>
public sealed class ParallelBatchForecastServiceTests
{
    [Fact]
    public async Task Groups_data_fills_missing_months_and_reports_completion()
    {
        // 构造两个事业部，每个事业部包含一个SKU和35个月的有效记录。
        var rows = new List<MonthlySalesRecord>();
        foreach (var businessUnit in new[] { "BU-A", "BU-B" })
        {
            for (var i = 0; i < 36; i++)
            {
                // 故意缺少第4个月，用于验证程序是否补零。
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
        var progress = new Progress<ForecastProgress>(reports.Add);

        var result = await new ParallelBatchForecastService(
                maxDegreeOfParallelism: 2)
            .ProcessAsync(rows, progress: progress);

        Assert.Equal(
            2,
            result.Summaries.Count(x => x.Status == "Success"));

        // 每个分组应生成30个月训练明细和6个月测试明细。
        Assert.Equal(72, result.Details.Count);

        Assert.Contains(
            result.Details,
            x => x.DataType == "Train"
                && x.Month == new DateTime(2023, 4, 1)
                && x.ActualQuantity == 0);

        // 并行完成顺序可能不同，但最大进度必须达到100%。
        Assert.Equal(100, reports.Max(x => x.Percentage));
        Assert.Equal(2, reports.Max(x => x.Completed));
    }

    [Fact]
    public async Task Insufficient_group_does_not_stop_other_groups()
    {
        // 一个数据完整的分组和一个历史数据不足的分组。
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

        var result = await new ParallelBatchForecastService(
                maxDegreeOfParallelism: 2)
            .ProcessAsync(valid.Concat(invalid));

        Assert.Contains(
            result.Summaries,
            x => x.BusinessUnit == "BU-OK"
                && x.Status == "Success");

        Assert.Contains(
            result.Summaries,
            x => x.BusinessUnit == "BU-BAD"
                && x.Status == "Failed");
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        // 已取消的令牌应直接抛出OperationCanceledException。
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new ParallelBatchForecastService().ProcessAsync(
                Array.Empty<MonthlySalesRecord>(),
                cancellationToken: cancellation.Token));
    }
}
