using Microsoft.Win32.SafeHandles;

namespace ParquetViewer.Engine.ParquetNET
{
    /// <summary>
    /// 把文件中一段连续区间包装成可独立定位的只读流。
    /// </summary>
    /// <remarks>
    /// 用于解析"多个 parquet 文件首尾拼接在同一个文件里"的数据：每个片段都要交给 Parquet.Net 独立解析，
    /// 但片段内部的偏移量是相对片段自身的，因此需要一个带基址的流视图。
    /// 读写通过 <see cref="RandomAccess"/> 基于文件句柄直接定位，不依赖流的当前位置，
    /// 因此同一个文件句柄可以安全地被多个片段流共享并并行解析。
    /// </remarks>
    internal sealed class ParquetSegmentStream : Stream
    {
        private readonly SafeFileHandle _fileHandle;
        private readonly long _segmentStart;
        private readonly long _segmentLength;
        private long _position;

        /// <summary>
        /// 创建一个片段流视图。
        /// </summary>
        /// <param name="fileHandle">底层文件句柄，生命周期由调用方管理</param>
        /// <param name="segmentStart">片段在文件中的起始偏移</param>
        /// <param name="segmentLength">片段长度</param>
        public ParquetSegmentStream(SafeFileHandle fileHandle, long segmentStart, long segmentLength)
        {
            ArgumentNullException.ThrowIfNull(fileHandle);
            ArgumentOutOfRangeException.ThrowIfNegative(segmentStart);
            ArgumentOutOfRangeException.ThrowIfNegative(segmentLength);

            _fileHandle = fileHandle;
            _segmentStart = segmentStart;
            _segmentLength = segmentLength;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _segmentLength;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override void Flush()
        {
            // 只读流没有需要刷新的缓冲
        }

        public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long remaining = _segmentLength - _position;
            if (remaining <= 0 || buffer.Length == 0)
            {
                return 0;
            }

            int bytesToRead = (int)Math.Min(buffer.Length, remaining);
            int bytesRead = RandomAccess.Read(_fileHandle, buffer[..bytesToRead], _segmentStart + _position);
            _position += bytesRead;
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _segmentLength + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "不支持的定位基准"),
            };

            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException("片段流是只读视图");

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("片段流是只读视图");
    }
}