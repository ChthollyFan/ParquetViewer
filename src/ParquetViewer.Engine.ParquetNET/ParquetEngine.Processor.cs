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
        public async Task<Func<bool, DataTable>> ReadRowsAsync(List<string> selectedFields, int offset, int recordCount, CancellationToken cancellationToken, IProgress<int>? progress = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recordCount, nameof(recordCount));
            ArgumentOutOfRangeException.ThrowIfNegative(offset, nameof(offset));

            long recordsLeftToRead = recordCount;
            DataTableLite result = BuildDataTable(null, selectedFields, Math.Min(recordCount, (int)this.RecordCount));

            foreach (var reader in this.GetReaders(offset))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (recordsLeftToRead <= 0)
                    break;

                recordsLeftToRead = await PopulateDataTable(result, reader.ParquetReader, reader.RemainingOffset, recordsLeftToRead, cancellationToken, progress);
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
            int skippedRecords = 0;
            var fieldIndex = dataTable.Columns[field.Path]?.Ordinal ?? throw new ParquetEngineException($"Column `{field.Path}` is missing");

            if (field.BelongsToListField || field.BelongsToListOfStructsField || field.DataField?.IsArray == true)
            {
                await ReadListField(dataTable, groupReader, rowBeginIndex, field, fieldIndex, skipRecords, readRecords, isFirstColumn, cancellationToken, progress);
            }
            else
            {
                var dataColumn = await ReadColumnAsync(groupReader, field, cancellationToken);
                var dataEnumerable = dataColumn.GetDataWithPaddedNulls(field);

                var fieldType = dataTable.Columns[field.Path].Type;
                foreach (var value in dataEnumerable)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (skipRecords > skippedRecords)
                    {
                        skippedRecords++;
                        continue;
                    }

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
                    progress?.Report(1);
                }
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

                    foreach (var listValue in listValues)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (isFirstColumn)
                        {
                            dataTable.NewRow();
                        }

                        dataTable.Rows[rowIndex][fieldIndex] = listValue;
                        rowIndex++;
                        progress?.Report(1);
                    }
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
                    progress?.Report(1);

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
            progress.ProgressChanged += (int progressSoFar) =>
            {
                if (fieldCount > 0)
                {
                    //To report progress accurately we'll need to divide the progress total  
                    //by the field count to convert it to row count in the main data table.
                    var increment = progressSoFar % fieldCount;
                    if (increment == 0)
                        _progress?.Report(1);
                }
                else
                {
                    //If the struct field has no columns, then each read is one row.
                    _progress?.Report(1);
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

        private static async Task<RawColumnDataView> ReadColumnAsync(ParquetRowGroupReader groupReader, ParquetSchemaElement field, CancellationToken cancellationToken)
        {
            try
            {
                // Parquet.Net 6.x 用 ReadRawColumnDataBaseAsync 取代 ReadColumnAsync，返回泛型 RawColumnData<T>
                var dataField = field.DataField ?? throw new MalformedFieldException($"Field `{field.PathWithParent}` has no data field");
                var rawColumnData = await groupReader.ReadRawColumnDataBaseAsync(dataField, cancellationToken);
                return await ConvertRawColumnDataView(groupReader, dataField, rawColumnData, field, cancellationToken);
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
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>统一视图，Data 为每行值的 object 数组</returns>
        private static async Task<RawColumnDataView> ConvertRawColumnDataView(ParquetRowGroupReader groupReader, DataField dataField, RawColumnData rawColumnData, ParquetSchemaElement field, CancellationToken cancellationToken)
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
                return await (Task<RawColumnDataView>)method.Invoke(null, new object[] { groupReader, dataField, rawColumnData, timePrecision, cancellationToken })!;
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                // 反射调用会把泛型方法内部异常包装为 TargetInvocationException，解包后按原异常类型上抛
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private static async Task<RawColumnDataView> ConvertRawColumnDataViewGeneric<T>(ParquetRowGroupReader groupReader, DataField dataField, RawColumnData rawColumnData, Parquet.Schema.TimeUnitPrecision? timePrecision, CancellationToken cancellationToken) where T : struct
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

            object?[] data;
            if (definitionLevels is null || !definitionLevels.Any(d => d < dataField.MaxDefinitionLevel))
            {
                // 无 def levels（required 列）或列不含 null 时，Values 每位置一个值且无 null，可直接使用
                var values = typed.Values;
                data = new object?[values.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    data[i] = NormalizeColumnValue(values[i], timePrecision);
                }
            }
            else
            {
                // 6.1.0 的 Values 对含 null 项/空项的列表列会错位（null 位置被后续物理值占用），
                // 改用 ReadRawAsync 重读物理值流（只含有值位置的值），并按 def==maxDef 展开重建，恢复 5.x 语义
                var maxDefinitionLevel = dataField.MaxDefinitionLevel;
                var positionCount = definitionLevels.Length;
                // 库可能额外写行标记，放大 defs/reps buffer 避免越界
                var bufferSize = positionCount + (int)Math.Min(groupReader.RowCount, int.MaxValue);
                var valuesBuffer = new T[positionCount];
                var defsBuffer = new int[bufferSize];
                var repsBuffer = new int[bufferSize];
                await groupReader.ReadRawAsync(dataField, valuesBuffer.AsMemory(), defsBuffer.AsMemory(), repsBuffer.AsMemory(), cancellationToken);
                data = new object?[positionCount];
                var valueIndex = 0;
                for (var i = 0; i < positionCount; i++)
                {
                    if (definitionLevels[i] == maxDefinitionLevel)
                    {
                        data[i] = NormalizeColumnValue(valuesBuffer[valueIndex++], timePrecision);
                    }
                    else
                    {
                        data[i] = DBNull.Value;
                    }
                }
            }

            return new RawColumnDataView
            {
                Data = data,
                DefinitionLevels = definitionLevels,
                RepetitionLevels = repetitionLevels
            };
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

