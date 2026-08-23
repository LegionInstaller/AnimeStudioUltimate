using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AnimeStudio
{
    public class EndianBinaryReader : BinaryReader
    {
        private readonly byte[] buffer;

        public EndianType Endian;

        public EndianBinaryReader(Stream stream, EndianType endian = EndianType.BigEndian, bool leaveOpen = false) : base(stream, Encoding.UTF8, leaveOpen)
        {
            Endian = endian;
            buffer = new byte[8];
        }

        public long Position
        {
            get => BaseStream.Position;
            set => BaseStream.Position = value;
        }

        // Length is read constantly (every ReadBytes, ReadAlignedString and every byte of
        // ReadStringToNull consults Remaining). On a FileStream opened with FileShare.ReadWrite
        // the runtime may not cache the file length, so each call becomes a
        // GetFileInformationByHandleEx syscall -- 9.5% of load CPU on real ZZZ blocks. A stream
        // we cannot write to cannot change length underneath us, so cache it for those only.
        private long cachedLength = -1;

        public long Length
        {
            get
            {
                if (cachedLength >= 0)
                    return cachedLength;
                var length = BaseStream.Length;
                if (!BaseStream.CanWrite)
                    cachedLength = length;
                return length;
            }
        }
        public long Remaining => Length - Position;

        public override short ReadInt16()
        {
            if (Endian == EndianType.BigEndian)
            {
                Read(buffer, 0, 2);
                return BinaryPrimitives.ReadInt16BigEndian(buffer);
            }
            return base.ReadInt16();
        }

        public override int ReadInt32()
        {
            if (Endian == EndianType.BigEndian)
            {
                Read(buffer, 0, 4);
                return BinaryPrimitives.ReadInt32BigEndian(buffer);
            }
            return base.ReadInt32();
        }

        public override long ReadInt64()
        {
            if (Endian == EndianType.BigEndian)
            {
                Read(buffer, 0, 8);
                return BinaryPrimitives.ReadInt64BigEndian(buffer);
            }
            return base.ReadInt64();
        }

        public override ushort ReadUInt16()
        {
            if (Endian == EndianType.BigEndian)
            {
                Read(buffer, 0, 2);
                return BinaryPrimitives.ReadUInt16BigEndian(buffer);
            }
            return base.ReadUInt16();
        }

        public override uint ReadUInt32()
        {
            if (Endian == EndianType.BigEndian)
            {
                Read(buffer, 0, 4);
                return BinaryPrimitives.ReadUInt32BigEndian(buffer);
            }
            return base.ReadUInt32();
        }

        public override ulong ReadUInt64()
        {
            if (Endian == EndianType.BigEndian)
            {
                Read(buffer, 0, 8);
                return BinaryPrimitives.ReadUInt64BigEndian(buffer);
            }
            return base.ReadUInt64();
        }

        public override float ReadSingle()
        {
            if (Endian == EndianType.BigEndian)
            {
                Read(buffer, 0, 4);
                Array.Reverse(buffer, 0, 4);
                return BitConverter.ToSingle(buffer, 0);
            }
            return base.ReadSingle();
        }

        public override double ReadDouble()
        {
            if (Endian == EndianType.BigEndian)
            {
                Read(buffer, 0, 8);
                Array.Reverse(buffer);
                return BitConverter.ToDouble(buffer, 0);
            }
            return base.ReadDouble();
        }
        public override byte[] ReadBytes(int count)
        {
            if (count == 0)
            {
                return Array.Empty<byte>();
            }
            // A negative or oversized count always means the stream position is wrong (bad
            // length field, misaligned object). Fail here with the offset instead of deeper
            // down in `new byte[negative]` (OverflowException) or a silent short read.
            if (count < 0 || count > Remaining)
            {
                throw new EndOfStreamException($"Invalid read of {count} bytes at position 0x{Position:X} ({Position}), only {Remaining} bytes remaining (stream length {Length})");
            }

            // Single destination buffer — avoids List growth + final ToArray copy that
            // previously doubled peak memory for large reads (object hashing, etc.).
            var result = new byte[count];
            int offset = 0;
            int remaining = count;
            while (remaining > 0)
            {
                int n = Read(result, offset, remaining);
                if (n == 0)
                {
                    if (offset == 0)
                    {
                        return Array.Empty<byte>();
                    }
                    if (offset < count)
                    {
                        Array.Resize(ref result, offset);
                    }
                    break;
                }
                offset += n;
                remaining -= n;
            }
            return result;
        }

        public void AlignStream()
        {
            AlignStream(4);
        }

        public void AlignStream(int alignment)
        {
            var pos = Position;
            var mod = pos % alignment;
            if (mod != 0)
            {
                Position += alignment - mod;
            }
        }

        public string ReadAlignedString()
        {
            var result = "";
            var length = ReadInt32();
            if (length > 0 && length <= Remaining)
            {
                var stringData = ReadBytes(length);
                result = Encoding.UTF8.GetString(stringData);
            }
            AlignStream();
            return result;
        }

        public string ReadStringToNull(int maxLength = 32767)
        {
            var bytes = new List<byte>();
            int count = 0;
            while (Remaining > 0 && count < maxLength)
            {
                var b = ReadByte();
                if (b == 0)
                {
                    break;
                }
                bytes.Add(b);
                count++;
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public Quaternion ReadQuaternion()
        {
            return new Quaternion(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
        }

        public Vector2 ReadVector2()
        {
            return new Vector2(ReadSingle(), ReadSingle());
        }

        public Vector4 ReadVector4()
        {
            return new Vector4(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
        }

        public Color ReadColor4()
        {
            return new Color(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
        }

        public Matrix4x4 ReadMatrix()
        {
            return new Matrix4x4(ReadSingleArray(16));
        }

        public Float ReadFloat()
        {
            return new Float(ReadSingle());
        }

        public int ReadMhyInt()
        {
            var buffer = ReadBytes(6);
            return buffer[2] | (buffer[4] << 8) | (buffer[0] << 0x10) | (buffer[5] << 0x18);
        }

        public uint ReadMhyUInt()
        {
            var buffer = ReadBytes(7);
            return (uint)(buffer[1] | (buffer[6] << 8) | (buffer[3] << 0x10) | (buffer[2] << 0x18));
        }

        public string ReadMhyString()
        {
            var pos = BaseStream.Position;
            var str = ReadStringToNull();
            BaseStream.Position += 0x105 - (BaseStream.Position - pos);
            return str;
        }


        /// <summary>
        /// Up-front capacity for a collection whose element count was just read from the file.
        /// Growing such lists from zero was 9% of load CPU (repeated Array.Resize) and doubled
        /// their transient memory. A corrupt count must not become a huge allocation, so it is
        /// clamped to the bytes left in the stream -- no element can occupy less than one byte.
        /// </summary>
        public int Capacity(int count)
        {
            if (count <= 0)
                return 0;
            var remaining = Remaining;
            return remaining < count ? (int)Math.Max(0, remaining) : count;
        }
        internal T[] ReadArray<T>(Func<T> del, int length)
        {
            // Same reasoning as ReadBytes: every element needs at least one byte, so a length
            // above the remaining stream size (or a negative one) is a bad offset, not data.
            if (length < 0 || length > Remaining)
            {
                throw new EndOfStreamException($"Invalid array length {length} at position 0x{Position:X} ({Position}), only {Remaining} bytes remaining (stream length {Length})");
            }
            // The length is validated above, so one exact-size array is safe for any size. The
            // previous large-array path filled a List<T> and then called ToArray(), allocating
            // the payload twice and copying it -- straight onto the LOH for big meshes.
            var array = new T[length];
            for (int i = 0; i < length; i++)
            {
                array[i] = del();
            }
            return array;
        }

        public bool[] ReadBooleanArray(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            return ReadArray(ReadBoolean, length);
        }

        public byte[] ReadUInt8Array(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            return ReadBytes(length);
        }

        public short[] ReadInt16Array(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            return ReadArray(ReadInt16, length);
        }

        public ushort[] ReadUInt16Array(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            return ReadArray(ReadUInt16, length);
        }

        public int[] ReadInt32Array(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            return ReadArray(ReadInt32, length);
        }

        public uint[] ReadUInt32Array(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            return ReadArray(ReadUInt32, length);
        }

        public ulong[] ReadUInt64Array(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            return ReadArray(ReadUInt64, length);
        }

        public uint[][] ReadUInt32ArrayArray(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            return ReadArray(() => ReadUInt32Array(), length);
        }

        public float[] ReadSingleArray(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            return ReadArray(ReadSingle, length);
        }

        public string[] ReadStringArray(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            return ReadArray(ReadAlignedString, length);
        }

        public Vector2[] ReadVector2Array(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            return ReadArray(ReadVector2, length);
        }

        public Vector4[] ReadVector4Array(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            return ReadArray(ReadVector4, length);
        }

        public Matrix4x4[] ReadMatrixArray(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            return ReadArray(ReadMatrix, length);
        }
    }
}
