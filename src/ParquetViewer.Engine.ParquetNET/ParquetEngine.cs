using Microsoft.Win32.SafeHandles;
using Parquet;
using Parquet.Meta;
using Parquet.Data;
using Parquet.Schema;
using ParquetViewer.Engine.Exceptions;
using ParquetViewer.Engine.Types;
using System.Data;

namespace ParquetViewer.Engine.ParquetNET
{
    public partial class ParquetEngine : IParquetEngine, IDisposable
    {
        // Parquet.Net 6.x 移除了 UseTimeOnlyTypeForTimeMillis/Micros 选项，仅保留日期相关选项
        private static readonly ParquetOptions _defaultParquetOptions = new() { UseDateOnlyTypeForDates = true };
        private readonly (string ParquetFilePath, ParquetReader Reader)[] _parquetFiles;

        // 拼接式 parquet 文件（多个 parquet 文件首尾拼接在同一个文件里）的片段来源。
        // 非 null 时数据由 _segmentSource 中的片段按顺序组成，_parquetFiles 为空数组。
        private readonly ParquetSegmentSource? _segmentSource;

        private long? _recordCount;

        private ParquetReader _defaultReader => _parquetFiles.Length > 0
            ? _parquetFiles[0].Reader
            : _segmentSource?.DefaultReader ?? throw new ParquetEngineException("No parquet readers available");

        private FileMetaData _thriftMetadata => _defaultReader.Metadata ?? throw new ParquetEngineException("No thrift metadata was found");

        private ParquetSchema _schema => _defaultReader.Schema;

        public Dictionary<string, string> CustomMetadata => _defaultReader.CustomMetadata;

        public long RecordCount => _recordCount ??= _segmentSource is not null
            ? _segmentSource.TotalRowCount
            : _parquetFiles.Sum(pf => pf.Reader.Metadata?.NumRows ?? 0);

        // 拼接文件在文件系统上仍然只是一个文件，因此分区数按 1 统计
        public int NumberOfPartitions => _segmentSource is not null ? 1 : _parquetFiles.Length;

        public List<string> Fields => _defaultReader.Schema.Fields.Select(f => f.Name).ToList();

        public string Path { get; }

        ParquetMetadata? _metadata = null;
        public IParquetMetadata Metadata => _metadata ??= new ParquetMetadata(_thriftMetadata, BuildParquetSchemaTree(), (int)RecordCount);

        private ParquetEngine(string fileOrFolderPath, params (string FilePath, ParquetReader Reader)[] parquetFiles)
        {
            _parquetFiles = parquetFiles ?? throw new ArgumentNullException(nameof(parquetFiles), "No parquet readers provided");
            _segmentSource = null;
            Path = fileOrFolderPath;
        }

        /// <summary>
        /// 以"拼接式 parquet 文件"的片段集合构造引擎。
        /// </summary>
        /// <param name="fileOrFolderPath">文件路径</param>
        /// <param name="segmentSource">片段来源，负责按需打开各片段并统计总行数</param>
        private ParquetEngine(string fileOrFolderPath, ParquetSegmentSource segmentSource)
        {
            _parquetFiles = Array.Empty<(string, ParquetReader)>();
            _segmentSource = segmentSource ?? throw new ArgumentNullException(nameof(segmentSource));
            Path = fileOrFolderPath;
        }

        private ParquetSchemaElement BuildParquetSchemaTree()
        {
            var thriftSchema = _thriftMetadata.Schema ?? throw new ParquetException("No thrift metadata was found");
            var schemaElements = thriftSchema.GetEnumerator();
            var thriftSchemaTree = ReadSchemaTree(ref schemaElements);

            foreach (var dataField in _schema.GetDataFields())
            {
                var field = thriftSchemaTree.GetChild(dataField.Path.FirstPart ?? throw new MalformedFieldException($"Field has no schema path: `{dataField.Name}`"));
                for (var i = 1; i < dataField.Path.Length; i++)
                {
                    field = field.GetChild(dataField.Path[i]);
                }
                field.DataField = dataField; //if it doesn't have a child it's a datafield (I hope)
            }

            return thriftSchemaTree;
        }

