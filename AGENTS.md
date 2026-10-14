# AGENTS.md

ParquetViewer：Windows 桌面端 Apache Parquet 文件查看与查询工具（WinForms + .NET 10）。本仓库基于 mukunku/ParquetViewer，由 ChthollyFan 继续维护。

## 本机验证

PATH 里的 `dotnet` 只有运行时，SDK 在 scoop，用完整路径调用：

```powershell
$dotnet = Join-Path $env:USERPROFILE "scoop\apps\dotnet-sdk\current\dotnet.exe"
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
- 受限权限下推送会因凭据为空而失败（`git push` 报 `remote: No anonymous write access`），需放宽沙箱权限后重试同一条命令。
- 完成标准：`0 个错误`。既有警告 `ParquetEngine.Processor.cs` CS8601 与本仓库改动无关，新增警告应清零。

## 改动流程（worktree 隔离验证）

修改代码时必须先在独立副本里改完并验证通过，再回到主工作区；禁止先改主工作区、事后再补验证。

```powershell
# 1. 基于当前 HEAD 新建副本（.worktrees/ 已加入 .gitignore）
git worktree add --detach .worktrees\verify HEAD

# 2. 在 .worktrees\verify 里改代码。副本是全新检出、没有 obj/bin，
#    验证前必须先还原依赖（本机约 1 分钟）；$dotnet 沿用上面定义的变量
& $dotnet restore .worktrees\verify\src\ParquetViewer.sln
& $dotnet build .worktrees\verify\src\ParquetViewer.sln -c Debug --no-restore -m:1 -nodeReuse:false
& $dotnet test .worktrees\verify\src\ParquetViewer.sln -c Debug --no-build -m:1 -nodeReuse:false

# 3. 验证通过后导出补丁。先 add -A，否则新增文件不会进补丁
git -C .worktrees\verify add -A
git -C .worktrees\verify diff --cached --binary --output=$env:TEMP\change.patch

# 4. 回到主工作区应用补丁（主工作区常有未提交改动，merge 会被拒绝，因此用 patch）
git apply --binary $env:TEMP\change.patch

