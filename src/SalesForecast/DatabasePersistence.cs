using Dapper;
using MySqlConnector;

namespace SalesForecast;

public interface IDbConnectionFactory
{
    Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken = default);
}

public sealed class MySqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("未配置MySQL连接字符串。");

        try
        {
            var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法连接MySQL数据库，请检查连接字符串和数据库可用性。", ex);
        }
    }
}

public interface IHistoricalSalesRepository
{
    Task UpsertAsync(IEnumerable<MonthlySalesRecord> rows, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MonthlySalesRecord>> GetByMarketSkuAsync(string market, string sku, DateTime beforeMonthExclusive, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<DateTime, double>> GetActualQuantitiesAsync(string market, string sku, DateTime startMonth, DateTime endMonth, CancellationToken cancellationToken = default);
}

public interface IBestParameterRepository
{
    Task<long> UpsertAsync(ForecastParameterRecord record, CancellationToken cancellationToken = default);
    Task UpsertCandidatesAsync(long parameterId, IReadOnlyList<ForecastCandidateRecord> candidates, CancellationToken cancellationToken = default);
    Task<ForecastParameterRecord?> GetByMarketSkuAsync(string market, string sku, string parameterVersion, CancellationToken cancellationToken = default);
}

public interface IForecastResultRepository
{
    Task UpsertAsync(IEnumerable<PersistedForecastResultRecord> rows, CancellationToken cancellationToken = default);
}

public sealed class MySqlHistoricalSalesRepository(IDbConnectionFactory connectionFactory) : IHistoricalSalesRepository
{
    public async Task UpsertAsync(IEnumerable<MonthlySalesRecord> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        const string sql = """
            INSERT INTO sales_history
            (business_unit, market, sku, month, quantity, imported_at)
            VALUES
            (@BusinessUnit, @Market, @Sku, @Month, @Quantity, @ImportedAt)
            ON DUPLICATE KEY UPDATE
              business_unit = VALUES(business_unit),
              quantity = VALUES(quantity),
              imported_at = VALUES(imported_at);
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await connection.ExecuteAsync(new CommandDefinition(
                sql,
                new
                {
                    BusinessUnit = row.BusinessUnit.Trim(),
                    Market = NormalizeMarket(row.BusinessUnit, row.Market),
                    Sku = row.Sku.Trim(),
                    Month = NormalizeMonth(row.Month),
                    Quantity = Math.Max(0, row.Quantity),
                    ImportedAt = now
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MonthlySalesRecord>> GetByMarketSkuAsync(string market, string sku, DateTime beforeMonthExclusive, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT business_unit AS BusinessUnit, market AS Market, sku AS Sku, month AS Month, quantity AS Quantity
            FROM sales_history
            WHERE market = @Market AND sku = @Sku AND month < @BeforeMonth
            ORDER BY month;
            """;
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<MonthlySalesRecord>(new CommandDefinition(sql, new
        {
            Market = market.Trim(),
            Sku = sku.Trim(),
            BeforeMonth = NormalizeMonth(beforeMonthExclusive)
        }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<IReadOnlyDictionary<DateTime, double>> GetActualQuantitiesAsync(string market, string sku, DateTime startMonth, DateTime endMonth, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT month AS Month, quantity AS Quantity
            FROM sales_history
            WHERE market = @Market AND sku = @Sku AND month >= @StartMonth AND month <= @EndMonth
            ORDER BY month;
            """;
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync(new CommandDefinition(sql, new
        {
            Market = market.Trim(),
            Sku = sku.Trim(),
            StartMonth = NormalizeMonth(startMonth),
            EndMonth = NormalizeMonth(endMonth)
        }, cancellationToken: cancellationToken));
        return rows.ToDictionary(
            x => NormalizeMonth((DateTime)x.Month),
            x => (double)x.Quantity);
    }

    private static string NormalizeMarket(string businessUnit, string market)
        => string.IsNullOrWhiteSpace(market) ? businessUnit.Trim() : market.Trim();

    private static DateTime NormalizeMonth(DateTime month) => new(month.Year, month.Month, 1);
}

public sealed class MySqlBestParameterRepository(IDbConnectionFactory connectionFactory) : IBestParameterRepository
{
    public async Task<long> UpsertAsync(ForecastParameterRecord record, CancellationToken cancellationToken = default)
    {
        const string upsertSql = """
            INSERT INTO best_forecast_parameters
            (business_unit, market, sku, parameter_version, model_type, alpha, beta, gamma, season_length,
             train_start_month, train_end_month, validation_start_month, validation_end_month,
             test_start_month, test_end_month, validation_smape, validation_wape, validation_mae, validation_score, generated_at)
            VALUES
            (@BusinessUnit, @Market, @Sku, @ParameterVersion, @ModelType, @Alpha, @Beta, @Gamma, @SeasonLength,
             @TrainStartMonth, @TrainEndMonth, @ValidationStartMonth, @ValidationEndMonth,
             @TestStartMonth, @TestEndMonth, @ValidationSmape, @ValidationWape, @ValidationMae, @ValidationScore, @GeneratedAt)
            ON DUPLICATE KEY UPDATE
              business_unit = VALUES(business_unit),
              model_type = VALUES(model_type),
              alpha = VALUES(alpha),
              beta = VALUES(beta),
              gamma = VALUES(gamma),
              season_length = VALUES(season_length),
              train_start_month = VALUES(train_start_month),
              train_end_month = VALUES(train_end_month),
              validation_start_month = VALUES(validation_start_month),
              validation_end_month = VALUES(validation_end_month),
              test_start_month = VALUES(test_start_month),
              test_end_month = VALUES(test_end_month),
              validation_smape = VALUES(validation_smape),
              validation_wape = VALUES(validation_wape),
              validation_mae = VALUES(validation_mae),
              validation_score = VALUES(validation_score),
              generated_at = VALUES(generated_at);
            """;

        const string queryIdSql = """
            SELECT id
            FROM best_forecast_parameters
            WHERE market = @Market AND sku = @Sku AND parameter_version = @ParameterVersion
            LIMIT 1;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(upsertSql, new
        {
            record.BusinessUnit,
            record.Market,
            record.Sku,
            record.ParameterVersion,
            ModelType = record.ModelType.ToString(),
            record.Alpha,
            record.Beta,
            record.Gamma,
            record.SeasonLength,
            TrainStartMonth = NormalizeMonth(record.TrainStartMonth),
            TrainEndMonth = NormalizeMonth(record.TrainEndMonth),
            ValidationStartMonth = NormalizeMonth(record.ValidationStartMonth),
            ValidationEndMonth = NormalizeMonth(record.ValidationEndMonth),
            TestStartMonth = NormalizeMonth(record.TestStartMonth),
            TestEndMonth = NormalizeMonth(record.TestEndMonth),
            record.ValidationSmape,
            record.ValidationWape,
            record.ValidationMae,
            record.ValidationScore,
            record.GeneratedAt
        }, cancellationToken: cancellationToken));
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(queryIdSql, new
        {
            record.Market,
            record.Sku,
            record.ParameterVersion
        }, cancellationToken: cancellationToken));
        return id;
    }

    public async Task UpsertCandidatesAsync(long parameterId, IReadOnlyList<ForecastCandidateRecord> candidates, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO forecast_parameter_candidates
            (parameter_id, rank_no, model_type, alpha, beta, gamma, season_length,
             validation_smape, validation_wape, validation_mae, score, is_selected, created_at)
            VALUES
            (@ParameterId, @Rank, @ModelType, @Alpha, @Beta, @Gamma, @SeasonLength,
             @ValidationSmape, @ValidationWape, @ValidationMae, @Score, @IsSelected, @CreatedAt)
            ON DUPLICATE KEY UPDATE
              model_type = VALUES(model_type),
              alpha = VALUES(alpha),
              beta = VALUES(beta),
              gamma = VALUES(gamma),
              season_length = VALUES(season_length),
              validation_smape = VALUES(validation_smape),
              validation_wape = VALUES(validation_wape),
              validation_mae = VALUES(validation_mae),
              score = VALUES(score),
              is_selected = VALUES(is_selected),
              created_at = VALUES(created_at);
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var candidate in candidates.OrderBy(x => x.Rank).Take(100))
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                ParameterId = parameterId,
                candidate.Rank,
                ModelType = candidate.ModelType.ToString(),
                candidate.Alpha,
                candidate.Beta,
                candidate.Gamma,
                candidate.SeasonLength,
                candidate.ValidationSmape,
                candidate.ValidationWape,
                candidate.ValidationMae,
                candidate.Score,
                candidate.IsSelected,
                CreatedAt = DateTime.UtcNow
            }, transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ForecastParameterRecord?> GetByMarketSkuAsync(string market, string sku, string parameterVersion, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id AS Id, business_unit AS BusinessUnit, market AS Market, sku AS Sku, parameter_version AS ParameterVersion,
                   model_type AS ModelTypeText, alpha AS Alpha, beta AS Beta, gamma AS Gamma, season_length AS SeasonLength,
                   train_start_month AS TrainStartMonth, train_end_month AS TrainEndMonth,
                   validation_start_month AS ValidationStartMonth, validation_end_month AS ValidationEndMonth,
                   test_start_month AS TestStartMonth, test_end_month AS TestEndMonth,
                   validation_smape AS ValidationSmape, validation_wape AS ValidationWape,
                   validation_mae AS ValidationMae, validation_score AS ValidationScore, generated_at AS GeneratedAt
            FROM best_forecast_parameters
            WHERE market = @Market AND sku = @Sku AND parameter_version = @ParameterVersion
            LIMIT 1;
            """;
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var item = await connection.QuerySingleOrDefaultAsync(new CommandDefinition(sql, new
        {
            Market = market.Trim(),
            Sku = sku.Trim(),
            ParameterVersion = parameterVersion.Trim()
        }, cancellationToken: cancellationToken));

        if (item is null)
            return null;

        return new ForecastParameterRecord
        {
            Id = item.Id,
            BusinessUnit = item.BusinessUnit,
            Market = item.Market,
            Sku = item.Sku,
            ParameterVersion = item.ParameterVersion,
            ModelType = Enum.Parse<ForecastModelType>((string)item.ModelTypeText, true),
            Alpha = (double)item.Alpha,
            Beta = (double)item.Beta,
            Gamma = (double)item.Gamma,
            SeasonLength = (int)item.SeasonLength,
            TrainStartMonth = NormalizeMonth((DateTime)item.TrainStartMonth),
            TrainEndMonth = NormalizeMonth((DateTime)item.TrainEndMonth),
            ValidationStartMonth = NormalizeMonth((DateTime)item.ValidationStartMonth),
            ValidationEndMonth = NormalizeMonth((DateTime)item.ValidationEndMonth),
            TestStartMonth = NormalizeMonth((DateTime)item.TestStartMonth),
            TestEndMonth = NormalizeMonth((DateTime)item.TestEndMonth),
            ValidationSmape = (double)item.ValidationSmape,
            ValidationWape = (double)item.ValidationWape,
            ValidationMae = (double)item.ValidationMae,
            ValidationScore = (double)item.ValidationScore,
            GeneratedAt = (DateTime)item.GeneratedAt
        };
    }

    private static DateTime NormalizeMonth(DateTime month) => new(month.Year, month.Month, 1);
}

public sealed class MySqlForecastResultRepository(IDbConnectionFactory connectionFactory) : IForecastResultRepository
{
    public async Task UpsertAsync(IEnumerable<PersistedForecastResultRecord> rows, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO forecast_results
            (business_unit, market, sku, forecast_month, forecast_quantity, actual_quantity,
             absolute_error, absolute_percentage_error, status, parameter_id, parameter_version, created_at)
            VALUES
            (@BusinessUnit, @Market, @Sku, @ForecastMonth, @ForecastQuantity, @ActualQuantity,
             @AbsoluteError, @AbsolutePercentageError, @Status, @ParameterId, @ParameterVersion, @CreatedAt)
            ON DUPLICATE KEY UPDATE
              business_unit = VALUES(business_unit),
              forecast_quantity = VALUES(forecast_quantity),
              actual_quantity = VALUES(actual_quantity),
              absolute_error = VALUES(absolute_error),
              absolute_percentage_error = VALUES(absolute_percentage_error),
              status = VALUES(status),
              parameter_id = VALUES(parameter_id),
              created_at = VALUES(created_at);
            """;
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var row in rows)
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                row.BusinessUnit,
                row.Market,
                row.Sku,
                ForecastMonth = NormalizeMonth(row.ForecastMonth),
                row.ForecastQuantity,
                row.ActualQuantity,
                row.AbsoluteError,
                row.AbsolutePercentageError,
                row.Status,
                row.ParameterId,
                row.ParameterVersion,
                row.CreatedAt
            }, transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static DateTime NormalizeMonth(DateTime month) => new(month.Year, month.Month, 1);
}
