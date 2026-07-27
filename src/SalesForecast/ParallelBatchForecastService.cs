using System.Collections.Concurrent;
using System.Diagnostics;

namespace SalesForecast;

/// <summary>按事业部和SKU并行执行预测的服务。</summary>
public sealed class ParallelBatchForecastService
{
    private readonly int defaultTrainLength;
    private readonly int defaultHorizon;
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

        defaultTrainLength = trainLength;
        defaultHorizon = horizon;
        this.seasonLength = seasonLength;
        maxDegreeOfParallelism ??= Math.Max(1, Environment.ProcessorCount - 1);

        if (maxDegreeOfParallelism <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));

        this.maxDegreeOfParallelism = maxDegreeOfParallelism.Value;
    }

    /// <summary>并行处理全部事业部和SKU。</summary>
    public async Task<BatchForecastResult> ProcessAsync(
        IEnumerable<MonthlySalesRecord> source,
        bool useLatestHistory = true,
        IProgress<ForecastProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

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
                var current = Interlocked.Increment(ref completed);
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

        var output = new BatchForecastResult();
        foreach (var result in results
            .OrderBy(x => x.BusinessUnit)
            .ThenBy(x => x.Sku))
        {
            if (result.Summary != null)
                output.Summaries.Add(result.Summary);

            output.Details.AddRange(result.Details.OrderBy(x => x.Month));
        }

        return output;
    }

    /// <summary>同步调用入口。</summary>
    public BatchForecastResult Process(
        IEnumerable<MonthlySalesRecord> source,
        bool useLatestHistory = true,
        IProgress<ForecastProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ProcessAsync(source, useLatestHistory, progress, cancellationToken)
            .GetAwaiter()
            .GetResult();

    /// <summary>处理单个事业部和SKU分组，并按历史长度动态划分训练集和验证集。</summary>
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
            var historyLength = data.Count;

            // 少于3个月没有足够信息，不执行模型计算，但保留跳过记录。
            if (historyLength < 3)
            {
                return Skipped(
                    businessUnit,
                    sku,
                    $"历史月份只有{historyLength}个月，少于3个月，跳过计算。");
            }

            // 36个月以上保持原规则；36个月以内使用该SKU的全部历史数据。
            var selectedLength = Math.Min(historyLength, defaultTrainLength + defaultHorizon);
            var selected = (useLatestHistory
                    ? data.OrderByDescending(x => x.Month).Take(selectedLength)
                    : data.Take(selectedLength))
                .OrderBy(x => x.Month)
                .ToList();

            var (trainLength, validationLength) = GetSplitLength(selected.Count);
            var train = selected.Take(trainLength).ToList();
            var validation = selected.Skip(trainLength).Take(validationLength).ToList();
            var values = selected.Select(x => x.Quantity).ToArray();

            var model = HoltWintersForecaster.SearchBest(
                values,
                trainLength,
                validationLength,
                seasonLength);

            var forecast = model.Forecast;
            var metrics = HoltWintersForecaster.CalculateMetrics(
                validation.Select(x => x.Quantity).ToArray(),
                forecast);

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
            for (var i = 0; i < validation.Count; i++)
            {
                var actual = validation[i].Quantity;
                var predicted = Math.Max(0, forecast[i]);
                var error = predicted - actual;
                var absoluteError = Math.Abs(error);
                absoluteErrorTotal += absoluteError;
                actualTotal += actual;

                details.Add(new ForecastDetailRecord
                {
                    BusinessUnit = businessUnit,
                    Sku = sku,
                    Month = validation[i].Month,
                    DataType = "Test",
                    MonthIndex = i + 1,
                    ActualQuantity = actual,
                    ForecastQuantity = predicted,
                    Error = error,
                    AbsoluteError = absoluteError,
                    AbsolutePercentageError = actual > 0 ? absoluteError / actual : null,
                    Wape = actual > 0 ? absoluteError / actual : null,
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
                Message = $"预测完成（训练{trainLength}个月，验证{validationLength}个月）",
                Summary = new ForecastSummaryRecord
                {
                    BusinessUnit = businessUnit,
                    Sku = sku,
                    TrainStartMonth = train[0].Month,
                    TrainEndMonth = train[^1].Month,
                    TestStartMonth = validation[0].Month,
                    TestEndMonth = validation[^1].Month,
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
                    TestActualQuantity = validation.Sum(x => x.Quantity),
                    TestForecastQuantity = forecast.Sum(),
                    Status = "Success"
                },
                Details = details
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed(businessUnit, sku, ex.Message);
        }
    }

    /// <summary>
    /// 根据补齐后的月份数计算训练/验证比例。
    /// 3-5个月固定最后2个月验证；6-35个月按约3:1划分；36个月以上为30:6。
    /// </summary>
    private (int TrainLength, int ValidationLength) GetSplitLength(int monthCount)
    {
        if (monthCount >= defaultTrainLength + defaultHorizon)
            return (defaultTrainLength, defaultHorizon);

        if (monthCount < 6)
            return (monthCount - 2, 2);

        var validationLength = Math.Max(
            2,
            (int)Math.Round(monthCount / 4d, MidpointRounding.AwayFromZero));

        return (monthCount - validationLength, validationLength);
    }

    private static GroupResult Skipped(
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
                Status = "Skipped",
                ErrorMessage = message
            }
        };

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

    /// <summary>按月聚合数据，并把起止月份之间的缺失月份补为0。</summary>
    private static List<MonthlySalesRecord> Normalize(
        IEnumerable<MonthlySalesRecord> source)
    {
        var values = source
            .GroupBy(x => new DateTime(x.Month.Year, x.Month.Month, 1))
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
