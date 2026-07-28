# Sales Forecast

## 功能概览

- `.NET 8` 批量预测（Excel 输入/输出）；
- `MySQL + Dapper + MySqlConnector` 数据持久化；
- 历史销量按 `市场 + SKU + 月份` 幂等 upsert；
- 保存最优参数版本和候选模型排名（每个参数版本最多保存前 100 条候选）；
- 按 `市场 + SKU + 起始预测月份 + horizon` 执行预测并幂等保存结果。

> 市场兼容说明：输入 Excel 可选 `Market/市场` 列；若缺失则默认将 `事业部` 作为市场值（向后兼容旧模板）。

## 1. Excel 批量预测（保持兼容）

```bash
dotnet run --project /home/runner/work/sales-forecast/sales-forecast/src/SalesForecast -- <输入Excel> <输出Excel> [并发数] [训练月数] [预测月数] [季节周期]
```

输入支持列名：

- 市场（可选）：`Market`、`市场`、`市场编码`
- 事业部：`BusinessUnit`、`Business Unit`、`事业部`、`事业部编码`
- SKU：`Sku`、`SKU`、`物料编码`、`商品编码`
- 月份：`Month`、`月份`、`日期`、`年月`
- 销量：`Quantity`、`Qty`、`销量`、`销售数量`、`实际值`、`数量`

输出工作表：

- `预测汇总`
- `预测明细`
- `参数搜索_N`（按行数自动拆分，且每个市场+SKU仅导出前100名候选）
- `说明`

## 2. 数据库初始化（MySQL）

执行 schema：

```sql
SOURCE /home/runner/work/sales-forecast/sales-forecast/src/SalesForecast/database/mysql-schema.sql;
```

或复制文件内容在数据库执行。

## 3. 连接配置

优先使用环境变量：

```bash
export SALES_FORECAST_DB_CONNECTION_STRING="Server=127.0.0.1;Port=3306;Database=sales_forecast;User ID=sales_forecast_user;******;SslMode=Preferred"
```

也可在命令参数末尾直接传完整连接串。

示例模板见：

- `/home/runner/work/sales-forecast/sales-forecast/.env.example`

> 不要提交真实密码。

## 4. 历史数据导入（幂等）

```bash
dotnet run --project /home/runner/work/sales-forecast/sales-forecast/src/SalesForecast -- db-import <输入Excel路径> [连接字符串]
```

规则：

- 按 `market + sku + month` 唯一；
- month 统一归一化为当月第一天；
- quantity 自动钳制为非负；
- 重复执行同键会更新为最新值（幂等 upsert）。

## 5. 按市场+SKU+起始月份预测并落库

```bash
dotnet run --project /home/runner/work/sales-forecast/sales-forecast/src/SalesForecast -- db-forecast-sku <市场> <SKU> <起始月份yyyy-MM> <预测月数> [--search-parameter-if-missing] [连接字符串]
```

行为：

1. 读取数据库历史销量（市场+SKU）；
2. 优先读取已保存参数（最新版本）；
3. 若无参数：
   - 默认报错（清晰提示）；
   - 加 `--search-parameter-if-missing` 时执行参数搜索并保存参数版本 + 候选排名；
4. 按起始预测月份生成 horizon 个月预测并写入 `forecast_results`；
5. 预测结果按 `market + sku + forecast_month` 幂等更新。

参数表保存字段包含：

- 市场、SKU、参数版本、模型类型；
- Alpha/Beta/Gamma、季节周期；
- 训练/验证/测试区间；
- 验证指标（sMAPE/WAPE/MAE）；
- 综合评分、生成时间。

## 6. 分层说明

- `HoltWintersForecaster`：纯算法，不依赖数据库；
- `IForecastRepository`：仓储抽象；
- `MySqlForecastRepository`：Dapper + MySqlConnector 实现（参数化 SQL + 连接释放）；
- `SkuForecastService` / `SalesHistoryImportService`：业务编排层。

## 7. 测试

本仓库当前提供单元测试（包含：
- 历史 upsert 幂等；
- 参数保存与候选 top100；
- 市场/SKU/起始月份选择；
- 缺少参数与数据库不可用错误提示）。

运行：

```bash
dotnet test /home/runner/work/sales-forecast/sales-forecast/tests/SalesForecast.Tests/SalesForecast.Tests.csproj
```

> 当前未包含真实 MySQL 集成测试；如需集成验证，请在可用 MySQL 环境中执行 schema 后运行 `db-import` 与 `db-forecast-sku` 命令。
