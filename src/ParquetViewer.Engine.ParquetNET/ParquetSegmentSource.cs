using Microsoft.Win32.SafeHandles;
using Parquet;
using ParquetViewer.Engine.Exceptions;

namespace ParquetViewer.Engine.ParquetNET
{
    /// <summary>
    /// 管理"拼接式 parquet 文件"中的各个片段：统计片段行数，并按需打开与缓存片段 reader。
    /// </summary>
    /// <remarks>
    /// 拼接文件可能包含上万个片段，实测每个片段 reader 约占 170KB 托管内存，全部常驻会占用数百 MB，
    /// 因此这里只常驻第一个片段（引擎的 schema 与元数据来源），其余片段在读取到时才打开并做 LRU 缓存。
    /// 所有片段流共享同一个文件句柄（见 <see cref="ParquetSegmentStream"/>），不会额外占用文件句柄。
    /// </remarks>
    internal sealed class ParquetSegmentSource : IDisposable
    {
        /// <summary>同时缓存的片段 reader 数量上限</summary>
        private const int MAX_CACHED_READERS = 8;

        /// <summary>打开片段、统计行数时的并行度上限</summary>
        private const int MAX_DEGREE_OF_PARALLELISM = 8;


        private readonly SafeFileHandle _fileHandle;
        private readonly bool _useDateOnlyTypeForDates;
        private readonly (long Offset, long Length, long RowCount)[] _segments;
        private readonly Dictionary<int, ParquetReader> _readerCache = new();
        private readonly LinkedList<int> _readerUsageOrder = new();
        private readonly ParquetReader _defaultReader;
        private bool _isDisposed;

        private ParquetSegmentSource(SafeFileHandle fileHandle, bool useDateOnlyTypeForDates,
            (long Offset, long Length, long RowCount)[] segments, ParquetReader defaultReader)
        {
            _fileHandle = fileHandle;
            _useDateOnlyTypeForDates = useDateOnlyTypeForDates;
            _segments = segments;
            _defaultReader = defaultReader;

            long totalRowCount = 0;
            foreach ((long _, long _, long rowCount) in segments)
            {
                totalRowCount += rowCount;
            }

            TotalRowCount = totalRowCount;
        }

        /// <summary>片段总数</summary>
        public int SegmentCount => _segments.Length;

        /// <summary>所有片段的行数之和</summary>
        public long TotalRowCount { get; }

        /// <summary>第一个片段的 reader，提供引擎所需的 schema 与元数据</summary>
        public ParquetReader DefaultReader => _defaultReader;