        private static ParquetSchemaElement ReadSchemaTree(ref List<SchemaElement>.Enumerator schemaElements)
        {
            if (!schemaElements.MoveNext())
                throw new ParquetException("Invalid parquet schema");

            var current = schemaElements.Current;
            var parquetSchemaElement = new ParquetSchemaElement(current);
            for (int i = 0; i < current.NumChildren; i++)
            {
                parquetSchemaElement.AddChild(ReadSchemaTree(ref schemaElements));
            }
            return parquetSchemaElement;
        }

        public static Task<ParquetEngine> OpenFileOrFolderAsync(string fileOrFolderPath, CancellationToken cancellationToken)
        {
            if (File.Exists(fileOrFolderPath)) //Handles null
            {
                return OpenFileAsync(fileOrFolderPath, cancellationToken);
            }
            else if (Directory.Exists(fileOrFolderPath)) //Handles null
            {
                return OpenFolderAsync(fileOrFolderPath, cancellationToken);
            }
            else
            {
                throw new FileNotFoundException($"Could not find file or folder at location: {fileOrFolderPath}");
            }
        }

        public static async Task<ParquetEngine> OpenFileAsync(string parquetFilePath, CancellationToken cancellationToken)
        {
            if (!File.Exists(parquetFilePath)) //Handles null
            {
                throw new FileNotFoundException($"Could not find parquet file at: {parquetFilePath}");
            }

            Stream? readOnlyNonLockingStream = null;
            try
            {
                readOnlyNonLockingStream = new FileStream(parquetFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long fileLength = readOnlyNonLockingStream.Length;
                var parquetReader = await ParquetReader.CreateAsync(readOnlyNonLockingStream, _defaultParquetOptions, false, cancellationToken);

                // 部分数据源会把多个 parquet 文件首尾拼接成一个文件，这类文件的 footer 偏移只对最后一个片段有效，
                // 按整文件解析会 seek 到错位位置读到别的片段数据，因此检测到偏移不匹配时改按片段逐个解析
                if (parquetReader.Metadata is not null
                    && ParquetFileSegments.HasUnmatchedFooterOffset(parquetFilePath, fileLength, parquetReader.Metadata))
                {
                    var segmentedEngine = await TryOpenSegmentedFileAsync(parquetFilePath, fileLength, parquetReader, readOnlyNonLockingStream, cancellationToken);
                    if (segmentedEngine is not null)
                    {
                        return segmentedEngine;
                    }
                }

                return new ParquetEngine(parquetFilePath, (parquetFilePath, parquetReader));
            }
            catch (Exception ex)
            {
                readOnlyNonLockingStream?.Dispose();
                throw new FileReadException(ex);
            }
        }

        /// <summary>
        /// 尝试把"多个 parquet 文件首尾拼接而成"的文件按片段逐个打开。
        /// </summary>
        /// <param name="parquetFilePath">文件路径</param>
        /// <param name="fileLength">文件长度</param>
        /// <param name="wholeFileReader">按整文件打开的 reader，确认是拼接文件后由本方法释放</param>
        /// <param name="wholeFileStream">按整文件打开的流，确认是拼接文件后由本方法释放</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>拼接文件引擎；不是拼接文件时返回 null（此时传入的 reader 与流仍然有效）</returns>
        private static async Task<ParquetEngine?> TryOpenSegmentedFileAsync(string parquetFilePath, long fileLength,
            ParquetReader wholeFileReader, Stream wholeFileStream, CancellationToken cancellationToken)
        {
            // 片段边界只能靠扫描文件中的 PAR1 魔数确定，因此仅在 footer 偏移明显不匹配时才执行
            SafeFileHandle fileHandle = File.OpenHandle(parquetFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            List<(long Offset, long Length)> segments;
            try
            {
                segments = ParquetFileSegments.FindSegments(fileHandle, fileLength, cancellationToken);
            }
            catch
            {
                fileHandle.Dispose();
                throw;
            }

            if (segments.Count <= 1)
            {
                // 偏移异常但并非拼接文件（例如数据区之后带有大段填充），仍按普通单文件处理
                fileHandle.Dispose();
                return null;
            }

            // 确认是拼接文件后再释放按整文件打开的 reader 与流
            await wholeFileReader.DisposeAsync();
            wholeFileStream.Dispose();

            try
            {
                bool useDateOnlyTypeForDates = _defaultParquetOptions.UseDateOnlyTypeForDates;
                ParquetReader firstSegmentReader = await ParquetSegmentSource.CreateSegmentReaderAsync(
                    fileHandle, segments[0].Offset, segments[0].Length, useDateOnlyTypeForDates, cancellationToken);
                ParquetSegmentSource segmentSource = await ParquetSegmentSource.OpenAsync(
                    fileHandle, useDateOnlyTypeForDates, segments, firstSegmentReader, cancellationToken);
                return new ParquetEngine(parquetFilePath, segmentSource);
            }
            catch
            {
                fileHandle.Dispose();
                throw;
            }
        }

        public static async Task<ParquetEngine> OpenFolderAsync(string folderPath, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(folderPath)) //Handles null
            {
                throw new DirectoryNotFoundException($"Directory doesn't exist: {folderPath}");
            }

            var skippedFiles = new Dictionary<string, Exception>();
            var fileGroups = new Dictionary<ParquetSchema, List<(string FilePath, ParquetReader Reader)>>();
            foreach (var file in Engine.Helpers.ListParquetFiles(folderPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                Stream? readOnlyNonLockingStream = null;
                try
                {
                    readOnlyNonLockingStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var parquetReader = await ParquetReader.CreateAsync(readOnlyNonLockingStream, _defaultParquetOptions, false, cancellationToken);
                    if (!fileGroups.ContainsKey(parquetReader.Schema))
                    {
                        fileGroups.Add(parquetReader.Schema, new List<(string, ParquetReader)>());
                    }

                    fileGroups[parquetReader.Schema].Add((file, parquetReader));
                }
                catch (Exception ex)
                {
                    readOnlyNonLockingStream?.Dispose();
                    skippedFiles.Add(System.IO.Path.GetRelativePath(folderPath, file), ex);
                }
            }

            if (fileGroups.Keys.Count == 0)
            {
                if (skippedFiles.Count == 0)
                {
                    throw new FileNotFoundException("Directory is empty");
                }
                else
                {
                    throw new AllFilesSkippedException(skippedFiles);
                }
            }
            else if (fileGroups.Keys.Count > 1)
            {
                //We found more than one type of schema.
                foreach (var fileGroupList in fileGroups.Values)
                {
                    DisposeReaders(fileGroupList.Select(f => f.Reader));
                }

                throw new MultipleSchemasFoundException(fileGroups.Keys.ToList()
                    .Select(schema => schema.Fields.Select(f => f.Name).ToList()).ToList());
            }
            else if (skippedFiles.Count > 0)
            {
                //We found one schema but some files couldn't be read
                DisposeReaders(fileGroups.Values.First().Select(f => f.Reader));
                throw new SomeFilesSkippedException(skippedFiles);
            }

            cancellationToken.ThrowIfCancellationRequested();

            return new ParquetEngine(folderPath, fileGroups.Values.First().ToArray());
        }

        private IEnumerable<(long RemainingOffset, ParquetReader ParquetReader)> GetReaders(long offset)
        {
            if (_segmentSource is not null)
            {
                // 拼接文件：先按行号定位到片段，再由片段来源打开（必要时懒加载）对应 reader
                foreach ((long remainingOffset, ParquetReader reader) in _segmentSource.GetReaders(offset))
                {
                    yield return (remainingOffset, reader);
                }

                yield break;
            }

            foreach (var parquetFile in _parquetFiles)
            {
                if (offset >= parquetFile.Reader.Metadata?.NumRows)
                {
                    offset -= parquetFile.Reader.Metadata.NumRows;
                    continue;
                }

                yield return (offset, parquetFile.Reader);
                offset = 0;
            }
        }

        /// <summary>
        /// 为并行分片额外打开一个独立的 ParquetReader。
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>独占该文件流的 reader，由调用方负责释放</returns>
        /// <remarks>
        /// ParquetRowGroupReader 共享底层流并依赖流位置，多个线程并发读取同一个 reader 会互相破坏读位置，
        /// 因此并行分片必须各自持有独立 reader。目前仅支持单文件引擎。
        /// </remarks>
        private async Task<ParquetReader> OpenAdditionalReaderAsync(CancellationToken cancellationToken)
        {
            // 拼接文件的 reader 由 ParquetSegmentSource 统一管理，且其并行读取路径已关闭
            if (this._parquetFiles.Length != 1 || this._segmentSource is not null)
            {
                throw new InvalidOperationException("Additional readers are only supported for single file parquet engines");
            }

            // 每个 reader 使用独立的 ParquetOptions 实例，避免多个 reader 共享同一个可变配置对象
            var options = new ParquetOptions { UseDateOnlyTypeForDates = _defaultParquetOptions.UseDateOnlyTypeForDates };
            var readOnlyNonLockingStream = new FileStream(this._parquetFiles[0].ParquetFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                return await ParquetReader.CreateAsync(readOnlyNonLockingStream, options, false, cancellationToken);
            }
            catch
            {
                readOnlyNonLockingStream.Dispose();
                throw;
            }
        }

        public async Task WriteDataToParquetFileAsync(DataTable dataTable, string path,
            CancellationToken cancellationToken, IProgress<int> progress, Dictionary<string, string>? customMetadata)
        {
            var fields = new List<Field>(dataTable.Columns.Count);
            foreach (DataColumn column in dataTable.Columns)
            {
                fields.Add(this._schema.Fields
                    .Where(field => field.Name.Equals(column.ColumnName, StringComparison.InvariantCulture))
                    .First());
            }
            var parquetSchema = new ParquetSchema(fields);

            var writeOptions = new ParquetOptions { UseDateOnlyTypeForDates = true, CompressionLevel = System.IO.Compression.CompressionLevel.Optimal };
            await using var fs = new FileStream(path, FileMode.OpenOrCreate);
            // Parquet.Net 6.x 的 ParquetWriter 仅实现 IAsyncDisposable，且压缩级别通过 ParquetOptions 配置
            await using var parquetWriter = await ParquetWriter.CreateAsync(parquetSchema, fs, writeOptions, false, cancellationToken);
            if (customMetadata is not null)
                parquetWriter.CustomMetadata = customMetadata;

            const int MAX_ROWS_PER_ROWGROUP = 100_000; //Without batching we sometimes get "OverflowException: Array dimensions exceeded supported range" from Parquet.NET
            var batchIndex = 0;
            var isLastBatch = false;
            while (!isLastBatch)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                using var rowGroup = parquetWriter.CreateRowGroup();
                foreach (var dataField in parquetSchema.DataFields)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    var type = dataField.IsNullable ? GetNullableVersion(dataField.ClrType) : dataField.ClrType;
                    type = GetEngineClrType(type);
                    var values = GetColumnValues(dataTable, type, dataField.Name, batchIndex * MAX_ROWS_PER_ROWGROUP, MAX_ROWS_PER_ROWGROUP);
                    // 6.x 以泛型 WriteAsync 取代 WriteColumnAsync(DataColumn)，按元素类型分派
                    await WriteColumnValuesAsync(rowGroup, dataField, values, cancellationToken);
                    progress.Report(values.Length); //No way to report progress for each row, so do it by column
                    isLastBatch = values.Length < MAX_ROWS_PER_ROWGROUP;
                }
                batchIndex++;
            }
        }

