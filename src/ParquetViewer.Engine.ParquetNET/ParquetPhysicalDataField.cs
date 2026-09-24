using System.Reflection;
using Parquet.Schema;

namespace ParquetViewer.Engine.ParquetNET
{
    /// <summary>
    /// 构造与原字段同名同层级的"物理类型字段"，用于绕过库对特定编码的解码限制。
    /// </summary>
    /// <remarks>
    /// Parquet.Net 6.1.0 没有公开"按物理类型读取列"的入口：ReadRawAsync 依赖 DataField.ClrType 做泛型分派，
    /// 而新字段的挂载状态（IsAttachedToSchema）与层级（MaxDefinitionLevel/MaxRepetitionLevel）只有 internal 可写。
    /// 这里通过反射补齐这三处状态；库升级后若成员改名，<see cref="IsSupported"/> 会返回 false，调用方回退到原始异常。
    /// </remarks>
    internal static class ParquetPhysicalDataField
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static readonly PropertyInfo? IsAttachedToSchemaProperty = typeof(DataField).GetProperty("IsAttachedToSchema", InstanceFlags);

        private static readonly PropertyInfo? MaxDefinitionLevelProperty = typeof(DataField).GetProperty("MaxDefinitionLevel", InstanceFlags);

        private static readonly PropertyInfo? MaxRepetitionLevelProperty = typeof(DataField).GetProperty("MaxRepetitionLevel", InstanceFlags);

        /// <summary>
        /// 当前使用的 Parquet.Net 版本是否支持构造物理类型字段。
        /// </summary>
        public static bool IsSupported =>
            IsAttachedToSchemaProperty?.CanWrite == true
            && MaxDefinitionLevelProperty?.CanWrite == true
            && MaxRepetitionLevelProperty?.CanWrite == true;

        /// <summary>
        /// 按物理类型创建字段。
        /// </summary>
        /// <typeparam name="TPhysical">物理类型：int 对应 INT32，long 对应 INT64</typeparam>
        /// <param name="source">原字段，提供路径、可空性与层级信息</param>
        /// <returns>可直接传给 ReadRawAsync 的物理类型字段</returns>
        public static DataField Create<TPhysical>(DataField source) where TPhysical : struct
        {
            ArgumentNullException.ThrowIfNull(source);

            DataField field = new DataField<TPhysical>(source.Path.ToString(), source.IsNullable);
            IsAttachedToSchemaProperty!.SetValue(field, true);
            MaxDefinitionLevelProperty!.SetValue(field, source.MaxDefinitionLevel);
            MaxRepetitionLevelProperty!.SetValue(field, source.MaxRepetitionLevel);
            return field;
        }
    }
}