using ParquetViewer.Engine.Types;

namespace ParquetViewer.Engine
{
    //Global settings, what can go wrong? It's convenient, though.
    public static class ParquetEngineSettings
    {
        /// <summary>
        /// By default Parquet Engine will render Dates using the system culture's format.
        /// By setting this value a custom date format can be used instead.
        /// </summary>
        /// <remarks>Parquet Engine renders dates when converting <see cref="IListValue"/>, 
        /// <see cref="IStructValue"/>, and <see cref="IMapValue"/> types to string.</remarks>
        public static string? DateDisplayFormat { get; set; }
        public static string? DateOnlyDisplayFormat { get; set; }
        public static string? TimeOnlyDisplayFormat { get; set; }

        /// <summary>
        /// 按 row group 并行分片读取时的最大并行度。
        /// </summary>
        /// <remarks>
        /// 0（默认）表示自动：取 CPU 逻辑核数与引擎内部上限（当前为 8）的较小值；
        /// 1 表示禁用并行，用于网络盘/机械盘等并行反而更慢的场景或排查问题；
        /// 大于 1 时按该值限制并行度（仍不会超过请求覆盖的 row group 数量）。
        /// 仅单文件读取且请求覆盖多个 row group 时才会启用并行。
        /// </remarks>
        public static int MaxReadParallelism { get; set; } = 0;
    }
}