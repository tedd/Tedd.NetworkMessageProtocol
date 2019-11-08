using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tedd.NetworkMessageProtocol
{
    public class MessageObject
    {
        private readonly byte[] _data;
        private readonly Memory<byte> _dataRaw;
        private Int32 _pos = 0;
        private Int32 _size = 0;
        private Int32 _rawPos = 0;

        public MessageObject()
        {
            _data = new byte[Constants.MaxPacketSize];
            _dataRaw = new Memory<Byte>(_data);
            SkipHeader();
        }

        public void Reset()
        {
            //Array.Clear(_data, 0, (int)_size);
            Array.Fill<byte>(_data, 0);
            _rawPos = 0;
            _pos = 0;
            _size = 0;
            SkipHeader();
        }

        public void SkipHeader()
        {
            // Ensure we are past header
            if (_pos < Constants.MaxPacketHeaderSize)
                _pos = Constants.MaxPacketHeaderSize;
            // Ensure size is at least header
            if (_size < Constants.MaxPacketHeaderSize)
                _size = Constants.MaxPacketHeaderSize;
        }

        public ReadOnlyMemory<byte> GetPacketMemory()
        {
            // Update size
            _dataRaw.Span[0] = (byte)(_size & 0xFF);
            _dataRaw.Span[1] = (byte)((_size >> 8 * 1) & 0xFF);
            _dataRaw.Span[2] = (byte)((_size >> 8 * 2) & 0xFF);
            // Return
            return new ReadOnlyMemory<Byte>(_data, 0, (int)_size);
        }

        /// <summary>
        /// Set message type for MessageObject. This is a byte that can be used to determine what the MessageObject contains.
        /// </summary>
        public byte MessageType
        {
            get => _dataRaw.Span[3];
            set => _dataRaw.Span[3] = value;
        }

        /// <summary>
        /// Size of MessageObject according to data located in header
        /// </summary>
        public Int32 PacketSizeAccordingToHeader
        {
            get => (Int32)(_dataRaw.Span[0] | _dataRaw.Span[1] << 8 * 1 | _dataRaw.Span[2] << 8 * 2);
        }

        public void RawSyncFromHeader()
        {
            _size = PacketSizeAccordingToHeader;
        }


        /// <summary>
        /// Current position
        /// </summary>
        public Int32 Position
        {
            get => _pos - Constants.MaxPacketHeaderSize;
        }
        /// <summary>
        /// Current raw position
        /// </summary>
        public Int32 RawPosition
        {
            get => _pos;
        }

        /// <summary>
        /// Size of MessageObject payload
        /// </summary>
        public Int32 Size
        {
            get => _size - Constants.MaxPacketHeaderSize;
        }

        /// <summary>
        /// Size of whole MessageObject
        /// </summary>
        public Int32 RawSize
        {
            get => _size;
        }
        /// <summary>
        /// Determine if object has header. Used 
        /// </summary>
        internal bool HasHeader
        {
            get => _size >= Constants.MaxPacketHeaderSize && PacketSizeAccordingToHeader >= Constants.MaxPacketHeaderSize;
        }

        #region Seek
        /// <summary>
        /// Seek to a position in payload of MessageObject
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="origin"></param>
        public void Seek(int pos, SeekOrigin origin)
        {
            int nPos = _pos - Constants.MaxPacketHeaderSize;
            var nSize = _size - Constants.MaxPacketHeaderSize;
            if (origin == SeekOrigin.Begin)
                nPos = 0;
            else if (origin == SeekOrigin.Current)
                nPos = _pos - Constants.MaxPacketHeaderSize;
            else if (origin == SeekOrigin.End)
                nPos = (Int32)(_size - Constants.MaxPacketHeaderSize - 1);

            nPos = nPos + pos;

            if ((nPos >= nSize && !(nPos == 0 && nSize == 0)) || nPos < 0)
                throw new IndexOutOfRangeException($"New position {nPos} outside of 0-{nSize - 1}.");

            _pos = (Int32)nPos + Constants.MaxPacketHeaderSize;
        }
        /// <summary>
        /// Seek to a position in payload of MessageObject
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="origin"></param>
        public void RawSeek(int pos, SeekOrigin origin)
        {
            int nPos = _rawPos;
            var nSize = _size;
            if (origin == SeekOrigin.Begin)
                nPos = 0;
            else if (origin == SeekOrigin.Current)
                nPos = _rawPos;
            else if (origin == SeekOrigin.End)
                nPos = (Int32)(_size - 1);

            nPos = nPos + pos;

            if ((nPos >= nSize && !(nPos == 0 && nSize == 0)) || nPos < 0)
                throw new IndexOutOfRangeException($"New position {nPos} outside of 0-{nSize - 1}.");

            _rawPos = (Int32)nPos;
        }
        #endregion

        #region CheckOverflow
        private void CheckWriteOverflow(int i, bool updateSize = true)
        {
            if (_pos + i > Constants.MaxPacketSize)
                throw new IndexOutOfRangeException("Packet size would overflow, write prohibited.");

            // Increase size if _pos pushes past size
            if (updateSize)
                _size = (Int32)Math.Max(_size, _pos + i);
        }
        private void RawCheckWriteOverflow(Int32 i)
        {
            if (_rawPos + i > Constants.MaxPacketSize)
                throw new IndexOutOfRangeException("Packet size would overflow, write prohibited.");

            // Increase size if _pos pushes past size
            _size = (Int32)Math.Max(_size, _rawPos + i);
        }

        private void CheckReadOverflow(Int32 i)
        {
            if (_pos + i > _size)
                throw new IndexOutOfRangeException("Read would go past packet size, prohibited.");
        }
        #endregion

        #region Write raw
        public void RawWrite(byte[] b, int offset, Int32 length)
        {
            RawCheckWriteOverflow(b.Length);
            Buffer.BlockCopy(b, offset, _data, _rawPos, length);
            _rawPos += length;
        }

        public void RawWrite(in ReadOnlySequence<Byte> b)
        {
            var len = (Int32)b.Length;
            RawCheckWriteOverflow(len);
            b.CopyTo(_dataRaw.Span.Slice(_rawPos, len));
            _rawPos += len;
        }

        public void RawWrite(in Memory<Byte> b)
        {
            var len = (Int32)b.Length;
            RawCheckWriteOverflow(len);
            b.Span.CopyTo(_dataRaw.Span.Slice(_rawPos, len));
            _rawPos += len;
        }

        #endregion

        #region Write datatypes

        public void Write(byte[] b, int offset, Int32 length)
        {
            CheckWriteOverflow(b.Length);
            Buffer.BlockCopy(b, offset, _data, _pos, length);
            _pos += length;
        }

        public void Write(in ReadOnlySequence<Byte> b)
        {
            var len = (Int32)b.Length;
            CheckWriteOverflow(len);
            b.CopyTo(_dataRaw.Span.Slice(_pos, len));
            _pos += len;
        }

        public void Write(in Memory<Byte> b)
        {
            var len = (Int32)b.Length;
            CheckWriteOverflow(len);
            b.Span.CopyTo(_dataRaw.Span.Slice(_pos, len));
            _pos += len;
        }

        public void Write(string text)
        {
            var b = Encoding.UTF8.GetBytes(text);
            CheckWriteOverflow(b.Length + sizeof(UInt16), false);
            Write((UInt16)b.Length);
            Write(b);
        }

        public void Write(byte b)
        {
            CheckWriteOverflow(sizeof(byte));

            _dataRaw.Span[_pos++] = b;
        }

        public void Write(Int16 i)
        {
            CheckWriteOverflow(sizeof(Int16));

            _dataRaw.Span[_pos++] = (byte)(i & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 1 & 0xFF);
        }

        public void WriteInt24(Int32 i)
        {
            CheckWriteOverflow(24);

            _dataRaw.Span[_pos++] = (byte)(i & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 1 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 2 & 0xFF);
        }

        public void WriteUInt24(UInt32 i)
        {
            CheckWriteOverflow(24);

            _dataRaw.Span[_pos++] = (byte)(i & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 1 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 2 & 0xFF);
        }

        public void Write(UInt16 i)
        {
            CheckWriteOverflow(sizeof(UInt16));

            _dataRaw.Span[_pos++] = (byte)(i & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 1 & 0xFF);
        }

        public void Write(Int32 i)
        {
            CheckWriteOverflow(sizeof(Int32));

            _dataRaw.Span[_pos++] = (byte)(i & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 1 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 2 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 3 & 0xFF);
        }

        public void Write(UInt32 i)
        {
            CheckWriteOverflow(sizeof(UInt32));

            _dataRaw.Span[_pos++] = (byte)(i & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 1 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 2 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 3 & 0xFF);
        }


        public void Write(Int64 i)
        {
            CheckWriteOverflow(sizeof(Int64));

            _dataRaw.Span[_pos++] = (byte)(i & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 1 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 2 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 3 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 4 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 5 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 6 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 7 & 0xFF);
        }

        public void Write(UInt64 i)
        {
            CheckWriteOverflow(sizeof(UInt64));

            _dataRaw.Span[_pos++] = (byte)(i & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 1 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 2 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 3 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 4 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 5 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 6 & 0xFF);
            _dataRaw.Span[_pos++] = (byte)(i >> 8 * 7 & 0xFF);
        }

        public void Write(Single f)
        {
            CheckWriteOverflow(sizeof(Single));
            var bytes = BitConverter.GetBytes(f);
            _dataRaw.Span[_pos++] = bytes[0];
            _dataRaw.Span[_pos++] = bytes[1];
            _dataRaw.Span[_pos++] = bytes[2];
            _dataRaw.Span[_pos++] = bytes[3];
            //            fixed (Byte* b = _dataBody.Span)
            //            {
            //                var bb = *(b + _pos);
            //                _pos += sizeof(Single);
            //                *((Single*) bb) = *(Single*) &f;
            //            }
        }


        public void Write(Double d)
        {
            CheckWriteOverflow(sizeof(Double));

            var bytes = BitConverter.GetBytes(d);
            _dataRaw.Span[_pos++] = bytes[0];
            _dataRaw.Span[_pos++] = bytes[1];
            _dataRaw.Span[_pos++] = bytes[2];
            _dataRaw.Span[_pos++] = bytes[3];
            _dataRaw.Span[_pos++] = bytes[4];
            _dataRaw.Span[_pos++] = bytes[5];
            _dataRaw.Span[_pos++] = bytes[6];
            _dataRaw.Span[_pos++] = bytes[7];

            //            fixed (Byte* b = _dataRaw.Span)
            //            {
            //                var bb = *(b + _pos);
            //                _pos += sizeof(Double);
            //                *((Double*) bb) = *(Double*) &f;
            //            }
        }
        #endregion

        #region Read datatypes
        public void ReadBytes(byte[] buffer, int offset, int length)
        {
            CheckReadOverflow(length);

            for (var i = offset; i < offset + length; i++)
                buffer[i] = _dataRaw.Span[_pos++];
        }

        public byte ReadByte()
        {
            CheckReadOverflow(sizeof(byte));

            return _dataRaw.Span[_pos++];
        }

        public Int16 ReadInt16()
        {
            CheckReadOverflow(sizeof(Int16));

            return (Int16)(_dataRaw.Span[_pos++] | _dataRaw.Span[_pos++] << 8 * 1);
        }

        public UInt16 ReadUInt16()
        {
            CheckReadOverflow(sizeof(UInt16));

            return (UInt16)(_dataRaw.Span[_pos++] | _dataRaw.Span[_pos++] << 8 * 1);
        }

        public Int32 ReadInt24()
        {
            CheckReadOverflow(24);

            return (Int32)((Int32)_dataRaw.Span[_pos++]
                            | (Int32)_dataRaw.Span[_pos++] << 8 * 1
                            | (Int32)_dataRaw.Span[_pos++] << 8 * 2
                );
        }

        public UInt32 ReadUInt24()
        {
            CheckReadOverflow(24);

            return (UInt32)((Int32)_dataRaw.Span[_pos++]
                             | (Int32)_dataRaw.Span[_pos++] << 8 * 1
                             | (Int32)_dataRaw.Span[_pos++] << 8 * 2
                );
        }

        public Int32 ReadInt32()
        {
            CheckReadOverflow(sizeof(Int32));

            return (Int32)((Int32)_dataRaw.Span[_pos++]
                            | (Int32)_dataRaw.Span[_pos++] << 8 * 1
                            | (Int32)_dataRaw.Span[_pos++] << 8 * 2
                            | (Int32)_dataRaw.Span[_pos++] << 8 * 3
                );
        }

        public UInt32 ReadUInt32()
        {
            CheckReadOverflow(sizeof(UInt32));

            return (UInt32)((Int32)_dataRaw.Span[_pos++]
                             | (Int32)_dataRaw.Span[_pos++] << 8 * 1
                             | (Int32)_dataRaw.Span[_pos++] << 8 * 2
                             | (Int32)_dataRaw.Span[_pos++] << 8 * 3
                );
        }

        public Int64 ReadInt64()
        {
            CheckReadOverflow(sizeof(Int64));


            return ((Int64)_dataRaw.Span[_pos++]
                    | (Int64)_dataRaw.Span[_pos++] << 8 * 1
                    | (Int64)_dataRaw.Span[_pos++] << 8 * 2
                    | (Int64)_dataRaw.Span[_pos++] << 8 * 3
                    | (Int64)_dataRaw.Span[_pos++] << 8 * 4
                    | (Int64)_dataRaw.Span[_pos++] << 8 * 5
                    | (Int64)_dataRaw.Span[_pos++] << 8 * 6
                    | (Int64)_dataRaw.Span[_pos++] << 8 * 7
                );
        }

        public UInt64 ReadUInt64()
        {
            CheckReadOverflow(sizeof(UInt64));


            return (UInt64)((Int64)_dataRaw.Span[_pos++]
                             | (Int64)_dataRaw.Span[_pos++] << 8 * 1
                             | (Int64)_dataRaw.Span[_pos++] << 8 * 2
                             | (Int64)_dataRaw.Span[_pos++] << 8 * 3
                             | (Int64)_dataRaw.Span[_pos++] << 8 * 4
                             | (Int64)_dataRaw.Span[_pos++] << 8 * 5
                             | (Int64)_dataRaw.Span[_pos++] << 8 * 6
                             | (Int64)_dataRaw.Span[_pos++] << 8 * 7
                );
        }

        public Single ReadFloat()
        {
            CheckReadOverflow(sizeof(Single));

            Single ret = BitConverter.ToSingle(_dataRaw.Span.Slice(_pos, sizeof(Single)));
            _pos += sizeof(Single);
            //            fixed (Byte* b = _dataRaw.Span)
            //            {
            //                var bb = *(b + _pos);
            //                _pos += sizeof(Single);
            //                *(Single*) &ret = *((Single*) bb);
            //            }

            return ret;
        }


        public Double ReadDouble()
        {
            CheckReadOverflow(sizeof(Double));

            Double ret = BitConverter.ToDouble(_dataRaw.Span.Slice(_pos, sizeof(Double)));
            _pos += sizeof(Double);

            //            fixed (Byte* b = _dataRaw.Span)
            //            {
            //                var bb = *(b + _pos);
            //                _pos += sizeof(Double);
            //                *(Double*) &ret = *((Double*) bb);
            //            }

            return ret;
        }

        public string ReadString()
        {
            var size = ReadUInt16();
            CheckReadOverflow(size);
            var buffer = new byte[size];
            ReadBytes(buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer);
        }
        #endregion
    }
}
