using ParquetViewer.Engine.Exceptions;
using System.Data;
using static ParquetViewer.Engine.DataTableLite;

namespace ParquetViewer.Engine
{
    public class DataTableLite
    {
        public record ColumnLite(string Name, Type Type, IParquetSchemaElement ParentSchema, int Ordinal);

        private int _ordinal = 0;
        private readonly Dictionary<string, ColumnLite> _columns = new();
        private readonly List<object[]> _rows;

        /// <summary>
        /// Total number of rows in the opened parquet file(s)
        /// irregardless of how many records are loaded.
        /// </summary>
        public long DataSetSize = 0;

        /// <summary>
        /// Columns in the dataset
        /// </summary>
        public IReadOnlyDictionary<string, ColumnLite> Columns => _columns;

        /// <summary>
        /// Rows of the dataset
        /// </summary>
        public IReadOnlyList<object[]> Rows => _rows;

        public DataTableLite(int expectedRowCount = 1000)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(expectedRowCount, 0);

            this._rows = new(expectedRowCount);
        }

        public ColumnLite AddColumn(string name, Type type, IParquetSchemaElement parent)
        {
            if (_rows.Count > 0)
            {
                throw new InvalidOperationException("Can't add columns after creating rows");
            }

            var column = new ColumnLite(name, type, parent, _ordinal++);
            _columns.Add(name, column);
            return column;
        }

        public void NewRow()
        {
            var row = new object[Columns.Count];
            _rows.Add(row);
        }

        /// <summary>
        /// 预分配行容量，避免逐行扩容。
        /// </summary>
        /// <param name="expectedRowCount">预期行数</param>
        /// <remarks>并行分片是先克隆出列定义、再确定行数，用它一次性预留底层数组容量。</remarks>
        public void EnsureCapacity(int expectedRowCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(expectedRowCount);

            if (expectedRowCount > this._rows.Count)
            {
                this._rows.EnsureCapacity(expectedRowCount);
            }
        }

        public DataTable ToDataTable(CancellationToken token, IProgress<int>? progress = null)
        {
            var dataTable = new DataTable();
            foreach (var column in _columns)
            {
                token.ThrowIfCancellationRequested();

                var columnLite = column.Value;

                if (dataTable.Columns.Contains(columnLite.Name))
                {
                    //DataTable's don't support case sensitive field names unfortunately
                    var columnPath = columnLite.ParentSchema + "/" + columnLite.Name;
                    throw new NotSupportedException($"Duplicate column '{columnPath}' detected. Column names are case insensitive and must be unique.");
                }

                var columnType = columnLite.Type;
                dataTable.Columns.Add(new DataColumn(columnName: columnLite.Name, dataType: columnType));
            }

            dataTable.BeginLoadData();
            for (var i = 0; i < _rows.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                LoadRow(dataTable, _rows[i]);
                progress?.Report(_columns.Count);
            }
            dataTable.EndLoadData();

            return dataTable;

            void LoadRow(DataTable dataTable, object[] values)
            {
                try
                {
                    //supposedly this is the fastest way to load data into a datatable https://stackoverflow.com/a/17123914/1458738
                    dataTable.LoadDataRow(values, false);
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("Type of value has a mismatch with column type"))
                    {
                        //Try figure out where the mismatch is
                        var columnIndex = 0;
                        foreach (var column in this._columns.Values)
                        {
                            if (values[columnIndex] != DBNull.Value && column.Type != values[columnIndex].GetType())
                            {
                                throw new TypeMismatchException($"Value type '{values[columnIndex]?.GetType()}' doesn't match column type {column.Type} for field `{column.Name}`");
                            }
                            columnIndex++;
                        }

                        throw new TypeMismatchException(null, ex);
                    }

                    throw;
                }
            }
        }

        //Gets a reference to the row data at the specified index
        public DataRowLite GetRowAt(int index)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(index, 0, nameof(index));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _rows.Count, nameof(index));

            return new DataRowLite(_rows[index], _columns.Values, this);
        }

        public DataTableLite Clone()
        {
            var clone = new DataTableLite();
            foreach (var column in this.Columns.Values)
            {
                clone.AddColumn(column.Name, column.Type, column.ParentSchema);
            }
            return clone;
        }

        /// <summary>
        /// 把另一个结构相同的 DataTableLite 的所有行追加到当前表末尾。
        /// </summary>
        /// <param name="source">来源表，列的数量、名称与类型必须与当前表一致</param>
        /// <remarks>
        /// 只做行数组的引用拷贝，不复制单元格内容，用于并行分片读取后按分片顺序合并结果。
        /// 合并顺序由调用方保证，本方法不做任何排序。
        /// </remarks>
        /// <exception cref="InvalidOperationException">列数量或列类型不一致时抛出</exception>
        public void AppendRowsFrom(DataTableLite source)
        {
            ArgumentNullException.ThrowIfNull(source);

            if (source._columns.Count != this._columns.Count)
            {
                throw new InvalidOperationException($"Column count mismatch when appending rows: {source._columns.Count} vs {this._columns.Count}");
            }

            foreach (var column in this._columns)
            {
                if (!source._columns.TryGetValue(column.Key, out var sourceColumn))
                {
                    throw new InvalidOperationException($"Column `{column.Key}` is missing in the source table");
                }

                if (sourceColumn.Type != column.Value.Type)
                {
                    throw new InvalidOperationException($"Column `{column.Key}` type mismatch when appending rows: {sourceColumn.Type} vs {column.Value.Type}");
                }
            }

            if (source._rows.Count == 0)
            {
                return;
            }

            this._rows.AddRange(source._rows);
        }
    }

    public class DataRowLite : IDataRowLite
    {
        public IReadOnlyCollection<string> ColumnNames => Columns.Keys;
        public Dictionary<string, ColumnLite> Columns { get; }
        public object[] Row { get; }
        public DataTableLite Table { get; }
        public DataRowLite(object[] data, IEnumerable<ColumnLite> columns, DataTableLite table)
        {
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(columns);
            ArgumentNullException.ThrowIfNull(table);

            Table = table;
            Row = data;
            Columns = columns.ToDictionary(c => c.Name);
            if (Row.Length != Columns.Count)
            {
                throw new ArgumentException($"Data length {data.Length} doesn't match number of columns {columns.Count()}", nameof(data));
            }
        }
        public object GetValue(string columnName)
        {
            if (!this.Columns.ContainsKey(columnName))
            {
                throw new IndexOutOfRangeException($"Column `{columnName}` not found");
            }

            var index = 0;
            foreach (var column in this.Columns.Keys)
            {
                if (column.Equals(columnName))
                {
                    return this.Row[index];
                }
                index++;
            }
            throw new IndexOutOfRangeException($"Could not get value for column `{columnName}`");
        }
    }

    public interface IDataRowLite
    {
        IReadOnlyCollection<string> ColumnNames { get; }
        object[] Row { get; }
        object GetValue(string columnName);
    }
}