        /// <summary>
        /// 按列元素类型将值数组写入 row group（Parquet.Net 6.x 的泛型 WriteAsync 需按类型分派）。
        /// </summary>
        /// <param name="rowGroup">目标 row group</param>
        /// <param name="dataField">目标字段</param>
        /// <param name="values">列值数组，元素可能是 Nullable&lt;T&gt;</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>写入完成的任务</returns>
        private static Task WriteColumnValuesAsync(ParquetRowGroupWriter rowGroup, DataField dataField, Array values, CancellationToken cancellationToken)
        {
            var elementType = values.GetType().GetElementType() ?? throw new InvalidOperationException("Column values cannot be empty");
            if (elementType == typeof(string))
            {
                // string 列使用库提供的专用重载
                return rowGroup.WriteAsync(dataField, (string[])values, null);
            }
            if (elementType == typeof(byte[]))
            {
                // byte[] 列使用库提供的专用重载（byte[][] 实现 IReadOnlyCollection<byte[]>）
                var typedBytes = (byte[][])values;
                return rowGroup.WriteAsync(dataField, typedBytes, null);
            }
            if (elementType.IsGenericType && elementType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                var underlyingType = Nullable.GetUnderlyingType(elementType)!;
                var nullableMethod = typeof(ParquetEngine).GetMethod(nameof(WriteNullableColumnValuesAsync),
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MakeGenericMethod(underlyingType);
                return (Task)nullableMethod.Invoke(null, new object[] { rowGroup, dataField, values, cancellationToken })!;
            }
            else
            {
                var method = typeof(ParquetEngine).GetMethod(nameof(WriteColumnValuesAsyncGeneric),
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MakeGenericMethod(elementType);
                return (Task)method.Invoke(null, new object[] { rowGroup, dataField, values, cancellationToken })!;
            }
        }

        private static async Task WriteColumnValuesAsyncGeneric<T>(ParquetRowGroupWriter rowGroup, DataField dataField, Array values, CancellationToken cancellationToken) where T : struct
        {
            var typedValues = (T[])values;
            await rowGroup.WriteAsync<T>(dataField, typedValues.AsMemory(), null, null, cancellationToken);
        }

        private static async Task WriteNullableColumnValuesAsync<T>(ParquetRowGroupWriter rowGroup, DataField dataField, Array values, CancellationToken cancellationToken) where T : struct
        {
            var typedValues = (T?[])values;
            await rowGroup.WriteAsync<T>(dataField, (ReadOnlyMemory<T?>)typedValues.AsMemory(), null, null, cancellationToken);
        }

        /// <summary>
        /// Parquet.Net 6.x 的 ParquetReader 仅实现 IAsyncDisposable，同步释放多个 reader。
        /// </summary>
        /// <param name="readers">待释放的 reader 序列</param>
        private static void DisposeReaders(IEnumerable<ParquetReader> readers)
        {
            foreach (var reader in readers)
            {
                reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// Parquet.Net 6.x 将 string/byte[] 列映射为 ReadOnlyMemory&lt;T&gt;，写入前还原为引擎预期类型。
        /// </summary>
        /// <param name="type">DataField.ClrType 或其它来源的 CLR 类型</param>
        /// <returns>还原后的 CLR 类型</returns>
        private static System.Type GetEngineClrType(System.Type type)
        {
            if (type == typeof(ReadOnlyMemory<char>))
                return typeof(string);
            if (type == typeof(ReadOnlyMemory<byte>))
                return typeof(byte[]);
            return type;
        }

        public void Dispose()
        {
            // Parquet.Net 6.x 的 ParquetReader 仅实现 IAsyncDisposable，这里以同步方式完成异步释放
            foreach (var (_, reader) in _parquetFiles)
            {
                reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            // 拼接文件：释放已缓存的片段 reader 与文件句柄
            _segmentSource?.Dispose();
        }

        private static System.Type GetNullableVersion(System.Type sourceType) => sourceType == null
                ? throw new ArgumentNullException(nameof(sourceType))
                : !sourceType.IsValueType
                    || (sourceType.IsGenericType
                        && sourceType.GetGenericTypeDefinition() == typeof(Nullable<>))
                ? sourceType
                : typeof(Nullable<>).MakeGenericType(sourceType);

        private static Array GetColumnValues(DataTable dataTable, System.Type type, string columnName, int skipCount, int fetchCount)
        {
            ArgumentNullException.ThrowIfNull(dataTable);
            ArgumentNullException.ThrowIfNull(type);
            ArgumentOutOfRangeException.ThrowIfLessThan(skipCount, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fetchCount, 0);

            if (!dataTable.Columns.Contains(columnName))
                throw new ArgumentException($"Column `{columnName}` does not exist in the datatable");

            var recordCountAfterSkip = dataTable.Rows.Count - skipCount;
            var recordCountToRead = fetchCount > recordCountAfterSkip ? recordCountAfterSkip : fetchCount;
            var values = Array.CreateInstance(type, recordCountToRead);
            var index = 0;
            foreach (DataRow row in dataTable.Rows)
            {
                if (skipCount-- > 0)
                {
                    continue;
                }

                var value = row[columnName];
                if (value == DBNull.Value)
                    value = null;
                else if (value is IByteArrayValue byteArray)
                    value = byteArray.Data;
                else if (value is IListValue || value is IMapValue || value is IStructValue)
                    throw new NotSupportedException("List, Map, and Struct types are currently not supported.");

                values.SetValue(value, index++);

                if (--fetchCount <= 0)
                {
                    break;
                }
            }

            return values;
        }

        // 拼接文件在文件系统上只是一个文件，因此只返回该文件路径（供文件变更监控使用）
        public IEnumerable<string> GetOpenParquetFilePaths() => this._segmentSource is not null
            ? [this.Path]
            : this._parquetFiles.Select(db => db.ParquetFilePath);
    }
}