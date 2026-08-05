using ClosedXML.Excel;

namespace SalesForecast;

/// <summary>Excel输入、预测执行和结果导出。</summary>
public static class ExcelForecastRunner
{
    private const int MaxRowsPerCandidateSheet = 900_000;
    private const int MaxCandidatesPerSku = 100;

    private static readonly string[] BusinessUnitHeaders = ["BusinessUnit", "Business Unit", "事业部", "事业部编码"];
    private static readonly string[] SkuHeaders = ["Sku", "SKU", "sku", "物料编码", "商品编码"];
    private static readonly string[] MonthHeaders = ["Month", "月份", "日期", "年月"];
    private static readonly string[] QuantityHeaders = ["Quantity", "Qty", "销量", "销售数量", "实际值", "数量"];

    public static async Task<BatchForecastResult> RunAsync(
        string inputPath,
        string outputPath,
        int trainLength = 30,
        int horizon = 6,
        int seasonLength = 12,
        int? maxDegreeOfParallelism = null,
        bool useLatestHistory = true,
        IProgress<ForecastProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var rows = ReadRows(inputPath);
        var service = new ParallelBatchForecastService(trainLength, horizon, seasonLength, maxDegreeOfParallelism);
        var result = await service.ProcessAsync(rows, useLatestHistory, progress, cancellationToken);
        ExportResult(outputPath, result);
        return result;
    }

    public static List<MonthlySalesRecord> ReadRows(string inputPath)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("找不到输入Excel文件。", inputPath);

        using var workbook = new XLWorkbook(inputPath);
        var worksheet = workbook.Worksheets.FirstOrDefault()
            ?? throw new InvalidDataException("Excel文件没有工作表。");
        var headerRow = worksheet.FirstRowUsed()
            ?? throw new InvalidDataException("Excel文件为空。");

