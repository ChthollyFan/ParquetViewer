using Parquet;
using Parquet.Data;
using Parquet.Schema;
using ParquetViewer.Engine.Exceptions;
using ParquetViewer.Engine.ParquetNET.Types;
using ParquetViewer.Engine.Types;
using System.Collections;
using System.Data;

namespace ParquetViewer.Engine.ParquetNET
{
    public partial class ParquetEngine
    {
        /// <summary>按 row group 并行读取时引擎内部的并行度上限</summary>
        /// <remarks>实测 12 核机器上 8 线程以后加速比已饱和（磁盘与解码内存分配成为瓶颈），再增加线程只增内存峰值。</remarks>
        private const int MAX_READ_PARALLELISM = 8;

        /// <summary>预分配 DataTableLite 行容量时的上限</summary>
        /// <remarks>请求行数可能达到上亿，按请求量一次性预分配底层数组会直接吃掉数 GB 内存。</remarks>
        private const int MAX_PREFETCH_ROW_CAPACITY = 1_000_000;

        /// <summary>row group 内需要读取的行区间（相对该 row group 起始行）</summary>
        private readonly record struct RowSlice(long SkipRecords, long ReadRecords);

        /// <summary>一次读取请求在单个文件内的连续行区间（按 row group 边界切分）</summary>
        private readonly record struct ReadSegment(long Offset, long RecordCount);

        /// <summary>
        /// 批次进度上报：把逐单元格的进度回调合并为每 <see cref="BatchSize"/> 个上报一次。
        /// </summary>
        /// <remarks>进度条总量仍以单元格计，只是减少回调次数；使用方必须在结束时调用 <see cref="Flush"/> 上报余数。</remarks>
        private sealed class ProgressBatcher
        {
            private const int BatchSize = 1024;

            private readonly IProgress<int>? _progress;
            private int _pending;

            public ProgressBatcher(IProgress<int>? progress) => this._progress = progress;

            /// <summary>记录一个单元格的进度，累积到批次大小后统一上报</summary>
            public void ReportOne()
            {
                if (this._progress is null)
                {
                    return;
                }

                this._pending++;
                if (this._pending >= BatchSize)
                {
                    Flush();
                }
            }

            /// <summary>上报尚未提交的进度</summary>
            public void Flush()
            {
                if (this._progress is null || this._pending == 0)
                {
                    return;
                }

                this._progress.Report(this._pending);
                this._pending = 0;
            }
        }

        public async Task<Func<bool, DataTable>> ReadRowsAsync(List<string> selectedFields, int offset, int recordCount, CancellationToken cancellationToken, IProgress<int>? progress = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recordCount, nameof(recordCount));
            ArgumentOutOfRangeException.ThrowIfNegative(offset, nameof(offset));

            // 行容量按请求量预分配，但设上限，避免超大请求直接分配数 GB 的底层数组
            DataTableLite result = BuildDataTable(null, selectedFields, Math.Min(recordCount, Math.Min((int)this.RecordCount, MAX_PREFETCH_ROW_CAPACITY)));

            // 单文件且请求跨越多个 row group 时按 row group 分片并行读取，分片结果再按顺序合并
            List<ReadSegment>? parallelSegments = TryBuildParallelSegments(offset, recordCount);
            if (parallelSegments is not null)
            {
                await ReadSegmentsInParallelAsync(result, selectedFields, parallelSegments, cancellationToken, progress);
            }
            else
            {
                long recordsLeftToRead = recordCount;
                foreach (var reader in this.GetReaders(offset))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (recordsLeftToRead <= 0)
                        break;

                    recordsLeftToRead = await PopulateDataTable(result, reader.ParquetReader, reader.RemainingOffset, recordsLeftToRead, cancellationToken, progress);
                }
            }

            result.DataSetSize = this.RecordCount;

