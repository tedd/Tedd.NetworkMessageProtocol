using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Tedd.NetworkMessageProtocol.Tests
{
    public class MessageObjectTests
    {
        private Random _random = new Random();

        [Fact]
        public void Seek()
        {
            var mo = new MessageObject();
            Assert.Throws<IndexOutOfRangeException>(() => mo.Seek(1, SeekOrigin.Begin));
            Assert.Throws<IndexOutOfRangeException>(() => mo.Seek(-1, SeekOrigin.End));
            mo.Write((Int32)0);
            mo.Seek(0, SeekOrigin.Begin);
            Assert.Equal(0, mo.Position);
            mo.Seek(2, SeekOrigin.Begin);
            Assert.Equal(2, mo.Position);
            mo.Seek(0, SeekOrigin.End);
            Assert.Equal(3, mo.Position);
            mo.Seek(-2, SeekOrigin.End);
            Assert.Equal(1, mo.Position);
            mo.Seek(-3, SeekOrigin.End);
            Assert.Equal(0, mo.Position);
            Assert.Throws<IndexOutOfRangeException>(() => mo.Seek(4, SeekOrigin.Begin));
            Assert.Throws<IndexOutOfRangeException>(() => mo.Seek(-4, SeekOrigin.End));
            Assert.Throws<IndexOutOfRangeException>(() => mo.Seek(-1, SeekOrigin.Begin));
            Assert.Throws<IndexOutOfRangeException>(() => mo.Seek(1, SeekOrigin.End));
            mo.Seek(0, SeekOrigin.Begin);
            Assert.Throws<IndexOutOfRangeException>(() => mo.Seek(-1, SeekOrigin.Current));
            mo.Seek(1, SeekOrigin.Current);
            mo.Seek(1, SeekOrigin.Current);
            mo.Seek(1, SeekOrigin.Current);
            Assert.Equal(3, mo.Position);
            Assert.Throws<IndexOutOfRangeException>(() => mo.Seek(1, SeekOrigin.Current));
            mo.Seek(-1, SeekOrigin.Current);
            mo.Seek(1, SeekOrigin.Current);
            Assert.Throws<IndexOutOfRangeException>(() => mo.Seek(1, SeekOrigin.Current));
        }

        [Fact]
        public void WriteRead()
        {
            var size = _random.Next(1, 100_000);
            var arr = new int[size];
            for (var i = 0; i < arr.Length; i++)
                arr[i] = _random.Next(Int32.MinValue, Int32.MaxValue);

            var mo = new MessageObject();

            // Write all integers
            for (var i = 0; i < arr.Length; i++)
                mo.Write(arr[i]);
            Assert.Equal(size * sizeof(Int32), mo.Size);

            // Read all integers
            mo.Seek(0, SeekOrigin.Begin);
            for (var i = 0; i < arr.Length; i++)
            {
                var r = mo.ReadInt32();
                Assert.True(arr[i] == r, $"arr[{i}] {arr[i]} == r {r}");
            }

            Assert.Throws<IndexOutOfRangeException>(() => _ = mo.ReadByte());
        }

        [Fact]
        public void WriteOverflow()
        {
            var mo = new MessageObject();

            // Fast write
            var max = (int)(Constants.MaxPacketBodySize / sizeof(Int64));
            for (var i = 0; i < max; i++)
                mo.Write((long)max);

            // Remainder
            var remainder = Constants.MaxPacketBodySize - (max * sizeof(Int64));
            for (var i = 0; i < remainder; i++)
                mo.Write((byte)i);

            // Overflow
            Assert.Throws<IndexOutOfRangeException>(() => mo.Write((byte)2));
        }


        [Fact]
        public void WriteReadAllTypes()
        {
            var mo = new MessageObject();
            var str = "JAllaBlergBlerg" + _random.NextDouble().ToString();
            //1
            mo.Write((byte)254);
            // Write 4080
            mo.Write((byte)0b11110000); // 240
            mo.Write((byte)0b00001111); // 15   
            mo.Write((byte)254);
            mo.Write(unchecked((Int32)0xAAAAAAAA)); // 4x170bytes
            mo.Write(unchecked((Int16)65432)); // 
            mo.Write(unchecked((Int16)43690)); // 2x170bytes
            mo.Write(unchecked((Int16)43690)); // 2x170bytes
            mo.Write(unchecked((Int16)43690)); // 2x170bytes
            mo.Write(unchecked((Int16)43690)); // 2x170bytes
            mo.Write((UInt32)5);
            //2
            mo.Write((Int64)0x0AAAAAAAAAAAAAAA);
            mo.Write((Single)1.2345f);
            mo.Write((UInt64)0x0AAAAAAAAAAAAAAA);
            mo.Write(str);
            mo.Write((Double)5.4321d);
            mo.WriteInt24(unchecked((Int32)0xFFFFFFFF));
            mo.WriteUInt24((UInt32)0xFFFFFFAA);


            mo.Seek(0, SeekOrigin.Begin);
            // 1
            Assert.Equal(254, mo.ReadByte());
            Assert.Equal(4080, mo.ReadInt16());
            Assert.Equal(254, mo.ReadByte());
            Assert.Equal(170, mo.ReadByte());
            Assert.Equal(170, mo.ReadByte());
            Assert.Equal(170, mo.ReadByte());
            Assert.Equal(170, mo.ReadByte());
            Assert.Equal(unchecked((Int16)65432), mo.ReadInt16());
            Assert.Equal(unchecked((Int32)0xAAAAAAAA), mo.ReadInt32());
            Assert.Equal(0xAAAAAAAA, mo.ReadUInt32());
            Assert.Equal((UInt32)5, mo.ReadUInt32());
            // 2
            Assert.Equal((Int64)0x0AAAAAAAAAAAAAAA, mo.ReadInt64());
            Assert.Equal((Single)1.2345f, mo.ReadFloat());
            Assert.Equal((UInt64)0x0AAAAAAAAAAAAAAA, mo.ReadUInt64());
            Assert.Equal(str, mo.ReadString());
            Assert.Equal((Double)5.4321d, mo.ReadDouble());
            Assert.Equal(unchecked((Int32)0x00FFFFFF), mo.ReadInt24());
            Assert.Equal(unchecked((UInt32)0x00FFFFAA), mo.ReadUInt24());
        }

        [Fact]
        public void Header()
        {
            var mo = new MessageObject();
            mo.SkipHeader();
            mo.MessageType = 10;
            Assert.Equal(10, mo.MessageType);
            mo.Write((Int32)1234);
            var memory = mo.GetPacketMemory();
            Assert.Equal(Constants.MaxPacketHeaderSize + 4, mo.PacketSizeAccordingToHeader);
            Assert.Equal(Constants.MaxPacketHeaderSize + 4, memory.Length);
            mo.Reset();
            Assert.Equal(0, mo.Size);
            Assert.False(mo.HasHeader);
            mo.SkipHeader();
            Assert.True(mo.HasHeader);
        }

        [Fact]
        public void WriteArrays()
        {
            var mo = new MessageObject();
            var bytes = new byte[] { 1, 2, 3, 4 };
            mo.Write(bytes, 0, (Int32)bytes.Length);
            Assert.Equal(4, mo.MessageType);
            Assert.Equal(197121, mo.PacketSizeAccordingToHeader);
            mo.Seek(0, SeekOrigin.Begin);
            Array.Clear(bytes, 0, bytes.Length);
            Assert.Equal(0, bytes[0]);
            mo.ReadBytes(bytes, 0, bytes.Length);
            Assert.Equal(1, bytes[0]);
            Assert.Equal(2, bytes[1]);
            Assert.Equal(3, bytes[2]);
            var bytes2 = new ReadOnlySequence<byte>(new byte[] { 4, 3, 2, 1 });
            mo.Seek(0, SeekOrigin.Begin);
            mo.Write(bytes2);
            Assert.Equal(1, mo.MessageType);
            Assert.Equal(131844, mo.PacketSizeAccordingToHeader);
        }
    }
}
