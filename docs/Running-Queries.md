# 运行查询

本工具允许用户对 Parquet 数据运行一些简单的 SQL 风格查询。

## 查询语法

点击界面顶部的 `Filter Query (?)` 标签即可查看语法说明。

语法与 SQL 非常相似，唯一的区别是日期的处理方式。示例查询格式如下：

| 数据类型 | 示例 |
| ------------ | ---------- |
| NULL 判断 | WHERE field_name IS NULL <br> WHERE field_name IS NOT NULL |
| 日期时间 | WHERE field_name >= #2000-12-31# <br> WHERE field_name = #2000-01-13 01:00:00# |
| 数值 | WHERE field_name <= 123.4 <br> WHERE field_name <> 10 |
| 字符串<br><br><sub>通配符：<br>% = 任意字符序列<br>_ = 任意单个字符</sub> | WHERE field_name LIKE '%value%' <br> WHERE field_name NOT LIKE '%value%' <br> WHERE field_name = 'equals value' <br> WHERE field_name <> 'not equals' |
| IN 判断 | WHERE field_name IN ('value1', 'value2') <br> WHERE field_name NOT IN (1, 2) |
| 多条件组合 | WHERE (field_1 = 0 AND field_2 <> 'value') OR field_3 IS NULL |
| 算术运算（+、-、*、/） | WHERE field_1 * (field_2 / field_3) <= 100 |

说明：
- List、Map、Struct 字段在查询时会被自动转换为 JSON 字符串。
- 支持以下日期格式：`yyyy/MM/dd` 和北美格式 `MM/dd/yyyy`。

### 字段名转义

包含空格或标点符号的字段名必须使用方括号转义：

```
WHERE [field with spaces and punctuation!] <> 'not equals'
```

## 执行查询

在界面顶部的查询框中输入查询条件：

![](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/querybox.png)

按 **Enter** 或点击 **Execute** 按钮执行。下方网格将更新为查询结果。可通过状态栏左下角确认有多少条记录被查询条件过滤：

| 查询前 | 查询后 |
| ------------ | ----------- |
| ![](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/beforequery.png) | ![](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/afterquery.png) |

**Clear** 按钮只会移除下方网格中的过滤结果，不会清空已输入的查询文本。在编辑查询时按 **Esc** 键可快速清除现有查询过滤。

## 查询范围

查询仅针对**已加载进应用程序的记录**（默认前 1000 条）生效。要对更多记录执行查询，必须增大 **记录数量（Record Count）**，以将 Apache Parquet 文件中的更多数据加载进应用程序。

加载更多数据需要更多系统内存，因此对于非常大的文件可能会造成负担。参见 [大文件使用技巧](Tips-For-Large-Files.md)。

### 更多工具

如果需要能对**整个文件**执行查询的更强大 SQL 解决方案，可以查看姊妹项目：[DuckSQL](https://github.com/mukunku/DuckSQL)。
