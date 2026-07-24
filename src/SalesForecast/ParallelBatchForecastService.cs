using System.Collections.Concurrent;
using System.Diagnostics;

namespace SalesForecast;

/// <summary>
/// 按事业部和SKU并行执行预测的服务。
/// 一个分组对应一个独立的月度时间序列。
/// </summary>
public sealed class ParallelBatchForecastService
{
    private readonly int trainLength;
    private readonly int horizon;
    private readonly int seasonLength;
    private readonly int maxDegreeOfParallelism;

    public ParallelBatchForecastService(
        int trainLength = 30,
        int horizon = 6,
        int seasonLength = 12,
        int? maxDegreeOfParallelism = null)
    {
        if (trainLength <= 0 || horizon <= 0 || seasonLength <= 1)
            throw new ArgumentOutOfRangeException();

        this.trainLength = trainLength;
        this.horizon = horizon;
        this.seasonLength = seasonLength;

        // 默认保留一个CPU核心给系统和其他应用使用。
        maxDegreeOfParallelism ??= Math.Max(
            1,
            Environment.ProcessorCount - 1);

        if (maxDegreeOfParallelism <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxDegreeOfParallelism));

        this.maxDegreeOfParallelism = maxDegreeOfParallelism.Value;
    }

    /// <summary>
    /// 并行处理全部事业部和SKU。
    /// </summary>
    public async Task<BatchForecastResult> ProcessAsync(
        IEnumerable<MonthlySalesRecord> source,
        bool useLatestHistory = true,
        IProgress<ForecastProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // 先在主线程完成分组，避免并行期间重复枚举输入数据。
        var groups = source
            .GroupBy(x => new
            {
                BusinessUnit = x.BusinessUnit.Trim(),
                Sku = x.Sku.Trim()
            })
            .OrderBy(x => x.Key.BusinessUnit)
            .ThenBy(x => x.Key.Sku)
            .ToList();

        var results = new ConcurrentBag<GroupResult>();
        var completed = 0;
        var watch = Stopwatch.StartNew();

        // 使用受控并行，避免SKU数量较大时占满线程池和CPU。
        await Parallel.ForEachAsync(
            groups,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
                CancellationToken = cancellationToken
            },
            async (group, token) =>
            {
                var result = await Task.Run(
                    () => ProcessGroup(
                        group.Key.BusinessUnit,
                        group.Key.Sku,
                        group,
                        useLatestHistory,
                        token),
                    token);

                results.Add(result);

                // Interlocked保证多个并行任务更新完成数时不会发生竞争。
                var current = Interlocked.Increment(ref completed);

                // 预计剩余时间可能未知，因此使用可空的TimeSpan。
                TimeSpan? remaining = current == 0
                    ? null
                    : TimeSpan.FromSeconds(
                        watch.Elapsed.TotalSeconds
                        * (groups.Count - current)
                        / current);

                progress?.Report(new ForecastProgress
                {
                    Completed = current,
                    Total = groups.Count,
                    Percentage = groups.Count == 0
                        ? 100
                        : current * 100d / groups.Count,
                    BusinessUnit = result.BusinessUnit,
                    Sku = result.Sku,
                    Success = result.Success,
                    Message = result.Message,
                    Elapsed = watch.Elapsed,
                    EstimatedRemaining = remaining
                });
            });

        // ConcurrentBag不保证顺序，输出前重新排序保证结果稳定。
        var output = new BatchForecastResult();

        foreach (var result in results
            .OrderBy(x => x.BusinessUnit)
            .ThenBy(x => x.Sku))
        {
            if (result.Summary != null)
                output.Summaries.Add(result.Summary);

            output.Details.AddRange(
                result.Details.OrderBy(x => x.Month));
        }

        return output;
    }

    /// <summary>同步调用入口，异步应用建议优先使用ProcessAsync。</summary>
    public BatchForecastResult Process(
        IEnumerable<MonthlySalesRecord> source,
        bool useLatestHistory = true,
        IProgress<ForecastProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ProcessAsync(
            source,
            useLatestHistory,
            progress,
            cancellationToken)
        .GetAwaiter()
        .GetResult();

    /// <summary>处理单个事业部和SKU分组。</summary>
    private GroupResult ProcessGroup(
        string businessUnit,
        string sku,
        IEnumerable<MonthlySalesRecord> source,
        bool useLatestHistory,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var data = Normalize(source);
            if (data.Count < trainLength + horizon)
            {
                return Failed(
                    businessUnit,
                    sku,
                    $"至少需要{trainLength + horizon}个月历史数据。");
            }

            // 默认取最近36个月：前30个月训练，后6个月测试。
            var selected = (useLatestHistory
                    ? data.OrderByDescending(x => x.Month)
                        .Take(trainLength + horizon)
                    : data.Take(trainLength + horizon))
                .OrderBy(x => x.Month)
                .ToList();

            var train = selected.Take(trainLength).ToList();
            var test = selected.Skip(trainLength).ToList();
            var values = selected.Select(x => x.Quantity).ToArray();

            var model = HoltWintersForecaster.SearchBest(
                values,
                trainLength,
                horizon,
                seasonLength);

            var forecast = model.Forecast;
            var metrics = HoltWintersForecaster.CalculateMetrics(
                test.Select(x => x.Quantity).ToArray(),
                forecast);

            // 生成训练集和测试集逐月明细。
            var details = new List<ForecastDetailRecord>();
            foreach (var item in train)
            {
                details.Add(new ForecastDetailRecord
                {
                    BusinessUnit = businessUnit,
                    Sku = sku,
                    Month = item.Month,
                    DataType = "Train",
                    ActualQuantity = item.Quantity
                });
            }

            double absoluteErrorTotal = 0;
            double actualTotal = 0;

            for (var i = 0; i < test.Count; i++)
            {
                var actual = test[i].Quantity;
                var predicted = Math.Max(0, forecast[i]);
                var error = predicted - actual;
                var absoluteError = Math.Abs(error);

                absoluteErrorTotal += absoluteError;
                actualTotal += actual;

                details.Add(new ForecastDetailRecord
                {
                    BusinessUnit = businessUnit,
                    Sku = sku,
                    Month = test[i].Month,
                    DataType = "Test",
                    MonthIndex = i + 1,
                    ActualQuantity = actual,
                    ForecastQuantity = predicted,
                    Error = error,
                    AbsoluteError = absoluteError,
                    AbsolutePercentageError = actual > 0
                        ? absoluteError / actual
                        : null,
                    Wape = actual > 0
                        ? absoluteError / actual
                        : null,
                    CumulativeWape = actualTotal > 0
                        ? absoluteErrorTotal / actualTotal
                        : null,
                    CumulativeMae = absoluteErrorTotal / (i + 1)
                });
            }

            return new GroupResult
            {
                BusinessUnit = businessUnit,
                Sku = sku,
                Success = true,
                Message = "预测完成",
                Summary = new ForecastSummaryRecord
                {
                    BusinessUnit = businessUnit,
                    Sku = sku,
                    TrainStartMonth = train[0].Month,
                    TrainEndMonth = train[^1].Month,
                    TestStartMonth = test[0].Month,
                    TestEndMonth = test[^1].Month,
                    ModelType = model.ModelType,
                    Alpha = model.Alpha,
                    Beta = model.Beta,
                    Gamma = model.Gamma,
                    SeasonLength = model.SeasonLength,
                    ValidationSmape = model.ValidationMetrics.Smape,
                    ValidationWape = model.ValidationMetrics.Wape,
                    ValidationMae = model.ValidationMetrics.Mae,
                    TestSmape = metrics.Smape,
                    TestWape = metrics.Wape,
                    TestMae = metrics.Mae,
                    SeasonalModelAccepted = model.SeasonalModelAccepted,
                    SeasonalImprovement = model.SeasonalImprovement,
                    TrainQuantity = train.Sum(x => x.Quantity),
                    TestActualQuantity = test.Sum(x => x.Quantity),
                    TestForecastQuantity = forecast.Sum(),
                    Status = "Success"
                },
                Details = details
            };
        }
        catch (OperationCanceledException)
        {
            // 取消异常必须继续向上传播，不能当作普通SKU失败处理。
            throw;
        }
        catch (Exception ex)
        {
            // 普通单SKU异常只记录失败，不影响其他SKU继续运行。
            return Failed(businessUnit, sku, ex.Message);
        }
    }

    private static GroupResult Failed(
        string businessUnit,
        string sku,
        string message) =>
        new()
        {
            BusinessUnit = businessUnit,
            Sku = sku,
            Success = false,
            Message = message,
            Summary = new ForecastSummaryRecord
            {
                BusinessUnit = businessUnit,
                Sku = sku,
                Status = "Failed",
                ErrorMessage = message
            }
        };

    /// <summary>
    /// 按月份聚合数据，并将起止月份之间的缺失月份补为0。
    /// </summary>
    private static List<MonthlySalesRecord> Normalize(
        IEnumerable<MonthlySalesRecord> source)
    {
        var values = source
            .GroupBy(x => new DateTime(
                x.Month.Year,
                x.Month.Month,
                1))
            .ToDictionary(
                x => x.Key,
                x => x.Sum(y => Math.Max(0, y.Quantity)));

        if (values.Count == 0)
            return new List<MonthlySalesRecord>();

        var result = new List<MonthlySalesRecord>();
        for (var month = values.Keys.Min();
             month <= values.Keys.Max();
             month = month.AddMonths(1))
        {
            result.Add(new MonthlySalesRecord
            {
                Month = month,
                Quantity = values.GetValueOrDefault(month)
            });
        }

        return result;
    }

    private sealed class GroupResult
    {
        public string BusinessUnit { get; init; } = string.Empty;
        public string Sku { get; init; } = string.Empty;
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public ForecastSummaryRecord? Summary { get; init; }
        public List<ForecastDetailRecord> Details { get; init; } = new();
    }
}
