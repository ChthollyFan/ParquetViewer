# ParquetViewer 使用文档（中文 Wiki）

欢迎使用 ParquetViewer！本目录为项目的**中文 Wiki 文档**，对应原项目的 [英文 Wiki](https://github.com/mukunku/ParquetViewer/wiki)。

## 快速开始

开始使用 ParquetViewer 前，请先阅读 [基础使用](Basics.md)，其中包含如何打开 Parquet 文件并浏览数据。

## 运行 SQL 查询

了解如何使用谓词查询 Parquet 数据，请阅读 [运行查询](Running-Queries.md)。

## 视频教程

社区制作的视频教程：
- [ParquetViewer 使用介绍](https://youtu.be/YV4WjdRv8mc?t=211)
- [如何安装 .NET Desktop Runtime 并首次运行 ParquetViewer](https://www.youtube.com/watch?v=3sQi9Y7BFBw)

## 独立可执行版本（Self-Contained）

两种发布形态的差异如下表：

| | ParquetViewer.exe | ParquetViewer_SelfContained.exe |
|---|---|---|
| 启动速度 | 快 | 慢 |
| 需要 .NET Desktop Runtime | 是 | 否 |
| 包含的解析引擎 | ParquetNET | ParquetNET + DuckDB |

为控制常规版体积，DuckDB 引擎仅随 Self-Contained 版发布。Self-Contained 版可能能打开常规版无法打开的复杂文件，但启动相对较慢。

## 了解更多

更多功能介绍请查阅本目录下的其他页面：

- [大文件使用技巧](Tips-For-Large-Files.md)
- [实用工具](Useful-Tools.md)
- [高级指南](Advanced-Guide.md)
- [用户设置](User-Settings.md)
- [将 ParquetViewer 设为 .parquet 默认应用](Make-Default-App-for-.parquet-Files.md)
- [翻译](Translations.md)
