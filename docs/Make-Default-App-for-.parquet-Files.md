# 将 ParquetViewer 设为 .parquet 文件的默认应用

将 ParquetViewer 设置为系统中 `.parquet` 扩展名的默认应用后，即可双击任意 Parquet 文件直接打开查看。

![两个 parquet 文件：一个空白图标，一个 ParquetViewer 图标](https://github.com/ChthollyFan/ParquetViewer/assets/4502154/ff226073-0149-47e1-a986-4157ce475973)

有两种方式可以将 ParquetViewer 设为 Parquet 文件的默认应用。

## 方式一：使用应用内复选框（最简单）

1. 打开 ParquetViewer 后进入 `Help → About` 页面
2. 勾选 `Associate with .parquet files`
3. 在 UAC 提示中点击 **是**（此更改需要管理员权限）
4. 完成！

## 方式二：使用 Windows 文件关联

1. 右键任意 `.parquet` 文件，进入 `属性`
2. 在 `常规` 选项卡中，点击 `打开方式:` 旁的 `更改...` 按钮
3. 滚动到底部，选择 `在电脑上选择应用`
4. 找到 ParquetViewer 可执行文件，点击 `设为默认值` 确认文件关联
5. 完成！
