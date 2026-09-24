# 基础使用

## 打开 Parquet 文件进行查看

1. 进入菜单 `File → Open`（快捷键 `Ctrl+O`）

   ![](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/open.png)

2. 选择要打开的 Parquet 文件

   ![](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/open2.png)

3. 选择要加载的字段
   - 选择的字段越少，数据加载越快

   ![](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/fieldselection.png)

4. 默认在界面中显示前 1000 条记录
   - 记录需要加载到内存才能显示
   - 可通过右上角的设置调整加载数量

## 打开文件夹进行查看

1. 进入菜单 `File → Open Folder`（快捷键 `Ctrl+Shift+O`）
2. 选择包含分区 Parquet 数据的文件夹
   - 文件夹及其子文件夹中的所有 Parquet 文件将按相对路径的字母顺序加载
   - 优先加载扩展名为 `.parquet` 的文件；若未找到，则加载所有文件（无论扩展名）
3. 选择要加载的字段
   - 选择的字段越少，数据加载越快
4. 默认在界面中显示前 1000 条记录
   - 可通过右上角的设置调整加载数量

---

## 选择字段

打开文件后想更改加载的字段集合？

- 进入菜单 `Edit → Add/Remove Fields`（快捷键 `Ctrl+F`）

![](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/addremove.png)

---

## 显示行数

决定从已打开的 Parquet 文件（或文件组）中加载多少条记录并显示在网格中。默认 1000 条。

为避免一次性把整份数据读进内存，单次加载上限为 100 万条，输入更大的值会被截断到该上限。

![显示行数截图](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/recordcount.png)

需要加载更多数据时，请按需增大该值。注意加载更多记录会占用更多系统内存。

也可以点击显示行数旁边的 **加载全部行** 按钮（快捷键 `Ctrl+E`），一次性加载文件中所有行。

![加载全部行按钮截图](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/recordoffset.png)

注意：如果按钮不可用，说明所有记录均已加载，没有更多可加载的数据。

## 起始行（浏览文件）

默认加载并显示前 1000 条记录。起始行从 1 起算，第 1 行就是文件的第一条记录；要查看后面的记录，需要增大 **起始行**：

![起始行截图](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/recordoffset.png)

该选项位于界面右上角。起始行的取值被限制在 1 到文件总行数之间：输入 0 会被校正为 1，输入超过总行数的值会被校正为最后一行。

起始行旁边有三个按钮，从左到右依次是 **上一页**、**下一页**、**加载全部行**：前两个按当前显示行数整页向前 / 向后翻动（已经在第一页或最后一页时对应按钮会置灰），**加载全部行** 则一次性加载文件中所有行。

### 示例

1. 假设一个 Parquet 文件有 28000 条记录。
2. 默认加载前 1000 条：起始行 1，显示行数 1000。
3. 可在状态栏右下角看到：`第 1 到 1000 行`。

   ![](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/0to1000.png)

4. 要查看下一个 1000 条，需将起始行改为 1001（也可以直接点击 **下一页** 按钮，点 **上一页** 则退回上一页）。
5. 工具会自动加载接下来的 1000 条记录。
6. 可通过状态栏确认：`第 1001 到 2000 行`。

   ![](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/1000to2000.png)