            return (logProgress) =>
            {
                var datatable = result.ToDataTable(cancellationToken, logProgress ? progress : null);
                return datatable;
            };
        }

        private async Task<long> PopulateDataTable(DataTableLite dataTable, ParquetReader parquetReader,
            long offset, long recordCount, CancellationToken cancellationToken, IProgress<int>? progress)
        {
            //Read column by column to generate each row in the datatable
            int totalRecordCountSoFar = 0;
            long rowsLeftToRead = recordCount;
            for (int i = 0; i < parquetReader.RowGroupCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using (ParquetRowGroupReader groupReader = parquetReader.OpenRowGroupReader(i))
                {
                    if (groupReader.RowCount > int.MaxValue)
                        throw new ArgumentOutOfRangeException(
                            string.Format("Cannot handle row group sizes greater than {0}. Found {1} instead.", int.MaxValue, groupReader.RowCount));

                    int rowsPassedUntilThisRowGroup = totalRecordCountSoFar;
                    totalRecordCountSoFar += (int)groupReader.RowCount;

                    if (offset >= totalRecordCountSoFar)
                        continue;

                    if (rowsLeftToRead <= 0)
                        break;

                    long numberOfRecordsToReadFromThisRowGroup = Math.Min(Math.Min(totalRecordCountSoFar - offset, rowsLeftToRead), groupReader.RowCount);
                    rowsLeftToRead -= numberOfRecordsToReadFromThisRowGroup;

                    long recordsToSkipInThisRowGroup = Math.Max(offset - rowsPassedUntilThisRowGroup, 0);

                    await ProcessRowGroup(dataTable, groupReader, recordsToSkipInThisRowGroup, numberOfRecordsToReadFromThisRowGroup, cancellationToken, progress);
                }
            }

            return rowsLeftToRead;
        }

        /// <summary>
        /// 计算本次请求的并行读取分片；不满足并行条件时返回 null，调用方回退到原有串行路径。
        /// </summary>
        /// <param name="offset">请求起始行（文件内绝对行号）</param>
        /// <param name="recordCount">请求行数</param>
        /// <returns>按 row group 边界切分的连续行区间，或 null 表示不分片</returns>
        /// <remarks>
        /// 并行分片需要为每个分片打开独立 reader，因此只对单文件场景启用，多文件仍走原有的跨文件串行逻辑。
        /// 分片边界对齐到 row group，避免同一个 row group 被两个分片重复解码。
        /// </remarks>
        private List<ReadSegment>? TryBuildParallelSegments(long offset, int recordCount)
        {
            if (this._parquetFiles.Length != 1)
            {
                return null;
            }

            int maxParallelism = ResolveMaxReadParallelism();
            if (maxParallelism <= 1)
            {
                return null;
            }

            long end = Math.Min(offset + recordCount, this.RecordCount);
            if (offset >= end)
            {
                return null;
            }

            // 分片边界按 thrift 元数据的行组行数计算，而实际读取用的是运行时的 row group 行数，
            // 两者不一致（损坏或异常文件）时分片会错位，这种情况下直接回退到串行读取
            long totalRowGroupRows = 0;
            foreach (Parquet.Meta.RowGroup rowGroup in this._thriftMetadata.RowGroups)
            {
                totalRowGroupRows += rowGroup.NumRows;
            }

            if (totalRowGroupRows != this.RecordCount)
            {
                return null;
            }

            List<ReadSegment> rowGroupSegments = new();
            long rowGroupStart = 0;
            foreach (Parquet.Meta.RowGroup rowGroup in this._thriftMetadata.RowGroups)
            {
                long rowGroupEnd = rowGroupStart + rowGroup.NumRows;
                long segmentStart = Math.Max(rowGroupStart, offset);
                long segmentEnd = Math.Min(rowGroupEnd, end);
                if (segmentEnd > segmentStart)
                {
                    rowGroupSegments.Add(new ReadSegment(segmentStart, segmentEnd - segmentStart));
                }

                rowGroupStart = rowGroupEnd;
                if (rowGroupStart >= end)
                {
                    break;
                }
            }

            if (rowGroupSegments.Count < 2)
            {
                // 只覆盖一个 row group 时无法拆分，并行读同一组只会重复解码
                return null;
            }

            return MergeSegments(rowGroupSegments, Math.Min(maxParallelism, rowGroupSegments.Count));
        }

        /// <summary>
        /// 把相邻的 row group 区间合并成行数尽量均衡的若干分片。
        /// </summary>
        /// <param name="segments">按行号升序排列的 row group 区间</param>
        /// <param name="shardCount">目标分片数</param>
        /// <returns>合并后的分片，保持原有行序</returns>
        private static List<ReadSegment> MergeSegments(List<ReadSegment> segments, int shardCount)
        {
            long totalRows = 0;
            foreach (ReadSegment segment in segments)
            {
                totalRows += segment.RecordCount;
            }

            long targetRowsPerShard = (totalRows + shardCount - 1) / shardCount;
            List<ReadSegment> shards = new(shardCount);
            int index = 0;
            for (int shard = 0; shard < shardCount && index < segments.Count; shard++)
            {
                bool isLastShard = shard == shardCount - 1;
                long shardStart = segments[index].Offset;
                long shardRows = 0;

                // 最后一个分片直接接收剩余区间，避免向上取整导致前面分片偏大
                // 每个分片至少取一个 row group 区间，否则并行度会超过实际可分片的数量
                while (index < segments.Count && (isLastShard || shardRows == 0 || shardRows < targetRowsPerShard))
                {
                    shardRows += segments[index].RecordCount;
                    index++;
                }

                shards.Add(new ReadSegment(shardStart, shardRows));
            }

            return shards;
        }

        /// <summary>
        /// 解析当前生效的并行度。
        /// </summary>
        /// <returns>返回值大于 1 表示可以并行</returns>
        private static int ResolveMaxReadParallelism()
        {
            int configured = ParquetEngineSettings.MaxReadParallelism;
            if (configured == 1)
            {
                return 1;
            }

            int auto = Math.Min(Environment.ProcessorCount, MAX_READ_PARALLELISM);
            return configured <= 1 ? auto : Math.Min(configured, auto);
        }

        /// <summary>
        /// 并行读取各分片，并把结果按分片顺序合并进汇总表。
        /// </summary>
        /// <param name="result">汇总结果表（调用时还没有任何行）</param>
        /// <param name="selectedFields">选中的字段</param>
        /// <param name="segments">已按行号升序排列的分片</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <param name="progress">进度回调</param>
        /// <remarks>
        /// 每个分片持有独立的 ParquetReader：ParquetRowGroupReader 共享底层流并依赖流位置，
        /// 多线程并发读取同一个 reader 会互相破坏读位置，所以不能直接把现有循环改成 Parallel.For。
        /// </remarks>
        private async Task ReadSegmentsInParallelAsync(DataTableLite result, List<string> selectedFields, List<ReadSegment> segments,
            CancellationToken cancellationToken, IProgress<int>? progress)
        {
            // 列定义在串行阶段先克隆好，避免多个分片并发初始化 schema 元数据
            DataTableLite shardTemplate = result.Clone();

            ParquetReader[] readers = new ParquetReader[segments.Count];
            bool[] ownsReader = new bool[segments.Count];
            try
            {
                // 第一个分片复用引擎自身的 reader，其余分片并行打开，省掉串行等待 footer 解析的时间
                readers[0] = this._defaultReader;

                Task<ParquetReader>[] openTasks = new Task<ParquetReader>[segments.Count - 1];
                for (int i = 0; i < openTasks.Length; i++)
                {
                    openTasks[i] = this.OpenAdditionalReaderAsync(cancellationToken);
                }

                try
                {
                    ParquetReader[] additionalReaders = await Task.WhenAll(openTasks);
                    for (int i = 0; i < additionalReaders.Length; i++)
                    {
                        readers[i + 1] = additionalReaders[i];
                        ownsReader[i + 1] = true;
                    }
                }
                catch
                {
                    // 打开失败时清理已经成功打开的 reader，避免文件句柄泄漏
                    foreach (Task<ParquetReader> openTask in openTasks)
                    {
                        if (openTask.Status == TaskStatus.RanToCompletion)
                        {
                            await openTask.Result.DisposeAsync();
                        }
                    }

                    throw;
                }

                Task<DataTableLite>[] readTasks = new Task<DataTableLite>[segments.Count];
                for (int i = 0; i < segments.Count; i++)
                {
                    ParquetReader shardReader = readers[i];
                    ReadSegment segment = segments[i];
                    readTasks[i] = Task.Run(() => ReadSegmentAsync(selectedFields, shardTemplate, shardReader, segment, cancellationToken, progress));
                }

                // Task.WhenAll 只有在全部分片结束后才会抛出异常，因此异常路径下释放 reader 也是安全的
                DataTableLite[] shards = await Task.WhenAll(readTasks);

                // 按分片顺序合并，保证行序与串行读取完全一致
                foreach (DataTableLite shard in shards)
                {
                    result.AppendRowsFrom(shard);
                }
            }
            finally
            {
                for (int i = 0; i < readers.Length; i++)
                {
                    if (!ownsReader[i])
                    {
                        continue;
                    }

                    try
                    {
                        await readers[i].DisposeAsync();
                    }
                    catch
                    {
                        // 释放失败不影响已读取的数据，吞掉以保证其余 reader 也能被释放
                    }
                }
            }
        }

        /// <summary>
        /// 读取单个分片，返回只包含该分片行的独立数据表。
        /// </summary>
        /// <param name="selectedFields">选中的字段</param>
        /// <param name="shardTemplate">列定义模板（只有列、没有行）</param>
        /// <param name="reader">该分片独占的 reader</param>
        /// <param name="segment">该分片覆盖的行区间</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <param name="progress">进度回调</param>
        /// <returns>该分片的数据表</returns>
        /// <remarks>各分片写入各自的表，避免多线程并发写入同一张表。</remarks>
        private async Task<DataTableLite> ReadSegmentAsync(List<string> selectedFields, DataTableLite shardTemplate, ParquetReader reader,
            ReadSegment segment, CancellationToken cancellationToken, IProgress<int>? progress)
        {
            DataTableLite shard = shardTemplate.Clone();
            shard.EnsureCapacity((int)Math.Min(segment.RecordCount, MAX_PREFETCH_ROW_CAPACITY));
            await PopulateDataTable(shard, reader, segment.Offset, segment.RecordCount, cancellationToken, progress);
            return shard;
        }

        private async Task ProcessRowGroup(DataTableLite dataTable, ParquetRowGroupReader groupReader,
            long skipRecords, long readRecords, CancellationToken cancellationToken, IProgress<int>? progress)
        {
            int rowBeginIndex = dataTable.Rows.Count;
            bool isFirstColumn = true;

            foreach (DataTableLite.ColumnLite column in dataTable.Columns.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var field = column.ParentSchema.Children.FirstOrDefault(c => c.Path == column.Name) as ParquetSchemaElement;
                switch (field?.FieldType)
                {
                    case FieldTypeId.Primitive:
                        await ReadPrimitiveField(dataTable, groupReader, rowBeginIndex, field, skipRecords,
                            readRecords, isFirstColumn, cancellationToken, progress);
                        break;
                    case FieldTypeId.List:
                        var listField = field.GetListField();
                        var itemField = listField.GetListItemField();
                        var fieldIndex = dataTable.Columns[field.Path]!.Ordinal;
                        await ReadListField(dataTable, groupReader, rowBeginIndex, itemField, fieldIndex,
                            skipRecords, readRecords, isFirstColumn, cancellationToken, progress);
                        break;
                    case FieldTypeId.Map:
                        await ReadMapField(dataTable, groupReader, rowBeginIndex, field, skipRecords,
                            readRecords, isFirstColumn, cancellationToken, progress);
                        break;
                    case FieldTypeId.Struct:
                        await ReadStructField(dataTable, groupReader, rowBeginIndex, field, skipRecords,
                            readRecords, isFirstColumn, cancellationToken, progress);
                        break;
                    default:
                        throw new InvalidDataException($"`{column.Name}`");
                }

                isFirstColumn = false;
            }
        }

        private async Task ReadPrimitiveField(DataTableLite dataTable, ParquetRowGroupReader groupReader, int rowBeginIndex, ParquetSchemaElement field,
            long skipRecords, long readRecords, bool isFirstColumn, CancellationToken cancellationToken, IProgress<int>? progress)
        {
            var rowIndex = rowBeginIndex;
            var fieldIndex = dataTable.Columns[field.Path]?.Ordinal ?? throw new ParquetEngineException($"Column `{field.Path}` is missing");

            if (field.BelongsToListField || field.BelongsToListOfStructsField || field.DataField?.IsArray == true)
            {
                await ReadListField(dataTable, groupReader, rowBeginIndex, field, fieldIndex, skipRecords, readRecords, isFirstColumn, cancellationToken, progress);
            }
            else
            {
                // 行对齐的普通列：把 skip/read 区间下推到列读取阶段，只转换（装箱）真正要返回的行。
                // 之前会先把整个 row group 装箱成 object[] 再逐值丢弃，翻页只取少量行时浪费极大。
                var dataColumn = await ReadColumnAsync(groupReader, field, cancellationToken, new RowSlice(skipRecords, readRecords));
                var dataEnumerable = dataColumn.GetDataWithPaddedNulls(field);

                var fieldType = dataTable.Columns[field.Path].Type;
                var progressBatcher = new ProgressBatcher(progress);

                foreach (var value in dataEnumerable)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (rowIndex - rowBeginIndex >= readRecords)
                        break;

                    if (isFirstColumn)
                    {
                        dataTable.NewRow();
                    }

                    if (value == DBNull.Value || value is null)
                    {
                        dataTable.Rows[rowIndex]![fieldIndex] = DBNull.Value;
                    }
                    else if (fieldType == typeof(ByteArrayValue))
                    {
                        dataTable.Rows[rowIndex]![fieldIndex] = new ByteArrayValue((byte[])value);
                    }
                    else
                    {
                        dataTable.Rows[rowIndex]![fieldIndex] = value;
                    }

                    rowIndex++;
                    progressBatcher.ReportOne();
                }

                // 上报不足一批的余数，否则进度条到不了 100%
                progressBatcher.Flush();
            }
        }

        private async Task ReadListField(DataTableLite dataTable, ParquetRowGroupReader groupReader, int rowBeginIndex, ParquetSchemaElement itemField, int fieldIndex,
            long skipRecords, long readRecords, bool isFirstColumn, CancellationToken cancellationToken, IProgress<int>? progress)
        {
            var lastMilestone = "Start";
            try
            {
                if (itemField.FieldType == FieldTypeId.List)
                {
                    var nestedListField = itemField.GetListField();
                    var nestedItemField = nestedListField.GetListItemField();
                    lastMilestone = "Read";

                    await ReadListField(dataTable, groupReader, rowBeginIndex, nestedItemField, fieldIndex: 0,
                        skipRecords, readRecords, isFirstColumn, cancellationToken, progress);
                }
                else if (itemField.FieldType == FieldTypeId.Primitive)
                {
                    int rowIndex = rowBeginIndex;

                    var dataColumn = await ReadColumnAsync(groupReader, itemField, cancellationToken);
                    lastMilestone = "Read";

                    var dataEnumerable = dataColumn.GetDataWithPaddedNulls(itemField);

                    var numberOfListParents = itemField.NumberOfListParents;
                    #region Fixes TWO_TIER_LIST_TYPE_TEST
                    numberOfListParents = numberOfListParents == 0 ? 1 : numberOfListParents;
                    #endregion

                    var listValueBuilder = new ListValueBuilder(dataColumn.RepetitionLevels!, dataColumn.DefinitionLevels!, dataEnumerable, itemField.ClrType);
                    var listValues = listValueBuilder.ReadRows((int)skipRecords, (int)readRecords, numberOfListParents,
                        itemField.CurrentDefinitionLevel, itemField.DataField?.MaxDefinitionLevel ?? 0, cancellationToken);
                    lastMilestone = "ReadRows";

                    var progressBatcher = new ProgressBatcher(progress);
                    foreach (var listValue in listValues)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (isFirstColumn)
                        {
                            dataTable.NewRow();
                        }

                        dataTable.Rows[rowIndex][fieldIndex] = listValue;
                        rowIndex++;
                        progressBatcher.ReportOne();
                    }

                    // 上报不足一批的余数
                    progressBatcher.Flush();
                }
                else if (itemField.FieldType == FieldTypeId.Struct)
                {
                    //Read struct data as a new datatable
                    DataTableLite structFieldTable = BuildDataTable(itemField, itemField.Children.Select(f => f.Path).ToList(), (int)readRecords);

                    //Need to calculate progress differently for structs
                    var structFieldReadProgress = StructReadProgress(progress, structFieldTable.Columns.Count);

                    //Read the struct data and populate the datatable
                    await ProcessRowGroup(structFieldTable, groupReader, skipRecords, readRecords, cancellationToken, structFieldReadProgress);
                    lastMilestone = "Processed";

                    //We need to pivot the data into a new data table (because we read it in columnar fashion above)
                    int rowIndex = rowBeginIndex;
                    foreach (var values in structFieldTable.Rows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        DataTableLite PivotTable(object[] valueArray, DataTableLite? newStructFieldTableOverride = null)
                        {
                            var newStructFieldTable = newStructFieldTableOverride ?? BuildDataTable(itemField, itemField.Children.Select(f => f.Path).ToList(), (int)readRecords);
                            for (var columnOrdinal = 0; columnOrdinal < valueArray.Length; columnOrdinal++)
                            {
                                lastMilestone = $"#{rowIndex}-{columnOrdinal}";
                                if (valueArray[columnOrdinal] == DBNull.Value)
                                {
                                    //Empty array
                                    continue;
                                }

                                var columnValues = (ListValue)valueArray[columnOrdinal];

                                if (columnValues.Data.Count == 0 && columnOrdinal != 0) //All values are null
                                {
                                    for (var i = 0; i < newStructFieldTable.Rows.Count; i++)
                                    {
                                        newStructFieldTable.Rows[i][columnOrdinal] = DBNull.Value;
                                    }

                                    continue;
                                }

                                for (var rowValueIndex = 0; rowValueIndex < columnValues.Data.Count; rowValueIndex++)
                                {
                                    lastMilestone = $"#{rowIndex}-{columnOrdinal}-{rowValueIndex}";

                                    var columnValue = columnValues.Data[rowValueIndex] ?? throw new SystemException("Column value missing during pivot");
                                    #region Hack for LIST_OF_STRUCT_OF_LIST_OF_STRUCT test
                                    if (columnValue is StructValueExt structValue && structValue.IsList)
                                    {
                                        //We need to convert `columnValue` from struct to a list of structs as it was a nested structure
                                        var areTypesAsExpected = newStructFieldTable.Columns.Values.ElementAt(columnOrdinal).Type == typeof(ListValue);
                                        if (!areTypesAsExpected)
                                        {
                                            throw new UnsupportedFieldException("Failed to pivot list of structs.");
                                        }

                                        if (structValue.Data is not DataRowLite dataRowLite)
                                        {
                                            throw new InvalidDataException("Struct data wasn't the expected type.");
                                        }

                                        var nestedStructFieldTable = PivotTable(structValue.Data.Row, dataRowLite.Table.Clone());
                                        var listValues = new ArrayList(nestedStructFieldTable.Rows.Count);
                                        for (var i = 0; i < nestedStructFieldTable.Rows.Count; i++)
                                        {
                                            var row = nestedStructFieldTable.GetRowAt(i);
                                            listValues.Add(new StructValueExt(row));
                                        }
                                        columnValue = new ListValue(listValues, typeof(StructValueExt));
                                    }
                                    #endregion

                                    bool isFirstValueColumn = columnOrdinal == 0;
                                    if (isFirstValueColumn)
                                    {
                                        newStructFieldTable.NewRow();
                                    }
                                    newStructFieldTable.Rows[rowValueIndex][columnOrdinal] = columnValue;
                                }
                            }
                            return newStructFieldTable;
                        }

                        ArrayList GetListOfStructs(object[] _values)
                        {
                            DataTableLite newStructFieldTable = PivotTable(_values);

                            var listValues = new ArrayList(newStructFieldTable.Rows.Count);
                            for (var i = 0; i < newStructFieldTable.Rows.Count; i++)
                            {
                                var dataRow = newStructFieldTable.GetRowAt(i);

                                //If all the fields of the struct are null, we assume the struct itself is null
                                if (dataRow.Row.All(value => value == DBNull.Value))
                                {
                                    listValues.Add(DBNull.Value);
                                }
                                else
                                {
                                    listValues.Add(new StructValueExt(dataRow) { IsList = itemField.NumberOfListParents > 1 });
                                }
                            }
                            return listValues;
                        }

                        var listValues = GetListOfStructs(values);

                        if (isFirstColumn)
                            dataTable.NewRow();

                        dataTable.Rows[rowIndex][fieldIndex] = new ListValue(listValues, typeof(StructValueExt));
                        rowIndex++;
                    }
                }
                else
                {
                    throw new NotSupportedException($"Lists of {itemField.FieldType}s are not currently supported");
                }
            }
            catch (Exception ex)
            {
                ex.Data["last_milestone"] = lastMilestone;
                throw;
            }
        }

        private static async Task ReadMapField(DataTableLite dataTable, ParquetRowGroupReader groupReader, int rowBeginIndex, ParquetSchemaElement field,
            long skipRecords, long readRecords, bool isFirstColumn, CancellationToken cancellationToken, IProgress<int>? progress)
        {
            var keyValueField = field.GetMapKeyValueField();
            var keyField = keyValueField.GetMapKeyField();
            var valueField = keyValueField.GetMapValueField();

            if (keyField.Children.Any() || valueField.Children.Any())
                throw new UnsupportedFieldException($"Cannot load field `{field.Path}`. Nested Map types are not supported");

            int rowIndex = rowBeginIndex;

            int skippedRecords = 0;
            var keyDataColumn = await ReadColumnAsync(groupReader, keyField, cancellationToken);
            var valueDataColumn = await ReadColumnAsync(groupReader, valueField, cancellationToken);

            var keyDataEnumerable = keyDataColumn.GetDataWithPaddedNulls(keyField);
            var valueDataEnumerable = valueDataColumn.GetDataWithPaddedNulls(valueField);

            var dataEnumerable = Engine.Helpers.PairEnumerables(keyDataEnumerable, valueDataEnumerable, DBNull.Value);

            var levelCount = Math.Max(keyDataColumn.RepetitionLevels?.Length ?? 0, valueDataColumn.RepetitionLevels?.Length ?? 0);
            var fieldIndex = dataTable.Columns[field.Path]!.Ordinal;
            var progressBatcher = new ProgressBatcher(progress);
            ArrayList? mapKeys = null;
            ArrayList? mapValues = null;
            int index = -1;
            foreach (var (key, value) in dataEnumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;

                bool IsEndOfRow() => (index + 1) == levelCount
                    || GetRepetitionLevel(index + 1) == 0; //0 means new map

                //Skip rows
                if (skipRecords > skippedRecords)
                {
                    if (IsEndOfRow())
                        skippedRecords++;

                    continue;
                }

                mapKeys ??= [];
                mapValues ??= [];
                if (IsEndOfRow())
                {
                    if (isFirstColumn)
                    {
                        dataTable.NewRow();
                    }

                    mapKeys.Add(key);
                    mapValues.Add(value);

                    if (keyDataColumn.IsEmpty(index, keyField) || valueDataColumn.IsEmpty(index, valueField))
                        dataTable.Rows[rowIndex]![fieldIndex] = new MapValue([], keyField.ClrType, [], valueField.ClrType);
                    else if (keyDataColumn.IsNull(index, keyField) || valueDataColumn.IsNull(index, valueField))
                        dataTable.Rows[rowIndex]![fieldIndex] = DBNull.Value;
                    else
                        dataTable.Rows[rowIndex]![fieldIndex] = new MapValue(mapKeys, keyField.ClrType, mapValues, valueField.ClrType);

                    mapKeys = null;
                    mapValues = null;

                    rowIndex++;
                    progressBatcher.ReportOne();

                    if (rowIndex - rowBeginIndex >= readRecords)
                        break;
                }
                else
                {
                    mapKeys.Add(key);
                    mapValues.Add(value);
                }

                int GetRepetitionLevel(int dataIndex)
                {
                    if (keyDataColumn.RepetitionLevels is null && valueDataColumn.RepetitionLevels is null)
                        return 0; // assume each entry is a new row since we have no repetition levels
                    else if (keyDataColumn.RepetitionLevels?.Length > dataIndex)
                        return keyDataColumn.RepetitionLevels[dataIndex];
                    else if (valueDataColumn.RepetitionLevels?.Length > dataIndex)
                        return valueDataColumn.RepetitionLevels[dataIndex];
                    else
                        throw new ArgumentOutOfRangeException(nameof(dataIndex));
                }
            }

            // 上报不足一批的余数
            progressBatcher.Flush();
        }

        private async Task ReadStructField(DataTableLite dataTable, ParquetRowGroupReader groupReader, int rowBeginIndex, ParquetSchemaElement field,
           long skipRecords, long readRecords, bool isFirstColumn, CancellationToken cancellationToken, IProgress<int>? progress)
        {
            //Read struct data as a new datatable
            DataTableLite structFieldTable = BuildDataTable(field, field.Children.Select(f => f.Path).ToList(), 1);

            //Need to calculate progress differently for structs
            var structFieldReadProgress = StructReadProgress(progress, structFieldTable.Columns.Count);

            //Read the struct data and populate the datatable
            await ProcessRowGroup(structFieldTable, groupReader, skipRecords, readRecords, cancellationToken, structFieldReadProgress);

            var rowIndex = rowBeginIndex;
            var fieldIndex = dataTable.Columns[field.Path]?.Ordinal ?? throw new ParquetEngineException($"Column `{field.Path}` is missing");
            for (var i = 0; i < structFieldTable.Rows.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (isFirstColumn)
                {
                    dataTable.NewRow();
                }

                //If all the fields of the struct are null, we assume the struct itself is null
                bool isNull = !structFieldTable.Rows[i].Any(item => item != DBNull.Value);

                if (isNull)
                {
                    dataTable.Rows[rowIndex]![fieldIndex] = DBNull.Value;
                }
                else
                {
                    var dataRow = structFieldTable.GetRowAt(i);
                    dataTable.Rows[rowIndex]![fieldIndex] = new StructValueExt(dataRow);
                }
                rowIndex++;
            }
        }

        private SimpleProgress StructReadProgress(IProgress<int>? _progress, int fieldCount)
        {
            var progress = new SimpleProgress();

            if (fieldCount <= 0)
            {
                //If the struct field has no columns, then each read is one row.
                var lastTotalSoFar = 0;
                progress.ProgressChanged += (int totalSoFar) =>
                {
                    var delta = totalSoFar - lastTotalSoFar;
                    lastTotalSoFar = totalSoFar;
                    _progress?.Report(delta);
                };
                return progress;
            }

            // 底层读取现在按批上报（一次可能前进多个单元格），这里按增量折算成整行后再上报。
            // 之前用“累计值是否为字段数整数倍”判断，批次上报后累计值可能永远落不到整数倍上，会导致 struct 进度卡住。
            var lastReportedTotal = 0;
            var pendingCells = 0;
            progress.ProgressChanged += (int totalSoFar) =>
            {
                pendingCells += totalSoFar - lastReportedTotal;
                lastReportedTotal = totalSoFar;

                //To report progress accurately we'll need to divide the progress total
                //by the field count to convert it to row count in the main data table.
                var completedRows = pendingCells / fieldCount;
                if (completedRows > 0)
                {
                    pendingCells -= completedRows * fieldCount;
                    _progress?.Report(completedRows);
                }
            };
            return progress;
        }

        private DataTableLite BuildDataTable(ParquetSchemaElement? parent, List<string> fields, int expectedRecordCount)
        {
            parent ??= (ParquetSchemaElement)this.Metadata.SchemaTree;
            DataTableLite dataTable = new(expectedRecordCount);
            foreach (var field in fields)
            {
                var schema = parent.GetChild(field);
                if (schema.FieldType == FieldTypeId.List
                    || schema.DataField?.IsArray == true)
                {
                    dataTable.AddColumn(field, typeof(ListValue), parent);
                }
                else if (schema.FieldType == FieldTypeId.Map)
                {
                    dataTable.AddColumn(field, typeof(MapValue), parent);
                }
                else if (schema.FieldType == FieldTypeId.Struct)
                {
                    dataTable.AddColumn(field, typeof(StructValueExt), parent);
                }
                //BYTE_ARRAY 与 FIXED_LEN_BYTE_ARRAY(即 fixed_size_binary[x]) 在无逻辑类型注解时都按原始字节数组展示
                else if ((schema.SchemaElement.Type == Parquet.Meta.Type.BYTE_ARRAY
                    || schema.SchemaElement.Type == Parquet.Meta.Type.FIXED_LEN_BYTE_ARRAY)
                    && schema.SchemaElement.LogicalType is null
                    && schema.SchemaElement.ConvertedType is null)
                {
                    dataTable.AddColumn(field, typeof(ByteArrayValue), parent);
                }
                else if (schema.DataField is DateTimeDataField dateField)
                {
                    if (dateField.DateTimeFormat == DateTimeFormat.Date)
                    {
                        dataTable.AddColumn(field, typeof(DateOnly), parent);
                    }
                    else
                    {
                        dataTable.AddColumn(field, typeof(DateTime), parent);
                    }
                }
                else
                {
                    var clrType = schema.ClrType ?? throw new MalformedFieldException($"`{(parent is not null ? parent + "/" : string.Empty)}/{field}` has no data field");
                    dataTable.AddColumn(field, clrType, parent);
                }
            }
            return dataTable;
        }

        /// <summary>
        /// 读取一个 row group 内的列数据。
        /// </summary>
        /// <param name="groupReader">当前 row group 读取器</param>
        /// <param name="field">列对应的字段</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <param name="slice">需要返回的行区间；null 表示返回整个 row group（列表/映射/结构列必须传 null，它们的层级数组要与完整行对齐）</param>
        /// <returns>列数据视图；指定 slice 时 Data 与层级数组只包含该区间</returns>
        private static async Task<RawColumnDataView> ReadColumnAsync(ParquetRowGroupReader groupReader, ParquetSchemaElement field,
            CancellationToken cancellationToken, RowSlice? slice = null)
        {
            try
            {
                // Parquet.Net 6.x 用 ReadRawColumnDataBaseAsync 取代 ReadColumnAsync，返回泛型 RawColumnData<T>
                var dataField = field.DataField ?? throw new MalformedFieldException($"Field `{field.PathWithParent}` has no data field");
                var rawColumnData = await groupReader.ReadRawColumnDataBaseAsync(dataField, cancellationToken);
                return await ConvertRawColumnDataView(groupReader, dataField, rawColumnData, field, slice, cancellationToken);
            }
            catch (OverflowException ex)
            {
                var isDecimalField = field.SchemaElement?.ConvertedType == Parquet.Meta.ConvertedType.DECIMAL
                    || field.SchemaElement?.LogicalType?.DECIMAL is not null;
                if (isDecimalField)
                {
                    var scale = field.SchemaElement!.Scale ?? 0;
                    var precision = field.SchemaElement.Precision ?? 0;
                    if (scale > DecimalOverflowException.MAX_DECIMAL_SCALE
                        || precision > DecimalOverflowException.MAX_DECIMAL_PRECISION)
                    {
                        throw new DecimalOverflowException(field.PathWithParent, precision, scale, ex);
                    }
                }

                throw;
            }
            catch (ParquetException ex)
            {
                var maskedExMessage = ex.Message.Replace($"'{field.Path}'", $"`{field.Path}`");
                throw new ParquetEngineException(maskedExMessage, ex);
            }
        }

        /// <summary>
        /// 将 Parquet.Net 6.x 的泛型 RawColumnData<T> 转为引擎内部使用的 RawColumnDataView，
        /// 便于后续按行展开（GetDataWithPaddedNulls 等）时保持既有逻辑。
        /// </summary>
        /// <param name="groupReader">当前行组读取器，含 null 列需要二次读取物理值流</param>
        /// <param name="dataField">列对应的 DataField</param>
        /// <param name="rawColumnData">库返回的原始列数据</param>
        /// <param name="field">字段定义</param>
        /// <param name="slice">需要返回的行区间；null 表示整个 row group</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>统一视图，Data 为每行值的 object 数组</returns>
        private static async Task<RawColumnDataView> ConvertRawColumnDataView(ParquetRowGroupReader groupReader, DataField dataField, RawColumnData rawColumnData, ParquetSchemaElement field, RowSlice? slice, CancellationToken cancellationToken)
        {
            // 若 CLR 类型为 Nullable<T>，库返回的 RawColumnData<T> 以非空 T 为泛型参数
            var actualType = Nullable.GetUnderlyingType(dataField.ClrType) ?? dataField.ClrType;
            // 6.x 的 TIME 列以 Int64 表达原始时间值，TimeDataField.Precision 用于还原 TimeOnly
            var timePrecision = (field.DataField as Parquet.Schema.TimeDataField)?.Precision;
            var method = typeof(ParquetEngine).GetMethod(nameof(ConvertRawColumnDataViewGeneric),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(actualType);
            try
            {
                return await (Task<RawColumnDataView>)method.Invoke(null, new object?[] { groupReader, dataField, rawColumnData, timePrecision, slice, cancellationToken })!;
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                // 反射调用会把泛型方法内部异常包装为 TargetInvocationException，解包后按原异常类型上抛
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private static async Task<RawColumnDataView> ConvertRawColumnDataViewGeneric<T>(ParquetRowGroupReader groupReader, DataField dataField, RawColumnData rawColumnData, Parquet.Schema.TimeUnitPrecision? timePrecision, RowSlice? slice, CancellationToken cancellationToken) where T : struct
        {
            var typed = (RawColumnData<T>)rawColumnData;

            // 列不含 repetition/definition levels 时库会在访问时抛异常，捕获后置 null（与旧 DataColumn 行为一致）
            int[]? repetitionLevels = null;
            try
            {
                if (!typed.RepetitionLevels.IsEmpty)
                    repetitionLevels = typed.RepetitionLevels.ToArray();
            }
            catch (InvalidOperationException)
            {
                repetitionLevels = null;
            }

            int[]? definitionLevels = null;
            try
            {
                if (!typed.DefinitionLevels.IsEmpty)
                    definitionLevels = typed.DefinitionLevels.ToArray();
            }
            catch (InvalidOperationException)
            {
                definitionLevels = null;
            }

            // 用显式循环代替 LINQ 判定是否存在 null：这里要扫过整个 row group 的层级数组，LINQ 的委托开销在百万级行数上不可忽略
            var hasNulls = false;
            if (definitionLevels is not null)
            {
                for (var i = 0; i < definitionLevels.Length; i++)
                {
                    if (definitionLevels[i] < dataField.MaxDefinitionLevel)
                    {
                        hasNulls = true;
                        break;
                    }
                }
            }

            var positionCount = definitionLevels?.Length ?? typed.Values.Length;

            // 行区间裁剪：只转换真正要返回的行。
            // 只有行对齐的普通列会传入 slice（此时每个位置对应一行），列表/映射/结构列的层级数组必须保持完整。
            var rangeStart = 0;
            var rangeEnd = positionCount;
            if (slice is not null)
            {
                rangeStart = (int)Math.Clamp(slice.Value.SkipRecords, 0, positionCount);
                rangeEnd = (int)Math.Clamp(slice.Value.SkipRecords + slice.Value.ReadRecords, rangeStart, positionCount);
            }

            object?[] data;
            if (!hasNulls)
            {
                // 无 def levels（required 列）或列不含 null 时，Values 每位置一个值且无 null，可直接使用
                var values = typed.Values;
                var valueRangeEnd = Math.Min(rangeEnd, values.Length);
                data = new object?[Math.Max(valueRangeEnd - rangeStart, 0)];
                for (var i = rangeStart; i < valueRangeEnd; i++)
                {
                    data[i - rangeStart] = NormalizeColumnValue(values[i], timePrecision);
                }
            }
            else
            {
                // 6.1.0 的 Values 对含 null 项/空项的列表列会错位（null 位置被后续物理值占用），
                // 改用 ReadRawAsync 重读物理值流（只含有值位置的值），并按 def==maxDef 展开重建，恢复 5.x 语义
                var maxDefinitionLevel = dataField.MaxDefinitionLevel;
                // 库可能额外写行标记，放大 defs/reps buffer 避免越界
                var bufferSize = positionCount + (int)Math.Min(groupReader.RowCount, int.MaxValue);
                var valuesBuffer = new T[positionCount];
                var defsBuffer = new int[bufferSize];
                var repsBuffer = new int[bufferSize];
                await groupReader.ReadRawAsync(dataField, valuesBuffer.AsMemory(), defsBuffer.AsMemory(), repsBuffer.AsMemory(), cancellationToken);

                // 先数出区间之前有多少个有效值，作为物理值下标的起点（只计数，不装箱）
                var valueIndex = 0;
                for (var i = 0; i < rangeStart; i++)
                {
                    if (definitionLevels![i] == maxDefinitionLevel)
                        valueIndex++;
                }

                data = new object?[rangeEnd - rangeStart];
                for (var i = rangeStart; i < rangeEnd; i++)
                {
                    if (definitionLevels![i] == maxDefinitionLevel)
                    {
                        data[i - rangeStart] = NormalizeColumnValue(valuesBuffer[valueIndex++], timePrecision);
                    }
                    else
                    {
                        data[i - rangeStart] = DBNull.Value;
                    }
                }
            }

            // 裁剪时层级数组要按同一区间裁剪，保证索引 0 对应请求的第一行
            if (slice is not null)
            {
                definitionLevels = SliceLevels(definitionLevels, rangeStart, rangeEnd);
                repetitionLevels = SliceLevels(repetitionLevels, rangeStart, rangeEnd);
            }

            return new RawColumnDataView
            {
                Data = data,
                DefinitionLevels = definitionLevels,
                RepetitionLevels = repetitionLevels
            };
        }

        /// <summary>
        /// 把层级数组裁剪到指定区间。
        /// </summary>
        /// <param name="levels">原始层级数组，可为 null</param>
        /// <param name="rangeStart">区间起始下标</param>
        /// <param name="rangeEnd">区间结束下标（不含）</param>
        /// <returns>裁剪后的数组；数组为空或长度不足以覆盖区间时返回 null</returns>
        /// <remarks>返回 null 与“没有层级信息”语义一致，切片路径只用于行对齐的普通列，不会因此丢失列表对齐所需的信息。</remarks>
        private static int[]? SliceLevels(int[]? levels, int rangeStart, int rangeEnd)
        {
            if (levels is null || levels.Length < rangeEnd)
            {
                return null;
            }

            if (rangeStart == 0 && rangeEnd == levels.Length)
            {
                return levels;
            }

            int[] sliced = new int[rangeEnd - rangeStart];
            Array.Copy(levels, rangeStart, sliced, 0, sliced.Length);
            return sliced;
        }

        /// <summary>
        /// Parquet.Net 6.x 以 ReadOnlyMemory&lt;char&gt;/ReadOnlyMemory&lt;byte&gt; 表达 string/byte[]，
        /// 读取时统一转回引擎预期的 CLR 类型，保证与 DataTable 列类型一致。
        /// </summary>
        /// <param name="value">库返回的原始值</param>
        /// <returns>规范化后的值（string/byte[]/原始值）</returns>
        private static object? NormalizeColumnValue<T>(T value, Parquet.Schema.TimeUnitPrecision? timePrecision)
        {
            if (value is ReadOnlyMemory<char> chars)
                return chars.Span.ToString();
            if (value is ReadOnlyMemory<byte> bytes)
                return bytes.ToArray();
            if (timePrecision is not null && value is long rawTime)
                return ConvertTimeValue(rawTime, timePrecision.Value);
            return value;
        }

        /// <summary>
        /// 将 Parquet.Net 6.x 返回的 TIME 原始整数值按精度换算为 TimeOnly（ticks 单位 100ns）。
        /// </summary>
        /// <param name="value">TIME 列的原始值（毫秒/微秒/纳秒）</param>
        /// <param name="precision">时间精度</param>
        /// <returns>对应的 TimeOnly</returns>
        private static TimeOnly ConvertTimeValue(long value, Parquet.Schema.TimeUnitPrecision precision)
        {
            return precision switch
            {
                Parquet.Schema.TimeUnitPrecision.Millis => new TimeOnly(value * 10_000),
                Parquet.Schema.TimeUnitPrecision.Micros => new TimeOnly(value * 10),
                Parquet.Schema.TimeUnitPrecision.Nanos => new TimeOnly(value / 100),
                _ => new TimeOnly(value),
            };
        }
    }
}

