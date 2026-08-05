namespace SalesForecast;

public sealed class MonthlySalesRecord
{
    public string BusinessUnit { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public DateTime Month { get; set; }
    public double Quantity { get; set; }
}

public sealed class ForecastSummaryRecord
{
    public string BusinessUnit { get; init; } = string.Empty;
    public string Sku { get; init; } = string.Empty;
    public DateTime TrainStartMonth { get; init; }
    public DateTime TrainEndMonth { get; init; }
    public DateTime TestStartMonth { get; init; }
    public DateTime TestEndMonth { get; init; }
    public ForecastModelType ModelType { get; init; }
    public double Alpha { get; init; }
    public double Beta { get; init; }
    public double Gamma { get; init; }
    public int SeasonLength { get; init; }
    public double ValidationSmape { get; init; }
    public double ValidationWape { get; init; }
    public double ValidationMae { get; init; }
    public double TestSmape { get; init; }
    public double TestWape { get; init; }
    public double TestMae { get; init; }
    public bool SeasonalModelAccepted { get; init; }
    public double SeasonalImprovement { get; init; }
    public double TrainQuantity { get; init; }
    public double TestActualQuantity { get; init; }
    public double TestForecastQuantity { get; init; }
    public double Phi { get; init; }
    public double StabilityPenalty { get; init; }
    public double RawForecastMax { get; init; }
    public double ClampedForecastMax { get; init; }
    public bool WasClamped { get; init; }
    public int ClampedPointCount { get; init; }
    public double ClampUpperBound { get; init; }
    public string Status { get; init; } = "Success";
    public string ErrorMessage { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public sealed class ForecastDetailRecord
{
    public string BusinessUnit { get; init; } = string.Empty;
    public string Sku { get; init; } = string.Empty;
    public DateTime Month { get; init; }
    public string DataType { get; init; } = string.Empty;
    public int MonthIndex { get; init; }
    public double ActualQuantity { get; init; }
    public double ForecastQuantity { get; init; }
    public double Error { get; init; }
    public double AbsoluteError { get; init; }
    public double? AbsolutePercentageError { get; init; }
    public double? Wape { get; init; }
    public double? CumulativeWape { get; init; }
    public double CumulativeMae { get; init; }
}

/// <summary>每个SKU的候选模型搜索结果。</summary>
public sealed class ForecastCandidateRecord
{
    public string BusinessUnit { get; init; } = string.Empty;
    public string Sku { get; init; } = string.Empty;
    public int Rank { get; init; }
    public ForecastModelType ModelType { get; init; }
    public double Alpha { get; init; }
    public double Beta { get; init; }
    public double Gamma { get; init; }
    public double Phi { get; init; }
    public int SeasonLength { get; init; }
    public double ValidationSmape { get; init; }
    public double ValidationWape { get; init; }
    public double ValidationMae { get; init; }
    public double Score { get; init; }
    public double StabilityPenalty { get; init; }
    public bool IsSelected { get; init; }
}

public sealed class BatchForecastResult
{
    public List<ForecastSummaryRecord> Summaries { get; } = new();
    public List<ForecastDetailRecord> Details { get; } = new();
    public List<ForecastCandidateRecord> Candidates { get; } = new();
}

public sealed class ForecastProgress
{
    public int Completed { get; init; }
    public int Total { get; init; }
    public double Percentage { get; init; }
    public string BusinessUnit { get; init; } = string.Empty;
    public string Sku { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public TimeSpan Elapsed { get; init; }
    public TimeSpan? EstimatedRemaining { get; init; }
}
