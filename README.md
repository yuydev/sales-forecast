# Sales Forecast

## 从 Excel 执行批量预测

项目现在提供了一个控制台入口，会读取 Excel、按事业部和 SKU 分组、搜索最优 Alpha/Beta/Gamma 参数、执行预测，并导出汇总和月度明细。

### 输入 Excel 格式

默认读取第一个工作表，第一行必须是表头。支持以下中英文列名：

| 业务字段 | 支持的列名 |
|---|---|
| 事业部 | `BusinessUnit`、`Business Unit`、`事业部`、`事业部编码` |
| SKU | `Sku`、`SKU`、`物料编码`、`商品编码` |
| 月份 | `Month`、`月份`、`日期`、`年月` |
| 销量 | `Quantity`、`Qty`、`销量`、`销售数量`、`实际值`、`数量` |

示例：

| 事业部 | SKU | 月份 | 销量 |
|---|---|---|---:|
| BU001 | SKU001 | 2024-01 | 120 |
| BU001 | SKU001 | 2024-02 | 135 |

缺失月份会在同一事业部和 SKU 的首尾月份之间补为 0。默认取最近 36 个月，前 30 个月训练，后 6 个月测试。

### 运行方式

```bash
dotnet run --project src/SalesForecast -- input.xlsx output.xlsx
```

完整参数顺序：

```bash
dotnet run --project src/SalesForecast -- <输入Excel> <输出Excel> [并发数] [训练月数] [预测月数] [季节周期]
```

例如：

```bash
dotnet run --project src/SalesForecast -- sales.xlsx forecast-result.xlsx 6 30 6 12
```

按 `Ctrl+C` 可以取消正在运行的预测。

输出 Excel 包含：

- `预测汇总`：每个事业部和 SKU 的最优模型、Alpha/Beta/Gamma、验证集指标和测试集指标；
- `预测明细`：训练集和测试集逐月实际值、预测值、误差、APE、WAPE、累计 WAPE、累计 MAE；
- `说明`：输入格式和指标计算说明。

项目使用 ClosedXML 读写 `.xlsx` 文件。
