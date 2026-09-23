# AGENTS.md

ParquetViewer：Windows 桌面端 Apache Parquet 文件查看与查询工具（WinForms + .NET 10）。本仓库基于 mukunku/ParquetViewer，由 ChthollyFan 继续维护。

## 本机验证

PATH 里的 `dotnet` 只有运行时，SDK 在 scoop，用完整路径调用：

```powershell
$dotnet = "C:\Users\zhangweimo\scoop\apps\dotnet-sdk\current\dotnet.exe"
```

在仓库根目录执行，下面三条命令覆盖日常验证：

```powershell
# 编译。-m:1 -nodeReuse:false 是必需的：默认多进程节点在本机会静默失败，
# 只打印“生成失败 / 0 个错误”且不给任何错误信息
& $dotnet build src\ParquetViewer.sln -c Debug --no-restore -m:1 -nodeReuse:false

# 全量重编译：怀疑增量编译没生效时
& $dotnet build src\ParquetViewer.sln -c Debug -t:Rebuild --no-restore -m:1 -nodeReuse:false

# 单元测试
& $dotnet test src\ParquetViewer.sln -c Debug --no-build
```

- `--no-restore`：还原依赖要访问 nuget.org，受限环境下会失败；`obj/project.assets.json` 已存在时跳过还原即可。新增或升级包之后必须联网还原，包版本集中在 `src/Directory.Packages.props`。
- 单测宿主报 `Win32Exception (5)`（拒绝访问）时测试跑不起来，改用临时控制台项目把目标源文件以 `<Compile Include="...">` 链入做等效验证 —— 纯逻辑文件（如 `src/ParquetViewer/Helpers/SemanticVersion.cs`）适用这一招。
- 完成标准：`0 个错误`。既有警告 `ParquetEngine.Processor.cs` CS8601 与本仓库改动无关，新增警告应清零。

## 文件编码

`.cs` / `.resx` 一律 **UTF-8 with BOM + CRLF**；Markdown 为无 BOM + LF，YAML 为无 BOM + CRLF。编辑工具会静默去掉 BOM，改动后确认文件头仍是 `EF BB BF`：

```powershell
$b = [System.IO.File]::ReadAllBytes($path)[0..2]
$b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF
```

`src/.editorconfig` 是代码风格来源，影响最大的三条：4 空格缩进、写显式类型（本项目禁用 `var`）、`insert_final_newline = false`（文件末尾不补空行）。

## 版本号

- 唯一版本来源是 `src/ParquetViewer/Properties/AssemblyInfo.cs` 的 `AssemblyVersion`，写满 4 段（`4.3.0.0`）—— CI 用正则 `(\d+(\.\d+){3})` 从该行提取版本号来生成 tag。
- `SemanticVersion.ToString()` 在 `Build == 0` 时输出 3 段（`4.3.0`），界面与上报因此显示 3 段；`Build != 0` 时保留 4 段。改这条规则要同步 `ParquetViewer.Tests/HelperTests.cs`。
- Release tag 用 `v{主}.{次}.{修订}`（`v4.3.0`），与程序版本数值一致即可，"帮助 → 关于"据此判断有没有新版本。
- "帮助 → 关于"的最新版本取自 `Helpers/Constants.cs` 的 `RELEASES_API_URL`（本仓库 releases）。发布新版本 = 改 `AssemblyVersion` + 建同数值 tag。

## 本地化

- 界面文本在 `.resx`：`Xxx.resx`（英文）、`Xxx.zh-CN.resx`（简体中文）、`Xxx.tr.resx`（土耳其语）；新增语言按 `Xxx.<语言>.resx` 放在同目录。
- "帮助 → 关于"的维护者行是 `AboutBox.resx` / `AboutBox.zh-CN.resx` 里的静态文本 `labelCompanyName.Text`，`AssemblyCompany` 不驱动它。
- 改界面文本时逐语言补齐同名 key，否则该语言回退为英文。翻译模板与流程见 `docs/Translations.md`。

## 结构

`src/ParquetViewer`（net10.0-windows，WinForms 主程序）、`src/ParquetViewer.Engine`（引擎抽象）、`src/ParquetViewer.Engine.ParquetNET`（默认引擎）、`src/ParquetViewer.Engine.DuckDB`（仅 `Release_SelfContained` 配置引用）、`src/ParquetViewer.Tests`（MSTest）。

中文使用文档在 `docs/`；用户可见的变更要同步 `README.md` 与 `README_en.md`。

## 发布

`.github/workflows/build-test-publish.yaml` 已面向本仓库：`dotnet-version` 为 10.0.x，`Test Report` 与 `checkPublish` 的判断条件为 `ChthollyFan/ParquetViewer`，`publish` 构建常规版与 Self-Contained 版两个 exe 后直接创建 Release —— 上游的 SignPath 签名环节已移除，产物未签名。

- PR 触发；同 tag 的 Release 已存在时 `checkPublish` 会跳过发布。
- `Inject Amplitude API Key` 依赖仓库 secret `AMPLITUDE_API_KEY`，未配置时注入空 key，不影响构建。
- 常规版 exe 需要目标机器安装 .NET 10 Desktop Runtime，Self-Contained 版不需要。

## 提交

提交信息规范见全局 `~/.dsh/AGENTS.md`。