# 5. 清理副本与临时补丁，避免 .git/worktrees 残留
git worktree remove --force .worktrees\verify
Remove-Item $env:TEMP\change.patch -Force
```

- 只改 Markdown、README、界面文案等非代码内容时可直接在主工作区改，不必建副本。
- 副本里 `dotnet test` 必须带 `-m:1 -nodeReuse:false`：本机默认的多进程 MSBuild 节点会静默挂死，表现为长时间无输出、进程表里堆满 CPU 近乎为零的空转 dotnet 进程。
- 最低验证要求与主工作区一致：编译 `0 个错误`；能跑测试就一并跑，失败原因要区分是本次改动还是本机环境限制。
- 主工作区已有冲突改动时 `git apply` 会失败，先把冲突处理干净再应用，不要用 `--3way` 掩盖冲突。

## 文件编码

`.cs` / `.resx` 一律 **UTF-8 with BOM + CRLF**；Markdown 为无 BOM + LF，YAML 为无 BOM + CRLF。编辑工具会静默去掉 BOM，改动后确认文件头仍是 `EF BB BF`：

```powershell
$b = [System.IO.File]::ReadAllBytes($path)[0..2]
$b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF
```

换行格式在本仓库要按 git 的口径理解：`core.autocrlf=true` 且没有 `.gitattributes`，`git ls-files --eol` 对 Markdown 报 `i/lf w/crlf` —— git 索引里存的是 LF，工作区检出的是 CRLF。编辑器里看到 CRLF 属正常现象，不要为了「统一成 LF」而重写整个文件，索引里保存的仍是 LF。

`src/.editorconfig` 是代码风格来源，影响最大的三条：4 空格缩进、写显式类型（本项目禁用 `var`）、`insert_final_newline = false`（文件末尾不补空行）。

## 版本号

- 唯一版本来源是 `src/ParquetViewer/Properties/AssemblyInfo.cs` 的 `AssemblyVersion`，写满 4 段（`4.4.0.0`）—— CI 用正则 `(\d+\.\d+\.\d+)` 从该行提取主.次.修订三段来拼 tag，所以 tag 是 `v4.4.0` 而不是 `v4.4.0.0`。
- `SemanticVersion.ToString()` 在 `Build == 0` 时输出 3 段（`4.3.0`），界面与上报因此显示 3 段；`Build != 0` 时保留 4 段。改这条规则要同步 `ParquetViewer.Tests/HelperTests.cs`。
- Release tag 用 `v{主}.{次}.{修订}`（`v4.3.0`），与程序版本数值一致即可，"帮助 → 关于"据此判断有没有新版本。
- "帮助 → 关于"的最新版本取自 `Helpers/Constants.cs` 的 `RELEASES_API_URL`（本仓库 releases）。发布新版本 = 改 `AssemblyVersion` + 建同数值 tag。

## 本地化

- 界面文本在 `.resx`：`Xxx.resx`（英文）、`Xxx.zh-CN.resx`（简体中文）、`Xxx.tr.resx`（土耳其语）；新增语言按 `Xxx.<语言>.resx` 放在同目录。
- "帮助 → 关于"的维护者行是 `AboutBox.resx` / `AboutBox.zh-CN.resx` 里的静态文本 `labelCompanyName.Text`，`AssemblyCompany` 不驱动它。
- 改既有界面文本时逐语言补齐同名 key，否则该语言回退为英文；新增功能的文案只写简体中文并放进中性 `.resx`，英文/土耳其语不补 key 而回退为简体中文，既有译文保持原样。翻译模板与流程见 `docs/Translations.md`。
- 翻译模板由 `.github/workflows/generate-translations-template.yaml` 自动维护：它以 `*.tr.resx` 为 source of truth，与英文 `.resx` 合成后 `Export-Csv` 到 `.github/ISSUE_TEMPLATE/translation_template.csv`，并在 PR 上自动提交回原分支（会给 PR 增加一个 bot 提交）。改完 `.resx` 不必手动同步该 csv。

## 结构

`src/ParquetViewer`（net10.0-windows，WinForms 主程序）、`src/ParquetViewer.Engine`（引擎抽象）、`src/ParquetViewer.Engine.ParquetNET`（默认引擎）、`src/ParquetViewer.Engine.DuckDB`（仅 `Release_SelfContained` 配置引用）、`src/ParquetViewer.Tests`（MSTest）。

中文使用文档在 `docs/`；用户可见的变更要同步 `README.md` 与 `README_en.md`。

### 项目依赖与配置

解决方案只有三个配置；`Release_SelfContained` 在 Engine / ParquetNET / DuckDB / Tests 上映射到各自的 `Release`，只有主程序真正区分：

| 项目 | 目标框架 | 特征 |
| --- | --- | --- |
| `src/ParquetViewer` | net10.0-windows | `WinExe`、`x64`、`PublishSingleFile=true`、`StartupObject=ParquetViewer.Program`、`ApplicationHighDpiMode=PerMonitorV2`、`GenerateAssemblyInfo=false`（版本号在 `Properties/AssemblyInfo.cs`）、`EnforceCodeStyleInBuild=True` |
| `src/ParquetViewer.Engine` | net10.0 | 引擎抽象，无任何引用 |
| `src/ParquetViewer.Engine.ParquetNET` | net10.0 | `x64`，引用 `Engine` 与 Parquet.Net |
| `src/ParquetViewer.Engine.DuckDB` | net10.0 | `x64`，引用 `Engine` 与 DuckDB.NET.Data.Full |
| `src/ParquetViewer.Tests` | net10.0-windows | `x64`，无条件引用上面四个项目（所以 Debug 下也会编译并测试 DuckDB） |

- 主程序对 DuckDB 的引用是**带条件**的（`src/ParquetViewer/ParquetViewer.csproj:82-85`）：`<ItemGroup Condition="'$(Configuration)' == 'Release_SelfContained'">` 里才出现 DuckDB 的 `ProjectReference`，注释原文「We only use DuckDB in the selfcontained release to save on file size」。给主程序加新依赖时注意放对位置，不要让它进入非自包含版。
- 包版本集中管理：`src/Directory.Packages.props`（`ManagePackageVersionsCentrally=true`）是唯一版本来源，各 csproj 的 `PackageReference` 只写包名、不写 `Version`。新增或升级包只改这一个文件，然后重新还原。
- 测试项目把 `Data\**\*.parquet` 以 `CopyToOutputDirectory=PreserveNewest` 复制到输出目录，新增样本文件无需改代码即可按相对路径访问。

## 引擎架构

`IParquetEngine`（`src/ParquetViewer.Engine/IParquetEngine.cs`）是唯一的引擎契约，两个实现分别在 `src/ParquetViewer.Engine.ParquetNET/ParquetEngine.cs`（默认）与 `src/ParquetViewer.Engine.DuckDB/ParquetEngine.cs`（可选）：

```csharp
List<string> Fields; long RecordCount; int NumberOfPartitions;
Dictionary<string, string> CustomMetadata; string Path; IParquetMetadata Metadata;
Task<Func<bool, DataTable>> ReadRowsAsync(List<string> selectedFields, int offset, int recordCount, CancellationToken token, IProgress<int>? progress);
Task WriteDataToParquetFileAsync(DataTable dataTable, string path, CancellationToken token, IProgress<int> progress, Dictionary<string, string>? customMetadata);
IEnumerable<string> GetOpenParquetFilePaths();
```

- 改接口必须同步两个实现与 `src/ParquetViewer.Tests`。两个实现的结果口径（列类型、能否处理 null 复杂类型、二层 list 是否按 struct 处理、schema 路径分隔符）必须一致；确有差异时改测试期望值，或用 `[SkipWhen]` 明确记录。
- 引擎选择与回退在 `src/ParquetViewer/MainForm.cs:338-378`：默认走 `Engine.ParquetNET.ParquetEngine`（`MainForm.cs:261`）；`#if RELEASE_SELFCONTAINED` 下 ParquetNET 抛异常时改用 DuckDB 引擎并 `SwapEngines`，两者都失败才抛 `Exceptions.RowsReadException(unhandledEx, duckDbEx)`；非自包含版（`#else` 分支，`MainForm.cs:373-375`）没有回退，直接失败。状态栏 tooltip 显示当前引擎（`MainForm.cs:473`）。
- 读取行为集中配置在 `src/ParquetViewer.Engine/ParquetEngineSettings.cs`：三个显示格式静态属性，以及 `MaxReadParallelism`（`0`=自动，取逻辑核数与引擎内部上限 8 的较小值；`1`=禁用并行；`>1`=限制并行度）。只有「单文件 + 跨多个 row group」才可能并行。
- 并行分片规则在 `src/ParquetViewer.Engine.ParquetNET/ParquetEngine.Processor.cs:153-265`：分片边界必须对齐 thrift row group，thrift 行数与实际 `RecordCount` 不一致时回退串行；每个分片用**独立的 `ParquetReader`** —— `ParquetRowGroupReader` 共享底层流，并发读会破坏读位置，不能直接用 `Parallel.For` 读同一个 reader。
- 拼接式 parquet（多份文件首尾相接成一份）由 `src/ParquetViewer.Engine.ParquetNET/ParquetSegmentSource.cs` / `ParquetSegmentStream.cs` / `ParquetFileSegments.cs` 支持，必须按片段逐个解析：并行读取与 DuckDB 引擎都不支持这类文件，相关用例用 `[SkipWhen(typeof(DuckDBEngineTests), "…")]` 标注。
- `src/ParquetViewer.Engine/Types/` 放引擎值类型（`IListValue`、`IStructValue`、`IMapValue`、`IByteArrayValue` 及 `DisplayFormat` 枚举 Hex/IPv6/IPv4/Guid/Short/Integer/Long/Float/Double/ASCII/Base64/Size），`src/ParquetViewer.Engine/DataTableLite.cs` 定义 `DataTableLite` / `ColumnLite`。这些是 UI 与导出共用的数据形状，扩展时要同时考虑两个引擎与导出路径。

