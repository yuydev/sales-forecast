# Sales Forecast

## Excel批量预测

程序会读取Excel，按事业部和SKU分组，搜索最优模型及Alpha/Beta/Gamma参数，并导出预测汇总和逐月明细。

### 历史长度划分规则

程序会先在每个事业部和SKU内部按月份聚合，并将首尾月份之间缺失的月份补为0，然后按补齐后的月份数划分训练集和验证集：

| 补齐后月份数 | 处理方式 |
|---:|---|
| 0-2个月 | 跳过计算，汇总状态为`Skipped` |
| 3-5个月 | 前面的月份训练，最后2个月验证 |
| 6-35个月 | 按约3:1划分，验证集至少2个月 |
| 36个月及以上 | 取最近36个月，30个月训练、6个月验证 |

例如：6个月为4个月训练+2个月验证，10个月为7个月训练+3个月验证，20个月为15个月训练+5个月验证。

短历史序列不启用Holt-Winters季节模型；季节模型只有在训练集至少包含两个完整季节周期，并且验证误差至少改善5%时才会采用。

### 输入Excel格式

默认读取第一个工作表，第一行必须是表头。支持以下中英文列名：

| 业务字段 | 支持的列名 |
|---|---|
| 事业部 | `BusinessUnit`、`Business Unit`、`事业部`、`事业部编码` |
| SKU | `Sku`、`SKU`、`物料编码`、`商品编码` |
| 月份 | `Month`、`月份`、`日期`、`年月` |
| 销量 | `Quantity`、`Qty`、`销量`、`销售数量`、`实际值`、`数量` |

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

按`Ctrl+C`可以取消正在运行的预测。输出Excel包含`预测汇总`、`预测明细`和`说明`三个工作表。
