namespace ParquetViewer.Engine.ParquetNET
{
    /// <summary>
    /// Parquet.Net 6.x 以泛型 RawColumnData&lt;T&gt; 取代了旧 DataColumn，
    /// 本视图将读取结果统一转为 object 数组与 definition/repetition level 数组，
    /// 保持引擎既有读取逻辑（GetDataWithPaddedNulls 等）不变。
    /// </summary>
    internal sealed class RawColumnDataView
    {
        public object?[] Data { get; init; } = Array.Empty<object?>();
        public int[]? DefinitionLevels { get; init; }
        public int[]? RepetitionLevels { get; init; }
    }

    internal static class Helpers
    {
        #region Dubious Functions
        //This logic is a cluster f... right now. It blends https://www.aloneguid.uk/posts/2023/04/parquet-empty-vs-null
        //with some of my understanding of how the dremel algorithm works. No way will it work for all cases.

        public static bool IsNull(this RawColumnDataView dataColumn, int index, ParquetSchemaElement field)
            => dataColumn.DefinitionLevels?.Length > index && dataColumn.DefinitionLevels[index] <= field.CurrentDefinitionLevel - 1;

        public static bool IsEmpty(this RawColumnDataView dataColumn, int index, ParquetSchemaElement field)
            => dataColumn.DefinitionLevels?.Length > index && dataColumn.DefinitionLevels[index] == field.CurrentDefinitionLevel
                    && field.DataField?.MaxDefinitionLevel != dataColumn.DefinitionLevels[index] /*Fixes STRUCT_TYPE_TEST*/;
        #endregion

        /// <summary>
        /// Some parquet writers don't write null entries into the data array for empty and null lists.
        /// This throws off our logic so lets find all empty/null lists and add a null entry into 
        /// the data array to align it with the repetition/definition levels.
        /// </summary>
        /// <param name="dataColumn">The parquet column data</param>
        public static IEnumerable<object> GetDataWithPaddedNulls(this RawColumnDataView dataColumn, ParquetSchemaElement field)
        {
            var dataEnumerable = dataColumn.Data.Cast<object?>().Select(d => d ?? DBNull.Value);

            int levelCount = dataColumn.DefinitionLevels?.Length ?? 0;
            if (levelCount > dataColumn.Data.Length)
            {
                dataEnumerable = GetDataWithPaddedNulls();

                IEnumerable<object> GetDataWithPaddedNulls()
                {
                    var index = -1;
                    foreach (var data in dataColumn.Data)
                    {
                        index++;

                        while (dataColumn.IsEmpty(index, field) || dataColumn.IsNull(index, field))
                        {
                            yield return DBNull.Value;
                            index++;
                        }

                        yield return data ?? DBNull.Value;
                    }

                    //Need to handle case where last N rows are null/empty
                    while (levelCount > index + 1)
                    {
                        yield return DBNull.Value;
                        index++;
                    }
                }
            }

            return dataEnumerable;
        }
    }
}
