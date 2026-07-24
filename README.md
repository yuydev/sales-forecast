# Sales Forecast

## Parallel batch forecasting

`ParallelBatchForecastService` groups data by `BusinessUnit + Sku`, fills missing months with zero, evaluates exponential-smoothing models, and produces summary and monthly detail records.

```csharp
var service = new ParallelBatchForecastService(maxDegreeOfParallelism: 6);
var progress = new Progress<ForecastProgress>(p =>
    Console.WriteLine($"{p.Completed}/{p.Total} {p.Percentage:F1}% {p.BusinessUnit}/{p.Sku}"));
using var cts = new CancellationTokenSource();
var result = await service.ProcessAsync(rows, useLatestHistory: true, progress, cts.Token);
```

Cancellation propagates as `OperationCanceledException`. A normal failure for one SKU is recorded in `Summaries` with `Status = "Failed"` and does not stop other groups.

The default history is the latest 36 months: 30 training months and 6 test months. Set `maxDegreeOfParallelism` explicitly when the process shares CPU or memory with other workloads.