## 测试

- 抽象基类 `EngineTests`（`src/ParquetViewer.Tests/EngineTests.cs`）定义全部用例，`ParquetNETEngineTests` 与 `DuckDBEngineTests` 各传一组构造参数（`useDuckDBEngine`、`canHandleNullComplexTypes`、`treatsTwoTierListAsStruct`、`schemaPathSeperator`），所以同一份断言会对两个引擎各跑一遍。
- 引擎能力差异用 `[SkippableTestMethod]` + `[SkipWhen(typeof(测试类), "原因")]` 标注（`src/ParquetViewer.Tests/SkipWhenAttribute.cs`：属性 `TestClassToSkip` 收的是**测试类**类型，如 `typeof(DuckDBEngineTests)`，不是引擎类型；`Reason` 要写清缺什么能力；`[AttributeUsage(Method, AllowMultiple = true)]` 允许一个用例挂多条）。
- 样本文件在 `src/ParquetViewer.Tests/Data/`（共 36 个 `.parquet`：32 个在目录根，4 个在 `PARTITIONED_PARQUET_FILE_TEST/` 子目录供分区用例使用），csproj 以 `Data\**\*.parquet` 通配复制到输出目录，文件名与用例对应。
- `[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]`：用例方法级并行，新增用例不要依赖共享可变静态状态。
- 不依赖两引擎差异的纯逻辑断言放 `src/ParquetViewer.Tests/HelperTests.cs`（如 `SEMANTIC_VERSION_PARSER_TESTS`、过滤器 SQL 生成、`ByteArray` 截断）。