        /// <summary>
        /// 打开拼接文件的片段集合。
        /// </summary>
        /// <param name="fileHandle">已打开的文件句柄，由本对象负责释放</param>
        /// <param name="useDateOnlyTypeForDates">日期列是否映射为 DateOnly，与引擎的解析选项保持一致</param>
        /// <param name="segments">按顺序排列的片段区间</param>
        /// <param name="defaultReader">第一个片段已打开的 reader</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>片段来源对象</returns>
        /// <remarks>
        /// 片段数据必须首尾衔接才能拼成完整的行序，任何一个片段读不出来都会导致行号错位，
        /// 因此这里不跳过任何异常片段，解析失败直接向上抛出。
        /// </remarks>
        public static async Task<ParquetSegmentSource> OpenAsync(SafeFileHandle fileHandle, bool useDateOnlyTypeForDates,
            List<(long Offset, long Length)> segments, ParquetReader defaultReader, CancellationToken cancellationToken)
        {
            long[] rowCounts = new long[segments.Count];
            rowCounts[0] = defaultReader.Metadata?.NumRows ?? 0;

            // 逐个片段读取 footer 统计行数。片段之间相互独立，且片段流基于文件句柄随机读取，因此可以并行处理。
            ParallelOptions parallelOptions = new()
            {
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, MAX_DEGREE_OF_PARALLELISM),
                CancellationToken = cancellationToken,
            };

            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, segments.Count), parallelOptions, async (index, token) =>
                {
                    if (index == 0)
                    {
                        return;
                    }

                    rowCounts[index] = await ReadSegmentRowCountAsync(fileHandle, segments[index], useDateOnlyTypeForDates, token);
                });
            }
            catch
            {
                // 统计行数失败时本对象还没构造完成，必须在这里释放调用方交进来的首个片段 reader，避免句柄与内存泄漏
                await defaultReader.DisposeAsync();
                throw;
            }

            (long Offset, long Length, long RowCount)[] segmentRows = new (long, long, long)[segments.Count];
            for (int i = 0; i < segments.Count; i++)
            {
                segmentRows[i] = (segments[i].Offset, segments[i].Length, rowCounts[i]);
            }

            return new ParquetSegmentSource(fileHandle, useDateOnlyTypeForDates, segmentRows, defaultReader);
        }

        /// <summary>
        /// 统计单个片段的行数：优先使用轻量 footer 解析，失败时回退到库的完整解析。
        /// </summary>
        /// <param name="fileHandle">底层文件句柄</param>
        /// <param name="segment">片段区间</param>
        /// <param name="useDateOnlyTypeForDates">日期列是否映射为 DateOnly，与引擎的解析选项保持一致</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>片段行数</returns>
        private static async Task<long> ReadSegmentRowCountAsync(SafeFileHandle fileHandle, (long Offset, long Length) segment,
            bool useDateOnlyTypeForDates, CancellationToken cancellationToken)
        {
            try
            {
                // 轻量解析只取 footer 中的行数，不必为每个片段构建完整的列 schema：
                // 数千个片段时，构建 schema 产生的临时对象是打开耗时的主要来源
                return ParquetFooterReader.ReadSegmentRowCount(fileHandle, segment.Offset, segment.Length);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 轻量解析遇到无法识别的 footer 结构时回退到库的完整解析，避免这项优化反而让文件打不开
                await using ParquetReader reader = await CreateSegmentReaderAsync(fileHandle, segment.Offset, segment.Length,
                    useDateOnlyTypeForDates, cancellationToken);
                return reader.Metadata?.NumRows ?? 0;
            }
        }

        /// <summary>
        /// 按文件内绝对行号获取需要读取的片段 reader 及其在片段内的起始偏移。
        /// </summary>
        /// <param name="offset">文件内绝对行号</param>
        /// <returns>片段内起始行号与对应 reader，按数据顺序排列</returns>
        public IEnumerable<(long RemainingOffset, ParquetReader Reader)> GetReaders(long offset)
        {
            long rowsBeforeSegment = 0;
            for (int i = 0; i < _segments.Length; i++)
            {
                long segmentRowCount = _segments[i].RowCount;
                if (offset >= rowsBeforeSegment + segmentRowCount)
                {
                    rowsBeforeSegment += segmentRowCount;
                    continue;
                }

                yield return (offset - rowsBeforeSegment, GetOrCreateReader(i));
                offset = 0;
                rowsBeforeSegment += segmentRowCount;
            }
        }

        /// <summary>
        /// 获取片段 reader，必要时打开并按 LRU 规则缓存。
        /// </summary>
        /// <param name="segmentIndex">片段下标</param>
        /// <returns>该片段的 reader</returns>
        /// <remarks>调用方是同步迭代器，无法 await，因此这里同步等待打开（实测单个片段约 1ms）。</remarks>
        private ParquetReader GetOrCreateReader(int segmentIndex)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (segmentIndex == 0)
            {
                return _defaultReader;
            }

            if (_readerCache.TryGetValue(segmentIndex, out ParquetReader? cachedReader))
            {
                TouchCacheEntry(segmentIndex);
                return cachedReader;
            }

            ParquetReader reader = CreateSegmentReaderAsync(_fileHandle, _segments[segmentIndex].Offset, _segments[segmentIndex].Length,
                    _useDateOnlyTypeForDates, CancellationToken.None)
                .GetAwaiter().GetResult();
            AddCacheEntry(segmentIndex, reader);
            return reader;
        }

        private void AddCacheEntry(int segmentIndex, ParquetReader reader)
        {
            if (_readerCache.Count >= MAX_CACHED_READERS)
            {
                EvictLeastRecentlyUsedReader();
            }

            _readerCache[segmentIndex] = reader;
            _readerUsageOrder.AddLast(segmentIndex);
        }

        private void TouchCacheEntry(int segmentIndex)
        {
            if (_readerUsageOrder.Remove(segmentIndex))
            {
                _readerUsageOrder.AddLast(segmentIndex);
            }
        }

        /// <summary>
        /// 释放最久未使用的片段 reader。
        /// </summary>
        /// <remarks>
        /// 一次读取过程中每个片段只会被取用一次，因此淘汰最久未使用的 reader 不会影响到正在读取的片段。
        /// </remarks>
        private void EvictLeastRecentlyUsedReader()
        {
            while (_readerUsageOrder.First is { } oldest)
            {
                _readerUsageOrder.RemoveFirst();
                if (_readerCache.Remove(oldest.Value, out ParquetReader? reader))
                {
                    DisposeReader(reader);
                    return;
                }
            }
        }

        /// <summary>
        /// 为指定片段区间打开一个 reader。
        /// </summary>
        /// <param name="fileHandle">底层文件句柄</param>
        /// <param name="offset">片段起始偏移</param>
        /// <param name="length">片段长度</param>
        /// <param name="useDateOnlyTypeForDates">日期列是否映射为 DateOnly，与引擎的解析选项保持一致</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>该片段的 reader</returns>
        public static Task<ParquetReader> CreateSegmentReaderAsync(SafeFileHandle fileHandle, long offset, long length,
            bool useDateOnlyTypeForDates, CancellationToken cancellationToken)
        {
            // 每个 reader 使用独立的 ParquetOptions 实例，避免多个 reader 共享同一个可变配置对象
            ParquetOptions options = new() { UseDateOnlyTypeForDates = useDateOnlyTypeForDates };
            ParquetSegmentStream stream = new(fileHandle, offset, length);
            return ParquetReader.CreateAsync(stream, options, false, cancellationToken);
        }

        private static void DisposeReader(ParquetReader reader)
        {
            try
            {
                // Parquet.Net 6.x 的 ParquetReader 仅实现 IAsyncDisposable，这里以同步方式完成异步释放
                reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                throw new ParquetEngineException("释放拼接文件片段 reader 失败", ex);
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            foreach (ParquetReader reader in _readerCache.Values)
            {
                DisposeReader(reader);
            }

            _readerCache.Clear();
            _readerUsageOrder.Clear();
            DisposeReader(_defaultReader);
            _fileHandle.Dispose();
        }
    }
}