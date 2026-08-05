using SalesForecast;

// 用法：
// dotnet run -- input.xlsx output.xlsx [并发数] [训练月数] [预测月数] [季节周期]
// 示例：
// dotnet run -- sales.xlsx forecast-result.xlsx 6 30 6 12

if (args.Length < 2)
{
    Console.WriteLine("用法：dotnet run -- <输入Excel> <输出Excel> [并发数] [训练月数] [预测月数] [季节周期]");
    return 1;
}

var inputPath = args[0];
var outputPath = args[1];
var maxDegreeOfParallelism = ReadOptionalInt(args, 2, null);
var trainLength = ReadOptionalInt(args, 3, 30)!.Value;
var horizon = ReadOptionalInt(args, 4, 6)!.Value;
var seasonLength = ReadOptionalInt(args, 5, 12)!.Value;

if (trainLength <= 0 || horizon <= 0 || seasonLength <= 1)
{
    Console.Error.WriteLine("训练月数和预测月数必须大于0，季节周期必须大于1。");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
    Console.WriteLine("正在取消预测，请稍候...");
};

var progress = new Progress<ForecastProgress>(info =>
{
    var remaining = info.EstimatedRemaining.HasValue
        ? info.EstimatedRemaining.Value.ToString(@"hh\:mm\:ss")
        : "计算中";

    Console.WriteLine(
        $"[{info.Completed}/{info.Total}] "
        + $"{info.Percentage:F1}% "
        + $"{info.BusinessUnit}/{info.Sku} "
        + $"{info.Message}，预计剩余：{remaining}");
});

try
{
    Console.WriteLine($"开始读取：{inputPath}");
    Console.WriteLine($"训练月数：{trainLength}，预测月数：{horizon}，季节周期：{seasonLength}");
    Console.WriteLine($"并发数：{maxDegreeOfParallelism?.ToString() ?? "默认"}");

    var result = await ExcelForecastRunner.RunAsync(
        inputPath,
        outputPath,
        trainLength,
        horizon,
        seasonLength,
        maxDegreeOfParallelism,
        useLatestHistory: true,
        progress,
        cancellation.Token);

    var success = result.Summaries.Count(x => x.Status == "Success");
    var failed = result.Summaries.Count(x => x.Status == "Failed");

    Console.WriteLine();
    Console.WriteLine("预测完成。");
    Console.WriteLine($"成功分组：{success}");
    Console.WriteLine($"失败分组：{failed}");
    Console.WriteLine($"结果文件：{Path.GetFullPath(outputPath)}");
    return failed == 0 ? 0 : 3;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("预测已取消。");
    return 4;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"预测失败：{ex.Message}");
    return 5;
}

static int? ReadOptionalInt(string[] arguments, int index, int? defaultValue)
{
    if (arguments.Length <= index || string.IsNullOrWhiteSpace(arguments[index]))
        return defaultValue;

    if (int.TryParse(arguments[index], out var value))
        return value;

    throw new ArgumentException($"参数{index + 1}必须是整数：{arguments[index]}");
}
