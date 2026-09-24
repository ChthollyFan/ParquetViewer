using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using ParquetViewer.Engine.Exceptions;

namespace ParquetViewer.Engine.ParquetNET
{
    /// <summary>
    /// 轻量读取 parquet 片段 footer 中的行数。
    /// </summary>
    /// <remarks>
    /// 拼接文件可能包含数千个片段，用 <see cref="Parquet.ParquetReader"/> 依次打开每个片段会构建完整的列 schema 与行组元数据，
    /// 产生大量临时对象（实测 5153 个片段需要 35 秒，而 footer 本身的读取只需约 0.1 秒）。
    /// 这里按 thrift compact 协议只解析 FileMetaData 的 version 与 num_rows 字段，schema 与 row_groups 按字节跳过，
    /// 把每个片段的解析开销降到与 footer 体积同量级。
    /// 字段编号与类型来自 parquet-format 的 parquet.thrift（1: version、2: schema、3: num_rows）。
    /// 任何不符合预期的结构都会抛出异常，由调用方回退到库的完整解析，避免因优化而打不开文件。
    /// </remarks>
    internal static class ParquetFooterReader
    {
        /// <summary>片段尾部结构：[footer][4 字节 footer 长度][PAR1]</summary>
        private const int FOOTER_TAIL_LENGTH = 4 + 4;

        /// <summary>FileMetaData 中 version 字段的编号</summary>
        private const int VERSION_FIELD_ID = 1;

        /// <summary>FileMetaData 中 num_rows 字段的编号</summary>
        private const int NUM_ROWS_FIELD_ID = 3;

        // thrift compact 协议的类型编号
        private const byte TYPE_STOP = 0;
        private const byte TYPE_BOOLEAN_TRUE = 1;
        private const byte TYPE_BOOLEAN_FALSE = 2;
        private const byte TYPE_BYTE = 3;
        private const byte TYPE_I16 = 4;
        private const byte TYPE_I32 = 5;
        private const byte TYPE_I64 = 6;
        private const byte TYPE_DOUBLE = 7;
        private const byte TYPE_BINARY = 8;
        private const byte TYPE_LIST = 9;
        private const byte TYPE_SET = 10;
        private const byte TYPE_MAP = 11;
        private const byte TYPE_STRUCT = 12;

        private static ReadOnlySpan<byte> ParquetMagic => "PAR1"u8;

        /// <summary>
        /// 读取片段的行数。
        /// </summary>
        /// <param name="fileHandle">底层文件句柄</param>
        /// <param name="segmentOffset">片段在文件中的起始偏移</param>
        /// <param name="segmentLength">片段长度</param>
        /// <returns>片段的行数</returns>
        /// <exception cref="ParquetEngineException">footer 结构不符合预期时抛出，调用方应回退到完整解析</exception>
        public static long ReadSegmentRowCount(SafeFileHandle fileHandle, long segmentOffset, long segmentLength)
        {
            if (segmentLength < FOOTER_TAIL_LENGTH)
            {
                throw new ParquetEngineException("片段长度不足以容纳 footer 尾部结构");
            }

            Span<byte> tail = stackalloc byte[FOOTER_TAIL_LENGTH];
            ReadExactly(fileHandle, tail, segmentOffset + segmentLength - FOOTER_TAIL_LENGTH);

            if (!tail[4..].SequenceEqual(ParquetMagic))
            {
                throw new ParquetEngineException("片段尾部没有 parquet 魔数");
            }

            int footerLength = BinaryPrimitives.ReadInt32LittleEndian(tail[..4]);
            if (footerLength <= 0 || footerLength > segmentLength - FOOTER_TAIL_LENGTH)
            {
                throw new ParquetEngineException($"片段 footer 长度 {footerLength} 非法");
            }

            // footer 的体积与片段数量成正比，用共享池避免数千次大数组分配
            byte[] footer = ArrayPool<byte>.Shared.Rent(footerLength);
            try
            {
                ReadExactly(fileHandle, footer.AsSpan(0, footerLength), segmentOffset + segmentLength - FOOTER_TAIL_LENGTH - footerLength);
                return ParseRowCount(footer.AsSpan(0, footerLength));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(footer);
            }
        }

