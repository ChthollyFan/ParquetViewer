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

## 记录数量（行数）

决定从已打开的 Parquet 文件（或文件组）中加载多少条记录。默认加载 1000 条。

![记录数量截图](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/recordcount.png)

需要加载更多数据时，请按需增大该值。注意加载更多记录会占用更多系统内存。

也可以点击记录数量旁边的 **加载全部记录（Ctrl+E）** 按钮，一次性加载文件中所有行。

![加载全部记录按钮截图](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/recordoffset.png)

注意：如果按钮不可用，说明所有记录均已加载，没有更多可加载的数据。

## 记录偏移（浏览文件）

默认加载并显示前 1000 条记录。若要查看第 2 个 1000 条，例如，需要增大 **记录偏移（Record Offset）**：

![记录偏移截图](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/recordoffset.png)

该选项位于界面右上角。

### 示例

1. 假设一个 Parquet 文件有 28000 条记录。
2. 默认加载前 1000 条：记录偏移 0，记录数量 1000。
3. 可在状态栏右下角看到：`0 to 1000`。

   ![](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/0to1000.png)

4. 要查看下一个 1000 条，需将记录偏移增加 1000。
5. 工具会自动加载接下来的 1000 条记录。
6. 可通过状态栏确认：`1000 to 2000`。

   ![](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/1000to2000.png)