        var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.CellsUsed())
        {
            var header = NormalizeHeader(cell.GetString());
            if (!string.IsNullOrWhiteSpace(header))
                headerMap[header] = cell.Address.ColumnNumber;
        }

        var businessUnitColumn = FindColumn(headerMap, BusinessUnitHeaders);
        var skuColumn = FindColumn(headerMap, SkuHeaders);
        var monthColumn = FindColumn(headerMap, MonthHeaders);
        var quantityColumn = FindColumn(headerMap, QuantityHeaders);
        var rows = new List<MonthlySalesRecord>();

        foreach (var row in worksheet.RowsUsed().Skip(1))
        {
            if (row.CellsUsed().All(x => x.IsEmpty()))
                continue;

            var businessUnit = row.Cell(businessUnitColumn).GetString().Trim();
            var sku = row.Cell(skuColumn).GetString().Trim();
            if (string.IsNullOrWhiteSpace(businessUnit) || string.IsNullOrWhiteSpace(sku))
                continue;

            rows.Add(new MonthlySalesRecord
            {
                BusinessUnit = businessUnit,
                Sku = sku,
                Month = ReadMonth(row.Cell(monthColumn)),
                Quantity = ReadQuantity(row.Cell(quantityColumn))
            });
        }

        if (rows.Count == 0)
            throw new InvalidDataException("Excel中没有可用的销售数据。");

        return rows;
    }

    public static void ExportResult(string outputPath, BatchForecastResult result)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var workbook = new XLWorkbook();
        AddSummarySheet(workbook, result.Summaries);
        AddDetailSheet(workbook, result.Details);
        AddCandidateSheets(workbook, result.Candidates);
        AddReadmeSheet(workbook);
        workbook.SaveAs(outputPath);
    }

    /// <summary>
    /// 将每个事业部和SKU的前100名候选模型拆分到多个工作表，避免输出文件过大。
    /// </summary>
    private static void AddCandidateSheets(
        XLWorkbook workbook,
        IReadOnlyCollection<ForecastCandidateRecord> candidates)
    {
        var headers = new[]
        {
            "事业部", "SKU", "排名", "模型类型", "Alpha", "Beta", "Gamma",
            "季节周期", "验证集sMAPE", "验证集WAPE", "验证集MAE", "综合评分", "是否选中"
        };

        // 每个事业部和SKU只保留综合评分排名前100的候选模型。
        var ordered = candidates
            .GroupBy(x => new { x.BusinessUnit, x.Sku })
            .SelectMany(group => group
                .OrderBy(x => x.Rank)
                .Take(MaxCandidatesPerSku))
            .OrderBy(x => x.BusinessUnit)
            .ThenBy(x => x.Sku)
            .ThenBy(x => x.Rank)
            .ToList();

        var sheetIndex = 1;
        IXLWorksheet? sheet = null;
        var row = 0;

        foreach (var item in ordered)
        {
            // 每个工作表预留一行表头，并使用低于Excel上限的安全阈值。
            if (sheet is null || row >= MaxRowsPerCandidateSheet)
            {
                sheet = workbook.Worksheets.Add($"参数搜索_{sheetIndex++}");
                WriteHeaders(sheet, headers);
                row = 2;
            }

            sheet.Cell(row, 1).Value = item.BusinessUnit;
            sheet.Cell(row, 2).Value = item.Sku;
            sheet.Cell(row, 3).Value = item.Rank;
            sheet.Cell(row, 4).Value = item.ModelType.ToString();
            sheet.Cell(row, 5).Value = item.Alpha;
            sheet.Cell(row, 6).Value = item.Beta;
            sheet.Cell(row, 7).Value = item.Gamma;
            sheet.Cell(row, 8).Value = item.SeasonLength;
            sheet.Cell(row, 9).Value = item.ValidationSmape;
            sheet.Cell(row, 10).Value = item.ValidationWape;
            sheet.Cell(row, 11).Value = item.ValidationMae;
            sheet.Cell(row, 12).Value = item.Score;
            sheet.Cell(row, 13).Value = item.IsSelected ? "是" : "否";
            row++;
        }

        for (var i = 1; i < sheetIndex; i++)
        {
            var candidateSheet = workbook.Worksheet($"参数搜索_{i}");
            var lastRow = candidateSheet.LastRowUsed()?.RowNumber() ?? 1;
            FormatTable(candidateSheet, lastRow, headers.Length);
            candidateSheet.Columns(5, 7).Style.NumberFormat.Format = "0.00";
            candidateSheet.Columns(9, 10).Style.NumberFormat.Format = "0.00%";
            candidateSheet.Column(12).Style.NumberFormat.Format = "0.000000";
        }
    }

    private static void AddSummarySheet(XLWorkbook workbook, IReadOnlyCollection<ForecastSummaryRecord> summaries)
    {
        var sheet = workbook.Worksheets.Add("预测汇总");
        var headers = new[]
        {
            "事业部", "SKU", "训练开始月份", "训练结束月份", "测试开始月份", "测试结束月份",
            "模型类型", "Alpha", "Beta", "Gamma", "季节周期", "验证集sMAPE", "验证集WAPE",
            "验证集MAE", "测试集sMAPE", "测试集WAPE", "测试集MAE", "是否采用季节模型",
            "季节模型改善比例", "训练销量", "测试实际销量", "测试预测销量", "状态", "错误信息", "创建时间"
        };
        WriteHeaders(sheet, headers);
        var row = 2;
        foreach (var item in summaries)
        {
            var values = new object?[]
            {
                item.BusinessUnit, item.Sku, item.TrainStartMonth, item.TrainEndMonth,
                item.TestStartMonth, item.TestEndMonth, item.ModelType.ToString(), item.Alpha,
                item.Beta, item.Gamma, item.SeasonLength, item.ValidationSmape,
                item.ValidationWape, item.ValidationMae, item.TestSmape, item.TestWape,
                item.TestMae, item.SeasonalModelAccepted ? "是" : "否", item.SeasonalImprovement,
                item.TrainQuantity, item.TestActualQuantity, item.TestForecastQuantity,
                item.Status, item.ErrorMessage, item.CreatedAt
            };

            for (var i = 0; i < values.Length; i++)
                sheet.Cell(row, i + 1).Value = values[i]?.ToString() ?? string.Empty;
            row++;
        }

        FormatTable(sheet, row - 1, headers.Length);
        sheet.Columns(3, 6).Style.DateFormat.Format = "yyyy-mm";
        sheet.Column(25).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
        sheet.Columns(8, 10).Style.NumberFormat.Format = "0.00";
        sheet.Columns(12, 14).Style.NumberFormat.Format = "0.00%";
        sheet.Columns(15, 16).Style.NumberFormat.Format = "0.00%";
        sheet.Column(19).Style.NumberFormat.Format = "0.00%";
    }

    private static void AddDetailSheet(XLWorkbook workbook, IReadOnlyCollection<ForecastDetailRecord> details)
    {
        var sheet = workbook.Worksheets.Add("预测明细");
        var headers = new[]
        {
            "事业部", "SKU", "月份", "数据类型", "测试月份序号", "实际值", "预测值",
            "误差", "绝对误差", "绝对百分比误差", "单月WAPE", "累计WAPE", "累计MAE"
        };
        WriteHeaders(sheet, headers);
        var row = 2;
        foreach (var item in details)
        {
            sheet.Cell(row, 1).Value = item.BusinessUnit;
            sheet.Cell(row, 2).Value = item.Sku;
            sheet.Cell(row, 3).Value = item.Month;
            sheet.Cell(row, 4).Value = item.DataType;
            sheet.Cell(row, 5).Value = item.MonthIndex;
            sheet.Cell(row, 6).Value = item.ActualQuantity;
            sheet.Cell(row, 7).Value = item.ForecastQuantity;
            sheet.Cell(row, 8).Value = item.Error;
            sheet.Cell(row, 9).Value = item.AbsoluteError;
            WriteNullable(sheet.Cell(row, 10), item.AbsolutePercentageError);
            WriteNullable(sheet.Cell(row, 11), item.Wape);
            WriteNullable(sheet.Cell(row, 12), item.CumulativeWape);
            sheet.Cell(row, 13).Value = item.CumulativeMae;
            row++;
        }

        FormatTable(sheet, row - 1, headers.Length);
        sheet.Column(3).Style.DateFormat.Format = "yyyy-mm";
        sheet.Columns(10, 12).Style.NumberFormat.Format = "0.00%";
        sheet.Columns(6, 9).Style.NumberFormat.Format = "0.####";
        sheet.Column(13).Style.NumberFormat.Format = "0.####";
    }

    private static void AddReadmeSheet(XLWorkbook workbook)
    {
        var sheet = workbook.Worksheets.Add("说明");
        sheet.Cell(1, 1).Value = "说明";
        sheet.Cell(2, 1).Value = "输入表第一行必须包含：事业部、SKU、月份、销量；支持中英文表头。";
        sheet.Cell(3, 1).Value = "缺失月份会在同一事业部和SKU的首尾月份之间补为0。";
        sheet.Cell(4, 1).Value = "参数搜索结果按事业部、SKU和排名排序，每个SKU只导出综合评分前100名候选模型。";
        sheet.Cell(5, 1).Value = "参数搜索结果自动拆分到多个工作表，每个工作表最多写入900000行数据。";
        sheet.Columns().AdjustToContents();
    }

    private static void WriteHeaders(IXLWorksheet sheet, IReadOnlyList<string> headers)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            sheet.Cell(1, i + 1).Value = headers[i];
            sheet.Cell(1, i + 1).Style.Font.Bold = true;
            sheet.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.LightBlue;
        }
    }

    private static void FormatTable(IXLWorksheet sheet, int lastRow, int columnCount)
    {
        const int maxExcelRow = 1_048_576;
        lastRow = Math.Min(lastRow, maxExcelRow);
        if (lastRow < 1)
            return;

        sheet.Range(1, 1, lastRow, columnCount).SetAutoFilter();
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();
    }

    private static void WriteNullable(IXLCell cell, double? value)
    {
        if (value.HasValue && double.IsFinite(value.Value))
            cell.Value = value.Value;
    }

    private static int FindColumn(IReadOnlyDictionary<string, int> headerMap, IEnumerable<string> aliases)
    {
        foreach (var alias in aliases)
        {
            var normalized = NormalizeHeader(alias);
            if (headerMap.TryGetValue(normalized, out var column))
                return column;
        }
        throw new InvalidDataException($"Excel缺少必要列：{string.Join("、", aliases)}。");
    }

    private static string NormalizeHeader(string value) => value.Trim().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

    private static DateTime ReadMonth(IXLCell cell)
    {
        if (cell.TryGetValue<DateTime>(out var date))
            return new DateTime(date.Year, date.Month, 1);

        var text = cell.GetString().Trim();
        if (DateTime.TryParse(text, out date))
            return new DateTime(date.Year, date.Month, 1);

        if (double.TryParse(text, out var serial) && serial > 0)
        {
            date = DateTime.FromOADate(serial);
            return new DateTime(date.Year, date.Month, 1);
        }

        throw new InvalidDataException($"无法解析月份：{cell.Address}={cell.GetString()}。");
    }

    private static double ReadQuantity(IXLCell cell)
    {
        if (cell.TryGetValue<double>(out var quantity) && double.IsFinite(quantity))
            return Math.Max(0, quantity);

        var text = cell.GetString().Trim();
        if (double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out quantity))
            return Math.Max(0, quantity);

        throw new InvalidDataException($"无法解析销量：{cell.Address}={cell.GetString()}。");
    }
}