        /// <summary>
        /// 解析 FileMetaData 开头部分，取出行数。
        /// </summary>
        /// <param name="footer">完整的 footer 字节</param>
        /// <returns>行数</returns>
        private static long ParseRowCount(ReadOnlySpan<byte> footer)
        {
            int position = 0;
            int lastFieldId = 0;
            bool hasVersion = false;
            bool hasRowCount = false;
            long rowCount = 0;

            while (true)
            {
                byte header = ReadByte(footer, ref position);
                if (header == TYPE_STOP)
                {
                    break;
                }

                int fieldIdDelta = header >> 4;
                byte type = (byte)(header & 0x0F);
                if (fieldIdDelta == 0)
                {
                    // 字段编号差值超过 15 时使用长格式，字段编号单独编码
                    lastFieldId = checked((int)ReadZigZag(footer, ref position));
                }
                else
                {
                    lastFieldId += fieldIdDelta;
                }

                if (lastFieldId == VERSION_FIELD_ID)
                {
                    if (type != TYPE_I32)
                    {
                        throw new ParquetEngineException($"FileMetaData.version 的类型 {type} 不是 i32");
                    }

                    // 不同写入器写的版本号不同（pyarrow 写 2、parquet-mr 写 1），这里只要求字段存在且类型正确
                    ReadZigZag(footer, ref position);
                    hasVersion = true;
                    continue;
                }

                if (lastFieldId == NUM_ROWS_FIELD_ID)
                {
                    if (type != TYPE_I64)
                    {
                        throw new ParquetEngineException($"FileMetaData.num_rows 的类型 {type} 不是 i64");
                    }

                    rowCount = ReadZigZag(footer, ref position);
                    hasRowCount = true;

                    // 行数之后只剩 schema 之外的元数据，无需继续解析
                    break;
                }

                SkipValue(footer, type, ref position);
            }

            if (!hasVersion || !hasRowCount || rowCount < 0)
            {
                throw new ParquetEngineException("FileMetaData 中缺少 version 或 num_rows");
            }

            return rowCount;
        }

        /// <summary>
        /// 按类型跳过字段值。
        /// </summary>
        /// <param name="buffer">footer 字节</param>
        /// <param name="type">thrift compact 类型</param>
        /// <param name="position">当前读取位置</param>
        /// <remarks>结构字段中的布尔值由类型本身表示，不额外占用字节。</remarks>
        private static void SkipValue(ReadOnlySpan<byte> buffer, byte type, ref int position)
        {
            switch (type)
            {
                case TYPE_BOOLEAN_TRUE:
                case TYPE_BOOLEAN_FALSE:
                    return;
                case TYPE_BYTE:
                    SkipBytes(buffer, ref position, sizeof(byte));
                    return;
                case TYPE_I16:
                case TYPE_I32:
                case TYPE_I64:
                    ReadZigZag(buffer, ref position);
                    return;
                case TYPE_DOUBLE:
                    // compact 协议中小端序存储 8 字节浮点数
                    SkipBytes(buffer, ref position, sizeof(double));
                    return;
                case TYPE_BINARY:
                    SkipBytes(buffer, ref position, checked((int)ReadVarInt(buffer, ref position)));
                    return;
                case TYPE_LIST:
                case TYPE_SET:
                    SkipList(buffer, ref position);
                    return;
                case TYPE_MAP:
                    SkipMap(buffer, ref position);
                    return;
                case TYPE_STRUCT:
                    SkipStruct(buffer, ref position);
                    return;
                default:
                    throw new ParquetEngineException($"footer 中出现未知的 thrift compact 类型 {type}");
            }
        }

        /// <summary>
        /// 跳过整个结构体。
        /// </summary>
        /// <param name="buffer">footer 字节</param>
        /// <param name="position">当前读取位置</param>
        private static void SkipStruct(ReadOnlySpan<byte> buffer, ref int position)
        {
            while (true)
            {
                byte header = ReadByte(buffer, ref position);
                if (header == TYPE_STOP)
                {
                    return;
                }

                if ((header >> 4) == 0)
                {
                    // 长格式字段编号，编号本身不关心，读掉即可
                    ReadZigZag(buffer, ref position);
                }

                SkipValue(buffer, (byte)(header & 0x0F), ref position);
            }
        }

