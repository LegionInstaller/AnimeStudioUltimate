using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AnimeStudio
{
    public class OffsetStream : Stream
    {
        private readonly Stream _baseStream;
        private long _offset;

        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => _baseStream.CanSeek;
        public override bool CanWrite => false;

        // Same reason as EndianBinaryReader.Length: the underlying .blk FileStream is opened
        // with FileShare.ReadWrite, so every Length read is a syscall. Offset, Seek and Length
        // all consult it, which means one per seek and one per bounds check.
        private long cachedBaseLength = -1;

        private long BaseLength
        {
            get
            {
                if (cachedBaseLength >= 0)
                    return cachedBaseLength;
                var length = _baseStream.Length;
                if (!_baseStream.CanWrite)
                    cachedBaseLength = length;
                return length;
            }
        }

        public long Offset
        {
            get => _offset;
            set
            {
                if (value < 0 || value > BaseLength)
                {
                    throw new IOException($"{nameof(Offset)} is out of stream bound");
                }
                _offset = value;
                Seek(0, SeekOrigin.Begin);
            }
        }
        public long AbsolutePosition => _baseStream.Position;
        public long Remaining => Length - Position;

        public override long Length => BaseLength - _offset;
        public override long Position
        {
            get => _baseStream.Position - _offset;
            set => Seek(value, SeekOrigin.Begin);
        }

        public OffsetStream(Stream stream, long offset)
        {
            _baseStream = stream;

            Offset = offset;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (offset > BaseLength)
            {
                throw new IOException("Unable to seek beyond stream bound");
            }

            var target = origin switch
            {
                SeekOrigin.Begin => offset + _offset,
                SeekOrigin.Current => offset + Position,
                SeekOrigin.End => offset + BaseLength,
                _ => throw new NotSupportedException()
            };

            _baseStream.Seek(target, SeekOrigin.Begin);
            return Position;
        }
        public override int Read(byte[] buffer, int offset, int count) => _baseStream.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
        public override void SetLength(long value) => throw new NotImplementedException();
        public override void Flush() => throw new NotImplementedException();
        public IEnumerable<long> GetOffsets(string path)
        {
            if (AssetsHelper.TryGet(path, out var offsets))
            {
                foreach (var offset in offsets)
                {
                    Offset = offset;
                    yield return offset;
                }
            }
            else
            {
                while (Remaining > 0)
                {
                    Offset = AbsolutePosition;
                    yield return AbsolutePosition;
                    if (Offset == AbsolutePosition)
                    {
                        break;
                    }
                }
            }
        }
    }
}
