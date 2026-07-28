namespace SalesForecast;

public sealed class SalesHistoryImportService(IForecastRepository repository)
{
    private readonly IForecastRepository repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<int> ImportFromExcelAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        var rows = ExcelForecastRunner.ReadRows(inputPath);
        await repository.UpsertMonthlySalesAsync(rows, cancellationToken);
        return rows.Count;
    }
}