        /// <summary>
        /// 跳过列表或集合。
        /// </summary>
        /// <param name="buffer">footer 字节</param>
        /// <param name="position">当前读取位置</param>
        private static void SkipList(ReadOnlySpan<byte> buffer, ref int position)
        {
            byte header = ReadByte(buffer, ref position);
            int size = header >> 4;
            byte elementType = (byte)(header & 0x0F);
            if (size == 15)
            {
                // 元素数量达到 15 时改用 varint 编码
                size = checked((int)ReadVarInt(buffer, ref position));
            }

            for (int i = 0; i < size; i++)
            {
                SkipContainerValue(buffer, elementType, ref position);
            }
        }

        /// <summary>
        /// 跳过映射。
        /// </summary>
        /// <param name="buffer">footer 字节</param>
        /// <param name="position">当前读取位置</param>
        private static void SkipMap(ReadOnlySpan<byte> buffer, ref int position)
        {
            int size = checked((int)ReadVarInt(buffer, ref position));
            if (size <= 0)
            {
                return;
            }

            byte header = ReadByte(buffer, ref position);
            byte keyType = (byte)(header >> 4);
            byte valueType = (byte)(header & 0x0F);
            for (int i = 0; i < size; i++)
            {
                SkipContainerValue(buffer, keyType, ref position);
                SkipContainerValue(buffer, valueType, ref position);
            }
        }

        /// <summary>
        /// 跳过容器中的元素。
        /// </summary>
        /// <param name="buffer">footer 字节</param>
        /// <param name="type">元素类型</param>
        /// <param name="position">当前读取位置</param>
        /// <remarks>容器中的布尔元素每个额外占用 1 字节，与结构字段中的布尔值编码不同。</remarks>
        private static void SkipContainerValue(ReadOnlySpan<byte> buffer, byte type, ref int position)
        {
            if (type == TYPE_BOOLEAN_TRUE || type == TYPE_BOOLEAN_FALSE)
            {
                SkipBytes(buffer, ref position, sizeof(byte));
                return;
            }

            SkipValue(buffer, type, ref position);
        }

        /// <summary>
        /// 读取一个无符号 varint。
        /// </summary>
        /// <param name="buffer">footer 字节</param>
        /// <param name="position">当前读取位置</param>
        /// <returns>读到的无符号整数</returns>
        private static ulong ReadVarInt(ReadOnlySpan<byte> buffer, ref int position)
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                byte value = ReadByte(buffer, ref position);
                result |= (ulong)(value & 0x7F) << shift;
                if ((value & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
                if (shift > 63)
                {
                    throw new ParquetEngineException("footer 中的 varint 过长");
                }
            }
        }

        /// <summary>
        /// 读取一个 zigzag 编码的有符号整数。
        /// </summary>
        /// <param name="buffer">footer 字节</param>
        /// <param name="position">当前读取位置</param>
        /// <returns>读到的有符号整数</returns>
        private static long ReadZigZag(ReadOnlySpan<byte> buffer, ref int position)
        {
            ulong value = ReadVarInt(buffer, ref position);
            return (long)(value >> 1) ^ -(long)(value & 1);
        }

        private static byte ReadByte(ReadOnlySpan<byte> buffer, ref int position)
        {
            if (position >= buffer.Length)
            {
                throw new ParquetEngineException("footer 数据提前结束");
            }

            return buffer[position++];
        }

        private static void SkipBytes(ReadOnlySpan<byte> buffer, ref int position, int length)
        {
            if (length < 0 || position > buffer.Length - length)
            {
                throw new ParquetEngineException("footer 数据提前结束");
            }

            position += length;
        }

        /// <summary>
        /// 从指定偏移读满整个缓冲区。
        /// </summary>
        /// <param name="fileHandle">底层文件句柄</param>
        /// <param name="buffer">目标缓冲区</param>
        /// <param name="offset">起始偏移</param>
        private static void ReadExactly(SafeFileHandle fileHandle, Span<byte> buffer, long offset)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = RandomAccess.Read(fileHandle, buffer[totalRead..], offset + totalRead);
                if (read <= 0)
                {
                    throw new ParquetEngineException("读取片段 footer 时提前遇到文件末尾");
                }

                totalRead += read;
            }
        }
    }
}