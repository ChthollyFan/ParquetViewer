# 实用工具

工具位于 **`Tools`** 菜单下。

## 生成 SQL 建表脚本

该工具根据当前已加载的 Parquet 文件的 schema 生成 ANSI SQL `CREATE TABLE` 脚本。当需要一个与 Parquet 文件 schema 匹配的 SQL 表时非常方便。

脚本只包含已加载的字段。

## 元数据查看器

该工具读取每个 Parquet 文件中存储的 Thrift 元数据以及发现的任何自定义元数据。

支持的元数据类型：`thrift`（默认）、`pandas`、`arrow:schema`。其他元数据将原样显示。如果遇到不可读的自定义元数据，欢迎提交 Issue 反馈，看看能否为这类元数据增加支持。
