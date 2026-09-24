using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Parquet.Meta;

namespace ParquetViewer.Engine.ParquetNET
{
    /// <summary>
    /// 识别"多个 parquet 文件首尾拼接在同一个文件里"的数据，并定位其中每个片段的区间。
    /// </summary>
    /// <remarks>
    /// 这类文件的 footer 只属于最后一个片段，而 footer 中记录的列块偏移是片段内的相对偏移。
    /// 按整文件解析时会 seek 到错误位置读到别的片段数据（表现为乱码或 Thrift 解析失败），
    /// 因此需要借助 PAR1 魔数定位片段边界，把每个片段当作独立 parquet 文件解析。
    /// </remarks>
    internal static class ParquetFileSegments
    {
        /// <summary>parquet 魔数长度</summary>
        private const int MAGIC_LENGTH = 4;

        /// <summary>文件末尾 footer 长度字段的大小（4 字节小端整数）</summary>
        private const int FOOTER_LENGTH_FIELD_SIZE = 4;

        /// <summary>扫描片段边界时的读取块大小</summary>
        private const int SCAN_BUFFER_SIZE = 1 << 20;

        /// <summary>拼接文件的魔法数对判定间距：片段尾部魔数与其后紧邻的下一片段头部魔数只隔 4 字节</summary>
        private const long ADJACENT_MAGIC_GAP = MAGIC_LENGTH;

        /// <summary>单个片段的最小合法长度（魔数 + footer 长度字段 + 空 footer）</summary>
        private const long MIN_SEGMENT_LENGTH = MAGIC_LENGTH + FOOTER_LENGTH_FIELD_SIZE + MAGIC_LENGTH;

        /// <summary>判定偏移不匹配的最小空洞占比：空洞超过文件大小的 1% 即认为 footer 偏移不可信</summary>
        private const long MIN_UNMATCHED_GAP_RATIO_DIVISOR = 100;

        /// <summary>
        /// 判定偏移不匹配的最小空洞绝对值（1KB）。
        /// 取小值是刻意的：这里的判定只负责"是否值得扫描魔数"，误判的代价仅是一次全文件扫描，
        /// 而漏判会让拼接文件按整文件解析并静默返回错误数据。
        /// </summary>
        private const long MIN_UNMATCHED_GAP_BYTES = 1024;

        private static ReadOnlySpan<byte> ParquetMagic => "PAR1"u8;

        /// <summary>
        /// 判断文件的 footer 偏移是否与真实数据位置不匹配（拼接文件的典型特征）。
        /// </summary>
        /// <param name="filePath">parquet 文件路径</param>
        /// <param name="fileLength">文件长度</param>
        /// <param name="metadata">按整文件解析得到的 thrift 元数据</param>
        /// <returns>偏移明显不匹配返回 true</returns>
        /// <remarks>
        /// 正常 parquet 文件的 footer 紧跟在最后一个列块（及其可选的页索引）之后；拼接文件的 footer 位于文件末尾，
        /// 但其中记录的列块偏移最大只有片段自身那么大，于是 footer 起始位置会远远大于列块结束位置。
        /// </remarks>
        public static bool HasUnmatchedFooterOffset(string filePath, long fileLength, FileMetaData metadata)
        {
            if (!TryGetFooterStart(filePath, fileLength, out long footerStart))
            {
                return false;
            }

            long lastColumnChunkEnd = GetLastColumnChunkEndOffset(metadata);
            if (lastColumnChunkEnd <= 0)
            {
                return false;
            }

            long gap = footerStart - lastColumnChunkEnd;
            long threshold = Math.Max(MIN_UNMATCHED_GAP_BYTES, fileLength / MIN_UNMATCHED_GAP_RATIO_DIVISOR);
            return gap > threshold;
        }

        /// <summary>
        /// 读取文件尾部的 footer 起始偏移。
        /// </summary>
        /// <param name="filePath">parquet 文件路径</param>
        /// <param name="fileLength">文件长度</param>
        /// <param name="footerStart">输出 footer 起始偏移</param>
        /// <returns>尾部结构合法时返回 true</returns>
        public static bool TryGetFooterStart(string filePath, long fileLength, out long footerStart)
        {
            footerStart = 0;
            if (fileLength < MIN_SEGMENT_LENGTH)
            {
                return false;
            }

            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> tail = stackalloc byte[FOOTER_LENGTH_FIELD_SIZE + MAGIC_LENGTH];
            stream.Seek(fileLength - tail.Length, SeekOrigin.Begin);
            stream.ReadExactly(tail);

            if (!tail[FOOTER_LENGTH_FIELD_SIZE..].SequenceEqual(ParquetMagic))
            {
                return false;
            }

            int footerLength = BinaryPrimitives.ReadInt32LittleEndian(tail[..FOOTER_LENGTH_FIELD_SIZE]);
            if (footerLength <= 0 || footerLength > fileLength - tail.Length)
            {
                return false;
            }

            footerStart = fileLength - tail.Length - footerLength;
            return true;
        }

