using Dapper;
using MySqlConnector;

namespace SalesForecast;

public sealed class MySqlForecastRepository(string connectionString) : IForecastRepository
{
    private readonly string connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("数据库连接字符串不能为空。", nameof(connectionString))
        : connectionString;

    public async Task UpsertMonthlySalesAsync(IEnumerable<MonthlySalesRecord> rows, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO sales_history (market, business_unit, sku, sales_month, quantity, created_at, updated_at)
            VALUES (@Market, @BusinessUnit, @Sku, @SalesMonth, @Quantity, UTC_TIMESTAMP(), UTC_TIMESTAMP())
            ON DUPLICATE KEY UPDATE
                business_unit = VALUES(business_unit),
                quantity = VALUES(quantity),
                updated_at = UTC_TIMESTAMP();
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var market = ParallelBatchForecastService.NormalizeMarket(row.Market, row.BusinessUnit);
            await connection.ExecuteAsync(new CommandDefinition(
                sql,
                new
                {
                    Market = market,
                    BusinessUnit = row.BusinessUnit.Trim(),
                    Sku = row.Sku.Trim(),
                    SalesMonth = NormalizeMonth(row.Month),
                    Quantity = Math.Max(0, row.Quantity)
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MonthlySalesRecord>> GetMonthlySalesAsync(string market, string sku, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                market AS Market,
                business_unit AS BusinessUnit,
                sku AS Sku,
                sales_month AS Month,
                quantity AS Quantity
            FROM sales_history
            WHERE market = @Market AND sku = @Sku
            ORDER BY sales_month;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<MonthlySalesRecord>(new CommandDefinition(
            sql,
            new
            {
                Market = ParallelBatchForecastService.NormalizeMarket(market, market),
                Sku = sku.Trim()
            },
            cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<ForecastParameterRecord?> GetLatestParameterAsync(string market, string sku, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                id AS Id,
                market AS Market,
                sku AS Sku,
                parameter_version AS ParameterVersion,
                model_type AS ModelType,
                alpha AS Alpha,
                beta AS Beta,
                gamma AS Gamma,
                season_length AS SeasonLength,
                train_start_month AS TrainStartMonth,
                train_end_month AS TrainEndMonth,
                validation_start_month AS ValidationStartMonth,
                validation_end_month AS ValidationEndMonth,
                test_start_month AS TestStartMonth,
                test_end_month AS TestEndMonth,
                validation_smape AS ValidationSmape,
                validation_wape AS ValidationWape,
                validation_mae AS ValidationMae,
                composite_score AS CompositeScore,
                generated_at AS GeneratedAt
            FROM forecast_parameter_sets
            WHERE market = @Market AND sku = @Sku
            ORDER BY parameter_version DESC
            LIMIT 1;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync(new CommandDefinition(
            sql,
            new
            {
                Market = ParallelBatchForecastService.NormalizeMarket(market, market),
                Sku = sku.Trim()
            },
            cancellationToken: cancellationToken));

        if (row is null)
            return null;

        return new ForecastParameterRecord
        {
            Id = row.Id,
            Market = row.Market,
            Sku = row.Sku,
            ParameterVersion = row.ParameterVersion,
            ModelType = Enum.Parse<ForecastModelType>((string)row.ModelType),
            Alpha = row.Alpha,
            Beta = row.Beta,
            Gamma = row.Gamma,
            SeasonLength = row.SeasonLength,
            TrainStartMonth = row.TrainStartMonth,
            TrainEndMonth = row.TrainEndMonth,
            ValidationStartMonth = row.ValidationStartMonth,
            ValidationEndMonth = row.ValidationEndMonth,
            TestStartMonth = row.TestStartMonth,
            TestEndMonth = row.TestEndMonth,
            ValidationSmape = row.ValidationSmape,
            ValidationWape = row.ValidationWape,
            ValidationMae = row.ValidationMae,
            CompositeScore = row.CompositeScore,
            GeneratedAt = row.GeneratedAt
        };
    }

    public async Task<ForecastParameterRecord> SaveParameterAsync(ForecastParameterRecord parameter, IReadOnlyCollection<ForecastCandidateRecord> candidates, CancellationToken cancellationToken = default)
    {
        const string nextVersionSql = """
            SELECT COALESCE(MAX(parameter_version), 0) + 1
            FROM forecast_parameter_sets
            WHERE market = @Market AND sku = @Sku
            FOR UPDATE;
            """;
        const string insertParameterSql = """
            INSERT INTO forecast_parameter_sets (
                market, sku, parameter_version, model_type, alpha, beta, gamma, season_length,
                train_start_month, train_end_month, validation_start_month, validation_end_month,
                test_start_month, test_end_month, validation_smape, validation_wape, validation_mae,
                composite_score, generated_at, created_at, updated_at)
            VALUES (
                @Market, @Sku, @ParameterVersion, @ModelType, @Alpha, @Beta, @Gamma, @SeasonLength,
                @TrainStartMonth, @TrainEndMonth, @ValidationStartMonth, @ValidationEndMonth,
                @TestStartMonth, @TestEndMonth, @ValidationSmape, @ValidationWape, @ValidationMae,
                @CompositeScore, @GeneratedAt, UTC_TIMESTAMP(), UTC_TIMESTAMP());
            SELECT LAST_INSERT_ID();
            """;
        const string insertCandidateSql = """
            INSERT INTO forecast_parameter_candidates (
                parameter_set_id, candidate_rank, model_type, alpha, beta, gamma, season_length,
                validation_smape, validation_wape, validation_mae, composite_score, is_selected, created_at, updated_at)
            VALUES (
                @ParameterSetId, @CandidateRank, @ModelType, @Alpha, @Beta, @Gamma, @SeasonLength,
                @ValidationSmape, @ValidationWape, @ValidationMae, @CompositeScore, @IsSelected, UTC_TIMESTAMP(), UTC_TIMESTAMP())
            ON DUPLICATE KEY UPDATE
                model_type = VALUES(model_type),
                alpha = VALUES(alpha),
                beta = VALUES(beta),
                gamma = VALUES(gamma),
                season_length = VALUES(season_length),
                validation_smape = VALUES(validation_smape),
                validation_wape = VALUES(validation_wape),
                validation_mae = VALUES(validation_mae),
                composite_score = VALUES(composite_score),
                is_selected = VALUES(is_selected),
                updated_at = UTC_TIMESTAMP();
            """;

        var market = ParallelBatchForecastService.NormalizeMarket(parameter.Market, parameter.Market);
        var sku = parameter.Sku.Trim();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var nextVersion = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            nextVersionSql,
            new { Market = market, Sku = sku },
            transaction,
            cancellationToken: cancellationToken));

        var parameterId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            insertParameterSql,
            new
            {
                Market = market,
                Sku = sku,
                ParameterVersion = nextVersion,
                ModelType = parameter.ModelType.ToString(),
                parameter.Alpha,
                parameter.Beta,
                parameter.Gamma,
                parameter.SeasonLength,
                TrainStartMonth = NormalizeMonth(parameter.TrainStartMonth),
                TrainEndMonth = NormalizeMonth(parameter.TrainEndMonth),
                ValidationStartMonth = NormalizeMonth(parameter.ValidationStartMonth),
                ValidationEndMonth = NormalizeMonth(parameter.ValidationEndMonth),
                TestStartMonth = NormalizeMonth(parameter.TestStartMonth),
                TestEndMonth = NormalizeMonth(parameter.TestEndMonth),
                parameter.ValidationSmape,
                parameter.ValidationWape,
                parameter.ValidationMae,
                CompositeScore = parameter.CompositeScore,
                parameter.GeneratedAt
            },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var candidate in candidates.OrderBy(x => x.Rank).Take(100))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                insertCandidateSql,
                new
                {
                    ParameterSetId = parameterId,
                    CandidateRank = candidate.Rank,
                    ModelType = candidate.ModelType.ToString(),
                    candidate.Alpha,
                    candidate.Beta,
                    candidate.Gamma,
                    candidate.SeasonLength,
                    candidate.ValidationSmape,
                    candidate.ValidationWape,
                    candidate.ValidationMae,
                    CompositeScore = candidate.Score,
                    IsSelected = candidate.IsSelected
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return new ForecastParameterRecord
        {
            Id = parameterId,
            Market = market,
            Sku = sku,
            ParameterVersion = nextVersion,
            ModelType = parameter.ModelType,
            Alpha = parameter.Alpha,
            Beta = parameter.Beta,
            Gamma = parameter.Gamma,
            SeasonLength = parameter.SeasonLength,
            TrainStartMonth = parameter.TrainStartMonth,
            TrainEndMonth = parameter.TrainEndMonth,
            ValidationStartMonth = parameter.ValidationStartMonth,
            ValidationEndMonth = parameter.ValidationEndMonth,
            TestStartMonth = parameter.TestStartMonth,
            TestEndMonth = parameter.TestEndMonth,
            ValidationSmape = parameter.ValidationSmape,
            ValidationWape = parameter.ValidationWape,
            ValidationMae = parameter.ValidationMae,
            CompositeScore = parameter.CompositeScore,
            GeneratedAt = parameter.GeneratedAt
        };
    }

    public async Task UpsertForecastResultsAsync(IEnumerable<ForecastPredictionRecord> rows, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO forecast_results (
                market, sku, start_forecast_month, forecast_month, forecast_quantity, actual_quantity,
                error, status, parameter_set_id, parameter_version, created_at, updated_at)
            VALUES (
                @Market, @Sku, @StartForecastMonth, @ForecastMonth, @ForecastQuantity, @ActualQuantity,
                @Error, @Status, @ParameterSetId, @ParameterVersion, @CreatedAt, UTC_TIMESTAMP())
            ON DUPLICATE KEY UPDATE
                start_forecast_month = VALUES(start_forecast_month),
                forecast_quantity = VALUES(forecast_quantity),
                actual_quantity = VALUES(actual_quantity),
                error = VALUES(error),
                status = VALUES(status),
                parameter_set_id = VALUES(parameter_set_id),
                parameter_version = VALUES(parameter_version),
                updated_at = UTC_TIMESTAMP();
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var row in rows)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                sql,
                new
                {
                    Market = ParallelBatchForecastService.NormalizeMarket(row.Market, row.Market),
                    Sku = row.Sku.Trim(),
                    StartForecastMonth = NormalizeMonth(row.StartForecastMonth),
                    ForecastMonth = NormalizeMonth(row.ForecastMonth),
                    ForecastQuantity = Math.Max(0, row.ForecastQuantity),
                    ActualQuantity = row.ActualQuantity.HasValue ? Math.Max(0, row.ActualQuantity.Value) : (double?)null,
                    row.Error,
                    row.Status,
                    ParameterSetId = row.ParameterRecordId,
                    row.ParameterVersion,
                    row.CreatedAt
                },
                transaction,
                cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<MySqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法连接MySQL数据库：{ex.Message}", ex);
        }
    }

    private static DateTime NormalizeMonth(DateTime month) => new(month.Year, month.Month, 1);
}