## 用户设置

- 设置存注册表 `HKCU\Software\ParquetViewer`（`src/ParquetViewer/AppSettings.cs`），不是配置文件；键包括 `AlwaysSelectAllFields`、`DateTimeDisplayFormat`、`CustomDateFormat`、`AlwaysLoadAllRecords`、`DarkMode`、`UserSelectedCulture`、`QueryEditorZoomLevel`、`OpenedFileCount` 等。新增设置要给它一个默认值，并考虑旧版本遗留值的行为。
- `RemoveLegacyAnalyticsSettings()` 在 `src/ParquetViewer/Program.cs:38` 启动时调用，负责清理已移除的匿名上报遗留键，不能删。
- 文件关联走命令行入口 `ParquetViewer.exe PERFORM_FILE_ASSOCIATION true|false`，由 `Program.cs` 的 `PERFORM_FILE_ASSOCIATION` 分支处理，退出码 `0`=成功、`1`=操作失败、`2`=缺 true/false 参数、`3`=其它异常（`Program.cs:46-74`）。

## 发布

### 发布流程

发版 = **改版本号 → 提交 → 打 tag → 推送 → 创建 Release**，缺一不可：

```powershell
# 1. 改 src\ParquetViewer\Properties\AssemblyInfo.cs 的 AssemblyVersion（例：4.4.0.0）
#    版本号怎么涨：新增功能涨次版本（4.3.0.0 → 4.4.0.0），仅缺陷修复涨修订号（→ 4.3.1.0）

# 2. 提交版本号改动
git add -- src/ParquetViewer/Properties/AssemblyInfo.cs
git commit -m "chore(release): 版本号提升至 4.4.0"

# 3. 打 tag：三段形式，与 AssemblyVersion 的前三段数值一致
git tag v4.4.0

# 4. 推送提交与 tag（两者都要推，tag 不会随分支一起推送）
git push origin main
git push origin v4.4.0

# 5. 构建产物并创建 Release（见下两节）
```

- tag 必须指向含该版本号提交的 commit：`git show --stat v4.4.0` 应能看到 `AssemblyInfo.cs` 的版本号改动。
- 用轻量 tag（`git tag vX.Y.Z`，不加 `-a`），与现存 tag 保持一致。
- tag 一旦推送就不要再移动或删除：程序"帮助 → 关于"按 tag 显示并比较版本。

### 构建产物

本机构建命令与 CI 一致（产物未签名）：

```powershell
$dotnet = Join-Path $env:USERPROFILE "scoop\apps\dotnet-sdk\current\dotnet.exe"

# 常规版：需要目标机器安装 .NET 10 Desktop Runtime
& $dotnet publish src\ParquetViewer\ParquetViewer.csproj -c Release -f net10.0-windows --nologo -o publish -r win-x64 --no-self-contained --no-restore

# 自包含版：不需要安装运行时
& $dotnet publish src\ParquetViewer\ParquetViewer.csproj -c Release_SelfContained -f net10.0-windows --nologo -o publish_selfcontained -r win-x64 --self-contained --no-restore
```

