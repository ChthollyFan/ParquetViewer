# 大文件使用技巧

这里提供一些处理较大 Parquet 文件的技巧。

# 只加载你关心的字段

默认情况下，应用会尝试加载 Parquet 文件中的所有字段。对于较小的文件这可能没问题，但处理大文件时选择所有字段可能效率不高。

![](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/subsetfields.png)

减少要加载的字段数量可以缩短加载时间并降低内存占用，从而允许加载更多记录用于显示和查询。

# 分块查看文件

如果 Parquet 文件记录过多，可能无法一次性全部加载到内存中。

此时可以设置 [显示行数](Basics.md#显示行数) 为你电脑能承受的值：

![](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/recordcount.png)

然后利用 [起始行](Basics.md#起始行浏览文件) 字段浏览文件：

![](https://github.com/ChthollyFan/ParquetViewer/blob/main/wiki_images/recordoffset.png)
