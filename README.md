# ParquetViewer（中文说明）

**简体中文** | [English](README_en.md)

> **前向声明**：本仓库为 [mukunku/ParquetViewer](https://github.com/mukunku/ParquetViewer) 的衍生项目。感谢原项目作者 mukunku 及所有贡献者的开源工作，本仓库在此基础之上继续维护与完善。

ParquetViewer 是一款用于在 Windows 桌面上**查看与查询 Apache Parquet 文件**的轻量级图形化工具。

[![主界面](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/main_screenshot5.png)](#)

## 简介

ParquetViewer 是一个实用工具，帮助你在 Windows 桌面环境下快速查看 Apache Parquet 文件的内容与元数据。无需编写代码，即可完成数据浏览、筛选查询与导出等常见操作。

欢迎通过 Pull Request 为本项目贡献新功能。

## 主要特性

- 查看 Parquet 文件的元数据（Thrift 元数据、pandas 元数据、arrow:schema 等）
- 对 Parquet 数据运行简单的 SQL 风格查询
- 打开单个文件或分区文件夹
- 根据 Parquet 文件自动生成 SQL 建表脚本
- 轻松预览与导出图片和音频数据
- 支持导出 CSV / JSON / Excel（xls、xlsx）/ Parquet 等格式
- 支持 `fixed_size_binary[x]` 等二进制类型的正确解析与多种显示格式（Hex / ASCII / Base64 / Guid 等）
- 支持深色 / 浅色主题切换
- 支持多语言界面（可在 Help → Language 菜单切换）

## 下载

发布版本可在以下地址获取：

https://github.com/ChthollyFan/ParquetViewer/releases

更多使用说明见 [Wiki](https://github.com/ChthollyFan/ParquetViewer/wiki)（本仓库 docs/ 目录亦提供中文版文档）。

## 技术栈

| 组件 | 说明 |
|------|------|
| 框架 | .NET 10（`net10.0-windows`）+ WinForms |
| 默认解析引擎 | Parquet.NET（`ParquetViewer.Engine.ParquetNET`） |
| 可选解析引擎 | DuckDB（`ParquetViewer.Engine.DuckDB`，仅 Self-Contained 发布版包含，用于处理常规版本无法打开的复杂文件） |
| 其他依赖 | Apache.Arrow、MiniExcel、NAudio 等 |

> 说明：为保证常规版安装包体积，DuckDB 引擎仅随 Self-Contained 版本发布。Self-Contained 版本启动稍慢，但无需预装 .NET Desktop Runtime。

## 环境要求

- Windows 10 / 11（x64）
- 常规版：需要安装 .NET 10 Desktop Runtime
- Self-Contained 版：无需预装运行时

## 构建与运行

### 前置条件

- 安装 .NET SDK 10（可通过 [scoop](https://scoop.sh/) 执行 `scoop install dotnet-sdk` 或从 [dotnet.microsoft.com](https://dotnet.microsoft.com/download) 下载）

### 构建

```powershell
# 还原并编译整个解决方案（Debug）
dotnet build src/ParquetViewer.sln -c Debug

# 编译发布版
dotnet build src/ParquetViewer.sln -c Release
```

### 运行

```powershell
# 直接运行编译产物
& "src\ParquetViewer\bin\Debug\net10.0-windows\ParquetViewer.exe"

# 或通过 dotnet run
dotnet run --project src/ParquetViewer/ParquetViewer.csproj
```

### 运行测试

```powershell
dotnet test src/ParquetViewer.Tests/ParquetViewer.Tests.csproj
```

## 快速上手

### 打开 Parquet 文件

1. 菜单：`File → Open`（快捷键 `Ctrl+O`）
2. 选择要打开的 Parquet 文件
3. 选择需要加载的字段（选择的字段越少，加载越快）
4. 默认显示前 1000 条记录（可通过右上角的 Record Count 调整）

### 打开分区文件夹

1. 菜单：`File → Open Folder`（快捷键 `Ctrl+Shift+O`）
2. 选择包含分区 Parquet 数据的文件夹（文件夹及其子目录下的 .parquet 文件将按相对路径字母序加载）

### 运行查询

在顶部查询框中输入 SQL 风格的条件，按回车或点击 Execute 执行。示例：

```sql
WHERE field_name IS NULL
WHERE field_name >= #2000-12-31#
WHERE field_name LIKE '%value%'
WHERE field_name IN ('value1', 'value2')
WHERE (field_1 = 0 AND field_2 <> 'value') OR field_3 IS NULL
```

说明：
- 日期格式使用 `#...#` 包裹，如 `#2000-12-31#`
- List、Map、Struct 字段会以 JSON 字符串形式参与查询
- 含空格或标点的字段名需用方括号转义，如 `[field with spaces!]`
- 查询仅针对已加载进内存的记录生效（默认前 1000 条），可通过增大 Record Count 扩大查询范围

### 二进制列显示

对于 `binary` / `fixed_size_binary[x]` 类型的列，默认行为：
- 字节可打印为 ASCII 时，默认按文本显示（如 `20250828`）
- 无法打印时回退为 Hex 显示（如 `A0-B1-C2`）

右键点击列头可随时切换 Hex / ASCII / Base64 / Guid / IPv4 / IPv6 / Short / Integer / Long / Float / Double / Size 等显示格式。

## 目录结构

```
src/
├── ParquetViewer/                  # WinForms 主程序（UI、导出、网格渲染）
├── ParquetViewer.Engine/           # 引擎公共抽象（接口、值类型、DataTableLite）
├── ParquetViewer.Engine.ParquetNET # Parquet.NET 解析引擎（默认）
├── ParquetViewer.Engine.DuckDB/    # DuckDB 解析引擎（可选）
└── ParquetViewer.Tests/            # 单元测试
wiki_images/                        # 文档用截图
docs/                               # 中文文档（Wiki 中文版）
```

## 使用统计（Analytics）

用户可选择加入匿名使用数据分享，以帮助改进应用。[^1]

可查看 [ParquetViewer Analytics Dashboard](https://app.amplitude.com/analytics/share/7207c0b64c154e979afd7082980d6dd6) 了解整体使用情况。

[^1]: 完整隐私政策：https://github.com/ChthollyFan/ParquetViewer/wiki/Privacy-Policy

## 贡献

- 欢迎提交 Issue 报告问题或功能建议
- 欢迎提交 Pull Request 贡献代码
- 若希望新增界面语言翻译，请参考原项目 Wiki 中的 [Translations](https://github.com/mukunku/ParquetViewer/wiki/Translations) 页面（翻译模板位于 `.github/ISSUE_TEMPLATE/translation_template.csv`）

## 致谢

- 感谢 [mukunku/ParquetViewer](https://github.com/mukunku/ParquetViewer) 原作者与所有贡献者
- 代码签名由 <a href="https://about.signpath.io/">SignPath.io</a> 免费提供，证书来自 <a href="https://signpath.org/">SignPath Foundation</a>

## 许可

本项目基于原项目继续分发，具体许可条款见仓库根目录的 [LICENSE](LICENSE) 文件。