- `publish/` 已在 `.gitignore` 中；`publish_selfcontained/` **没有**被忽略，构建完要清理，别让它出现在 `git status` 里。
- 上传前核对 `(Get-Item publish\ParquetViewer.exe).VersionInfo.FileVersion` 与 tag 数值一致。
- 上传的文件名固定为 `ParquetViewer.exe` 与 `ParquetViewer_SelfContained.exe`，与 CI 产出一致。

### 创建 Release

用 `gh` CLI（本机已登录）。没有 `gh` 时不要试图调用 GitHub API，更不要从 Windows 凭据管理器提取 Git 的令牌：

```powershell
gh release create v4.4.0 --title "v4.4.0" --notes-file publish\RELEASE_NOTES_v4.4.0.md "publish\ParquetViewer.exe" "publish\ParquetViewer_SelfContained.exe"

# 说明写错时只更新说明，不必重建 Release
gh release edit v4.4.0 --notes-file publish\RELEASE_NOTES_v4.4.0.md

# 产物需要重传时
gh release upload v4.4.0 <文件> --clobber
```

- 手动发布的是**正式 Release**（不加 `--prerelease`）；CI 从 PR 创建的 Release 是 prerelease。程序"帮助 → 关于"读的是 releases 列表，两者都能识别，但正式版本语义更正确。
- 创建后核对：`gh release list` 里该版本带 `Latest` 标记，`gh release view v4.4.0 --json assets` 里两个产物都存在。

### Release 说明写什么

说明要覆盖**上一个 tag 到本次 tag 之间的全部提交**，不能只写当次改动。先列提交、再读完整提交信息，逐条归类：

```powershell
git log --oneline --no-merges v4.3.0..v4.4.0
git log --format="%h %s%n%b" --no-merges v4.3.0..v4.4.0
```

章节顺序固定为：

```markdown
## ParquetViewer v4.4.0

Windows 桌面端 Apache Parquet 文件查看与查询工具（中文版发布）。

本版本包含自 v4.3.0 以来的全部改动。

### 新增功能
### 性能优化
### 问题修复
### 其他变更
### 使用说明
### 已知限制

更多文档见仓库 Wiki：https://github.com/ChthollyFan/ParquetViewer/wiki
```

- 每条写清"改了什么、为什么改、对用户的影响"；性能数据要带上实测样本条件，不要只给结论。
- `ci:` / `docs:` / `chore:` 这类内部改动归入「其他变更」，但**不能省略**。
- 「使用说明」沿用历史 Release 的固定写法（常规版需要 .NET 10 Desktop Runtime、自包含版不需要，加上打开方式与功能概述）。
- 写完核对界面文案：说明里提到的按钮名、菜单名要与 `*.zh-CN.resx` 里的实际文本一致。
- 说明文本同时留一份在 `publish/RELEASE_NOTES_vX.Y.Z.md`（该目录被忽略），方便后续用 `gh release edit` 微调。

### CI 发布流程

`.github/workflows/build-test-publish.yaml` 已面向本仓库：`dotnet-version` 为 10.0.x，`Test Report` 与 `checkPublish` 的判断条件为 `ChthollyFan/ParquetViewer`，`publish` 构建常规版与 Self-Contained 版两个 exe 后直接创建 Release —— 上游的 SignPath 签名环节已移除，产物未签名。

- **只监听 `pull_request` 事件**：推送 tag 不会触发 CI，手动发布的 tag 与 Release 都要自己创建（见上文）。
- 同 tag 的 Release 已存在时 `checkPublish` 会跳过发布；它从 `src/ParquetViewer/Properties/AssemblyInfo.cs` 提取版本号拼 tag，正则只取三段，所以 CI 生成的也是 `vX.Y.Z`。
- 本仓库已彻底移除匿名使用数据上报（Analytics/Amplitude），构建流程中不再有 `Inject Amplitude API Key` 步骤。

## 提交

提交信息规范见全局 `~/.dsh/AGENTS.md`。
