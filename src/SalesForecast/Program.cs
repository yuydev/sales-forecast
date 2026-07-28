using System.Globalization;
using SalesForecast;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
    Console.WriteLine("正在取消，请稍候...");
};

try
{
    if (IsDatabaseImportCommand(args))
        return await RunDatabaseImportAsync(args, cancellation.Token);

    if (IsDatabaseSkuForecastCommand(args))
        return await RunDatabaseSkuForecastAsync(args, cancellation.Token);

    return await RunExcelForecastAsync(args, cancellation.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("操作已取消。");
    return 4;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 5;
}

static async Task<int> RunExcelForecastAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length < 2)
    {
        PrintUsage();
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
        cancellationToken);

    var success = result.Summaries.Count(x => x.Status == "Success");
    var failed = result.Summaries.Count(x => x.Status == "Failed");
    Console.WriteLine();
    Console.WriteLine("预测完成。");
    Console.WriteLine($"成功分组：{success}");
    Console.WriteLine($"失败分组：{failed}");
    Console.WriteLine($"结果文件：{Path.GetFullPath(outputPath)}");
    return failed == 0 ? 0 : 3;
}

static async Task<int> RunDatabaseImportAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("用法：dotnet run -- db-import <输入Excel路径> [连接字符串]");
        return 1;
    }

    var inputPath = args[1];
    var repository = BuildRepository(args.Length > 2 ? args[2] : null);
    var importService = new SalesHistoryImportService(repository);
    var count = await importService.ImportFromExcelAsync(inputPath, cancellationToken);
    Console.WriteLine($"导入完成：{count}条记录已按市场+SKU+月份幂等写入数据库。");
    return 0;
}

static async Task<int> RunDatabaseSkuForecastAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length < 5)
    {
        Console.Error.WriteLine("用法：dotnet run -- db-forecast-sku <市场> <SKU> <起始月份yyyy-MM> <预测月数> [--search-parameter-if-missing] [连接字符串]");
        return 1;
    }

    var market = args[1];
    var sku = args[2];
    if (!DateTime.TryParseExact(args[3], "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startMonthRaw))
        throw new ArgumentException($"无法解析起始预测月份：{args[3]}，请使用yyyy-MM格式。");
    var startMonth = SalesValueNormalizer.NormalizeMonth(startMonthRaw);
    if (!int.TryParse(args[4], out var horizon) || horizon <= 0)
        throw new ArgumentException("预测月数必须是大于0的整数。");

    var searchIfMissing = args.Any(x => string.Equals(x, "--search-parameter-if-missing", StringComparison.OrdinalIgnoreCase));
    var connectionString = args.FirstOrDefault(x => x.StartsWith("Server=", StringComparison.OrdinalIgnoreCase));

    var repository = BuildRepository(connectionString);
    var service = new SkuForecastService(repository);
    var rows = await service.ForecastAsync(new SkuForecastRequest
    {
        Market = market,
        Sku = sku,
        StartForecastMonth = startMonth,
        Horizon = horizon,
        SearchParameterIfMissing = searchIfMissing
    }, cancellationToken);

    Console.WriteLine($"完成预测：市场={market}，SKU={sku}，起始月份={startMonth:yyyy-MM}，月数={horizon}。");
    foreach (var row in rows)
    {
        Console.WriteLine($"{row.ForecastMonth:yyyy-MM}\t预测={row.ForecastQuantity:F4}\t实际={(row.ActualQuantity.HasValue ? row.ActualQuantity.Value.ToString("F4") : "-")}\t状态={row.Status}\t参数版本={row.ParameterVersion}");
    }
    return 0;
}

static IForecastRepository BuildRepository(string? connectionString)
{
    var resolved = string.IsNullOrWhiteSpace(connectionString)
        ? Environment.GetEnvironmentVariable("SALES_FORECAST_DB_CONNECTION_STRING")
        : connectionString;
    if (string.IsNullOrWhiteSpace(resolved))
        throw new InvalidOperationException("未配置数据库连接字符串。请通过参数传入，或设置环境变量 SALES_FORECAST_DB_CONNECTION_STRING。");
    return new MySqlForecastRepository(resolved);
}

static bool IsDatabaseImportCommand(string[] arguments) => string.Equals(arguments[0], "db-import", StringComparison.OrdinalIgnoreCase);
static bool IsDatabaseSkuForecastCommand(string[] arguments) => string.Equals(arguments[0], "db-forecast-sku", StringComparison.OrdinalIgnoreCase);

static int? ReadOptionalInt(string[] arguments, int index, int? defaultValue)
{
    if (arguments.Length <= index || string.IsNullOrWhiteSpace(arguments[index]))
        return defaultValue;
    if (int.TryParse(arguments[index], out var value))
        return value;
    throw new ArgumentException($"参数{index + 1}必须是整数：{arguments[index]}");
}

static void PrintUsage()
{
    Console.WriteLine("Excel批量预测：dotnet run -- <输入Excel> <输出Excel> [并发数] [训练月数] [预测月数] [季节周期]");
    Console.WriteLine("导入历史数据：dotnet run -- db-import <输入Excel路径> [连接字符串]");
    Console.WriteLine("按SKU预测：dotnet run -- db-forecast-sku <市场> <SKU> <起始月份yyyy-MM> <预测月数> [--search-parameter-if-missing] [连接字符串]");
}
