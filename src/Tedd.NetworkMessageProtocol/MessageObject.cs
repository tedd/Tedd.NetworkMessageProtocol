using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Tedd;



namespace Tedd.NetworkMessageProtocol
{
    public class MessageObject
    {
        private readonly byte[] _buffer;
        public readonly MemoryStreamer Stream;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="maxSize">Maximum packet size. 5 bytes is used for header.</param>
        public MessageObject(int maxSize)
        {
            _buffer = new byte[maxSize];
            // User writable memory starts after header size
            Stream = new MemoryStreamer(new Memory<byte>(_buffer, 4, _buffer.Length - 5));
        }

        /// <summary>
        /// Uses Stream.Length to determine size of buffer.
        /// Writes variable size (WriteSize) in front and returns ArraySegment.
        /// </summary>
        /// <returns>Packet contents including header</returns>
        internal ArraySegment<byte> GetPacketArraySegment()
        {
            var length = Stream.Length;
            var size = Utils.MeasureWriteSize((uint)length);
            var start = 4 - size;
            new Span<byte>(_buffer, start, size).WriteSize((uint)length);
            return new ArraySegment<byte>(_buffer, start, (int)length + size);
        }
        internal ArraySegment<byte> GetPacketArraySegment(int start, int length)
        {
            return new ArraySegment<byte>(_buffer, start, (int)length);
        }
    }

}
