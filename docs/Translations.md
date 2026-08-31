# 翻译

从 [v3.5.1](https://github.com/mukunku/ParquetViewer/releases/tag/v3.5.1.1) 开始，ParquetViewer 支持基础本地化功能。用户可以在 `Help → Language` 菜单中切换界面语言。

# 如何提交新语言的翻译

如果你想为当前不支持的语言提供翻译，帮助改进 ParquetViewer：

1. 下载 [translation_template.csv](https://github.com/ChthollyFan/ParquetViewer/blob/main/.github/ISSUE_TEMPLATE/translation_template.csv)
   - 该模板通过[此工作流](https://github.com/ChthollyFan/ParquetViewer/actions/workflows/generate-translations-template.yaml)保持更新。
2. 为所有条目填写 `NewLanguageValue` 列。
   - 翻译应基于 `EnglishValue` 列。
   - 可忽略 `TurkishValue` 列，它只是作为翻译结构的一个额外示例。
   - 不接受机器翻译。请仅在熟悉目标语言和英语的情况下提供翻译。
3. 在仓库中新建翻译工单：https://github.com/ChthollyFan/ParquetViewer/issues/new?template=translation_proposal.md
   - 在标题中填写 ISO 语言代码（如 `es`）或区域性代码（如 `es-UY`）。
     - 语言代码列表：http://www.lingoes.net/en/translator/langcode.htm
   - 附上你的 `.csv` 文件以供审查。