        /// <summary>
        /// 计算元数据中所有列块的结束偏移最大值。
        /// </summary>
        /// <param name="metadata">thrift 元数据</param>
        /// <returns>最后一个列块的结束偏移；无法计算时返回 0</returns>
        public static long GetLastColumnChunkEndOffset(FileMetaData metadata)
        {
            if (metadata.RowGroups is null)
            {
                return 0;
            }

            long lastEnd = 0;
            foreach (RowGroup rowGroup in metadata.RowGroups)
            {
                if (rowGroup.Columns is null)
                {
                    continue;
                }

                foreach (ColumnChunk column in rowGroup.Columns)
                {
                    ColumnMetaData? columnMetadata = column.MetaData;
                    if (columnMetadata is null)
                    {
                        continue;
                    }

                    // 有字典页时数据从字典页开始；没有字典页时从数据页开始
                    long start = columnMetadata.DictionaryPageOffset ?? columnMetadata.DataPageOffset;
                    long end = start + columnMetadata.TotalCompressedSize;
                    if (end > lastEnd)
                    {
                        lastEnd = end;
                    }
                }
            }

            return lastEnd;
        }

        /// <summary>
        /// 扫描文件中的 parquet 片段边界。
        /// </summary>
        /// <param name="fileHandle">已打开的文件句柄</param>
        /// <param name="fileLength">文件长度</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>按顺序排列的片段区间；没有发现片段边界时返回空列表（表示按普通单文件处理）</returns>
        public static List<(long Offset, long Length)> FindSegments(SafeFileHandle fileHandle, long fileLength, CancellationToken cancellationToken)
        {
            List<(long Offset, long Length)> segments = new();
            List<long> magicPositions = FindMagicPositions(fileHandle, fileLength, cancellationToken);

            // 普通单文件的魔数只出现在文件头和文件尾，拼接文件的片段尾部魔数与下一片段头部魔数之间只隔 4 字节
            if (magicPositions.Count < 2 || magicPositions[0] != 0)
            {
                return segments;
            }

            long segmentStart = 0;
            for (int i = 0; i + 1 < magicPositions.Count; i++)
            {
                if (magicPositions[i + 1] - magicPositions[i] != ADJACENT_MAGIC_GAP)
                {
                    continue;
                }

                long segmentEnd = magicPositions[i] + MAGIC_LENGTH;
                if (segmentEnd - segmentStart < MIN_SEGMENT_LENGTH)
                {
                    continue;
                }

                segments.Add((segmentStart, segmentEnd - segmentStart));
                segmentStart = segmentEnd;
            }

            if (segments.Count == 0)
            {
                return segments;
            }

            // 最后一段从最后一个边界一直延伸到文件末尾
            if (fileLength - segmentStart >= MIN_SEGMENT_LENGTH)
            {
                segments.Add((segmentStart, fileLength - segmentStart));
            }

            return segments;
        }

        /// <summary>
        /// 扫描文件中所有 PAR1 魔数的位置。
        /// </summary>
        /// <param name="fileHandle">已打开的文件句柄</param>
        /// <param name="fileLength">文件长度</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>按出现顺序排列的魔数偏移</returns>
        /// <remarks>使用 RandomAccess 顺序读取，不改变文件位置，可按需并发或重入。</remarks>
        private static List<long> FindMagicPositions(SafeFileHandle fileHandle, long fileLength, CancellationToken cancellationToken)
        {
            List<long> positions = new();

            // 额外留出跨块匹配所需的字节，避免魔数正好落在两个读取块之间时被漏掉
            byte[] buffer = new byte[SCAN_BUFFER_SIZE + MAGIC_LENGTH - 1];
            long position = 0;
            int carryLength = 0;
            while (position < fileLength)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int read = RandomAccess.Read(fileHandle, buffer.AsSpan(carryLength, SCAN_BUFFER_SIZE), position);
                if (read <= 0)
                {
                    break;
                }

                int totalLength = carryLength + read;
                ReadOnlySpan<byte> block = buffer.AsSpan(0, totalLength);
                int searchStart = 0;
                while (true)
                {
                    // 用 IndexOf 借助运行时的向量化搜索；逐字节比较在 GB 级文件上慢一个数量级
                    int index = block[searchStart..].IndexOf(ParquetMagic);
                    if (index < 0)
                    {
                        break;
                    }

                    positions.Add(position - carryLength + searchStart + index);
                    searchStart += index + 1;
                }

                // 把末尾不足一个魔数的字节挪到缓冲区头部，与下一块一起匹配
                carryLength = MAGIC_LENGTH - 1;
                buffer.AsSpan(totalLength - carryLength, carryLength).CopyTo(buffer);
                position += read;
            }

            return positions;
        }
    }
}