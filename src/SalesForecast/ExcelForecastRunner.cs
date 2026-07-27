using ClosedXML.Excel;

namespace SalesForecast;

public static class ExcelForecastRunner
{
    // 其余Excel读取逻辑保持不变；ExportResult新增“参数搜索”工作表。
    public static void ExportResult(string outputPath, BatchForecastResult result)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using var workbook = new XLWorkbook();
        AddSummarySheet(workbook, result.Summaries);
        AddDetailSheet(workbook, result.Details);
        AddCandidateSheet(workbook, result.Candidates);
        AddReadmeSheet(workbook);
        workbook.SaveAs(outputPath);
    }

    private static void AddCandidateSheet(XLWorkbook workbook, IReadOnlyCollection<ForecastCandidateRecord> candidates)
    {
        var sheet = workbook.Worksheets.Add("参数搜索");
        var headers = new[] { "排名", "模型类型", "Alpha", "Beta", "Gamma", "季节周期", "验证集sMAPE", "验证集WAPE", "验证集MAE", "综合评分", "是否选中" };
        WriteHeaders(sheet, headers);
        var row = 2;
        foreach (var item in candidates)
        {
            // 候选记录目前按每个SKU独立生成；事业部和SKU通过明细上下文保留在模型对象之外时可继续扩展。
            sheet.Cell(row, 1).Value = item.Rank;
            sheet.Cell(row, 2).Value = item.ModelType.ToString();
            sheet.Cell(row, 3).Value = item.Alpha;
            sheet.Cell(row, 4).Value = item.Beta;
            sheet.Cell(row, 5).Value = item.Gamma;
            sheet.Cell(row, 6).Value = item.SeasonLength;
            sheet.Cell(row, 7).Value = item.ValidationSmape;
            sheet.Cell(row, 8).Value = item.ValidationWape;
            sheet.Cell(row, 9).Value = item.ValidationMae;
            sheet.Cell(row, 10).Value = item.Score;
            sheet.Cell(row, 11).Value = item.IsSelected ? "是" : "否";
            row++;
        }
        FormatTable(sheet, row - 1, headers.Length);
        sheet.Columns(3, 5).Style.NumberFormat.Format = "0.00";
        sheet.Columns(7, 8).Style.NumberFormat.Format = "0.00%";
        sheet.Column(10).Style.NumberFormat.Format = "0.000000";
    }

    private static void AddSummarySheet(XLWorkbook workbook, IReadOnlyCollection<ForecastSummaryRecord> summaries)
    {
        var sheet = workbook.Worksheets.Add("预测汇总");
        var headers = new[] { "事业部", "SKU", "训练开始月份", "训练结束月份", "测试开始月份", "测试结束月份", "模型类型", "Alpha", "Beta", "Gamma", "季节周期", "验证集sMAPE", "验证集WAPE", "验证集MAE", "测试集sMAPE", "测试集WAPE", "测试集MAE", "是否采用季节模型", "季节模型改善比例", "训练销量", "测试实际销量", "测试预测销量", "状态", "错误信息", "创建时间" };
        WriteHeaders(sheet, headers);
        var row = 2;
        foreach (var item in summaries)
        {
            var values = new object?[] { item.BusinessUnit, item.Sku, item.TrainStartMonth, item.TrainEndMonth, item.TestStartMonth, item.TestEndMonth, item.ModelType.ToString(), item.Alpha, item.Beta, item.Gamma, item.SeasonLength, item.ValidationSmape, item.ValidationWape, item.ValidationMae, item.TestSmape, item.TestWape, item.TestMae, item.SeasonalModelAccepted ? "是" : "否", item.SeasonalImprovement, item.TrainQuantity, item.TestActualQuantity, item.TestForecastQuantity, item.Status, item.ErrorMessage, item.CreatedAt };
            for (var i = 0; i < values.Length; i++) sheet.Cell(row, i + 1).Value = values[i]?.ToString() ?? string.Empty;
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

    private static void AddDetailSheet(XLWorkbook workbook, IReadOnlyCollection<ForecastDetailRecord> details) { var sheet = workbook.Worksheets.Add("预测明细"); var headers = new[] { "事业部", "SKU", "月份", "数据类型", "测试月份序号", "实际值", "预测值", "误差", "绝对误差", "绝对百分比误差", "单月WAPE", "累计WAPE", "累计MAE" }; WriteHeaders(sheet, headers); var row = 2; foreach (var item in details) { sheet.Cell(row, 1).Value = item.BusinessUnit; sheet.Cell(row, 2).Value = item.Sku; sheet.Cell(row, 3).Value = item.Month; sheet.Cell(row, 4).Value = item.DataType; sheet.Cell(row, 5).Value = item.MonthIndex; sheet.Cell(row, 6).Value = item.ActualQuantity; sheet.Cell(row, 7).Value = item.ForecastQuantity; sheet.Cell(row, 8).Value = item.Error; sheet.Cell(row, 9).Value = item.AbsoluteError; WriteNullable(sheet.Cell(row, 10), item.AbsolutePercentageError); WriteNullable(sheet.Cell(row, 11), item.Wape); WriteNullable(sheet.Cell(row, 12), item.CumulativeWape); sheet.Cell(row, 13).Value = item.CumulativeMae; row++; } FormatTable(sheet, row - 1, headers.Length); sheet.Column(3).Style.DateFormat.Format = "yyyy-mm"; sheet.Columns(10, 12).Style.NumberFormat.Format = "0.00%"; }
    private static void AddReadmeSheet(XLWorkbook workbook) { var sheet = workbook.Worksheets.Add("说明"); sheet.Cell(1, 1).Value = "参数搜索工作表包含每个SKU的有效候选模型，按综合评分升序排列，排名1不一定是最终选中模型，因为季节模型还需要满足至少5%的改善阈值。"; sheet.Columns().AdjustToContents(); }
    private static void WriteHeaders(IXLWorksheet sheet, IReadOnlyList<string> headers) { for (var i = 0; i < headers.Count; i++) { sheet.Cell(1, i + 1).Value = headers[i]; sheet.Cell(1, i + 1).Style.Font.Bold = true; sheet.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.LightBlue; } }
    private static void FormatTable(IXLWorksheet sheet, int lastRow, int columnCount) { if (lastRow < 1) return; sheet.Range(1, 1, lastRow, columnCount).SetAutoFilter(); sheet.SheetView.FreezeRows(1); sheet.Columns().AdjustToContents(); }
    private static void WriteNullable(IXLCell cell, double? value) { if (value.HasValue && double.IsFinite(value.Value)) cell.Value = value.Value; }
}
