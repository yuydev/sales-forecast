using System.Collections.Concurrent;
using System.Diagnostics;

namespace SalesForecast;

public sealed class ParallelBatchForecastService
{
    private readonly int trainLength;
    private readonly int horizon;
    private readonly int seasonLength;
    private readonly int maxDegreeOfParallelism;

    public ParallelBatchForecastService(int trainLength = 30, int horizon = 6, int seasonLength = 12, int? maxDegreeOfParallelism = null)
    {
        if (trainLength <= 0 || horizon <= 0 || seasonLength <= 1) throw new ArgumentOutOfRangeException();
        this.trainLength = trainLength; this.horizon = horizon; this.seasonLength = seasonLength;
        maxDegreeOfParallelism = maxDegreeOfParallelism ?? Math.Max(1, Environment.ProcessorCount - 1);
        if (maxDegreeOfParallelism <= 0) throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        this.maxDegreeOfParallelism = maxDegreeOfParallelism.Value;
    }

    public async Task<BatchForecastResult> ProcessAsync(IEnumerable<MonthlySalesRecord> source, bool useLatestHistory = true, IProgress<ForecastProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var groups = source.GroupBy(x => new { BusinessUnit = x.BusinessUnit.Trim(), Sku = x.Sku.Trim() }).OrderBy(x => x.Key.BusinessUnit).ThenBy(x => x.Key.Sku).ToList();
        var results = new ConcurrentBag<GroupResult>(); var completed = 0; var watch = Stopwatch.StartNew();
        await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = cancellationToken }, async (group, token) =>
        {
            var result = await Task.Run(() => ProcessGroup(group.Key.BusinessUnit, group.Key.Sku, group, useLatestHistory, token), token);
            results.Add(result);
            var current = Interlocked.Increment(ref completed); var remaining = current == 0 ? null : TimeSpan.FromSeconds(watch.Elapsed.TotalSeconds * (groups.Count - current) / current);
            progress?.Report(new ForecastProgress { Completed = current, Total = groups.Count, Percentage = groups.Count == 0 ? 100 : current * 100d / groups.Count, BusinessUnit = result.BusinessUnit, Sku = result.Sku, Success = result.Success, Message = result.Message, Elapsed = watch.Elapsed, EstimatedRemaining = remaining });
        });
        var output = new BatchForecastResult();
        foreach (var r in results.OrderBy(x => x.BusinessUnit).ThenBy(x => x.Sku)) { if (r.Summary != null) output.Summaries.Add(r.Summary); output.Details.AddRange(r.Details.OrderBy(x => x.Month)); }
        return output;
    }

    public BatchForecastResult Process(IEnumerable<MonthlySalesRecord> source, bool useLatestHistory = true, IProgress<ForecastProgress>? progress = null, CancellationToken cancellationToken = default) => ProcessAsync(source, useLatestHistory, progress, cancellationToken).GetAwaiter().GetResult();

    private GroupResult ProcessGroup(string bu, string sku, IEnumerable<MonthlySalesRecord> source, bool latest, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested(); var data = Normalize(source); if (data.Count < trainLength + horizon) return Failed(bu, sku, $"At least {trainLength + horizon} months are required.");
            var selected = (latest ? data.OrderByDescending(x => x.Month).Take(trainLength + horizon) : data.Take(trainLength + horizon)).OrderBy(x => x.Month).ToList();
            var train = selected.Take(trainLength).ToList(); var test = selected.Skip(trainLength).ToList(); var values = selected.Select(x => x.Quantity).ToArray();
            var model = HoltWintersForecaster.SearchBest(values, trainLength, horizon, seasonLength); var forecast = model.Forecast; var metrics = HoltWintersForecaster.CalculateMetrics(test.Select(x => x.Quantity).ToArray(), forecast);
            var details = new List<ForecastDetailRecord>(); foreach (var x in train) details.Add(new ForecastDetailRecord { BusinessUnit = bu, Sku = sku, Month = x.Month, DataType = "Train", ActualQuantity = x.Quantity });
            double abs = 0, actualTotal = 0; for (var i = 0; i < test.Count; i++) { var actual = test[i].Quantity; var predicted = Math.Max(0, forecast[i]); var error = predicted - actual; abs += Math.Abs(error); actualTotal += actual; details.Add(new ForecastDetailRecord { BusinessUnit = bu, Sku = sku, Month = test[i].Month, DataType = "Test", MonthIndex = i + 1, ActualQuantity = actual, ForecastQuantity = predicted, Error = error, AbsoluteError = Math.Abs(error), AbsolutePercentageError = actual > 0 ? Math.Abs(error) / actual : null, Wape = actual > 0 ? Math.Abs(error) / actual : null, CumulativeWape = actualTotal > 0 ? abs / actualTotal : null, CumulativeMae = abs / (i + 1) }); }
            return new GroupResult { BusinessUnit = bu, Sku = sku, Success = true, Message = "Completed", Summary = new ForecastSummaryRecord { BusinessUnit = bu, Sku = sku, TrainStartMonth = train[0].Month, TrainEndMonth = train[^1].Month, TestStartMonth = test[0].Month, TestEndMonth = test[^1].Month, ModelType = model.ModelType, Alpha = model.Alpha, Beta = model.Beta, Gamma = model.Gamma, SeasonLength = model.SeasonLength, ValidationSmape = model.ValidationMetrics.Smape, ValidationWape = model.ValidationMetrics.Wape, ValidationMae = model.ValidationMetrics.Mae, TestSmape = metrics.Smape, TestWape = metrics.Wape, TestMae = metrics.Mae, SeasonalModelAccepted = model.SeasonalModelAccepted, SeasonalImprovement = model.SeasonalImprovement, TrainQuantity = train.Sum(x => x.Quantity), TestActualQuantity = test.Sum(x => x.Quantity), TestForecastQuantity = forecast.Sum(), Status = "Success" }, Details = details };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Failed(bu, sku, ex.Message); }
    }

    private static GroupResult Failed(string bu, string sku, string message) => new() { BusinessUnit = bu, Sku = sku, Success = false, Message = message, Summary = new ForecastSummaryRecord { BusinessUnit = bu, Sku = sku, Status = "Failed", ErrorMessage = message } };
    private static List<MonthlySalesRecord> Normalize(IEnumerable<MonthlySalesRecord> source) { var values = source.GroupBy(x => new DateTime(x.Month.Year, x.Month.Month, 1)).ToDictionary(x => x.Key, x => x.Sum(y => Math.Max(0, y.Quantity))); if (values.Count == 0) return new(); var result = new List<MonthlySalesRecord>(); for (var m = values.Keys.Min(); m <= values.Keys.Max(); m = m.AddMonths(1)) result.Add(new MonthlySalesRecord { Month = m, Quantity = values.GetValueOrDefault(m) }); return result; }
    private sealed class GroupResult { public string BusinessUnit { get; init; } = ""; public string Sku { get; init; } = ""; public bool Success { get; init; } public string Message { get; init; } = ""; public ForecastSummaryRecord? Summary { get; init; } public List<ForecastDetailRecord> Details { get; init; } = new(); }
}
