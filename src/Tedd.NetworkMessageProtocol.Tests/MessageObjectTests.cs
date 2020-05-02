using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tedd.NetworkMessageProtocol.Tests
{
    public class MessageObjectTests
    {
        private Logger<MessageObjectTests> _logger = new Logger<MessageObjectTests>(new NullLoggerFactory());

        [Fact]
        public void SizeTest()
        {
            var maxTestSize = 100;
            var rnd = new Random();
            var count = rnd.Next(1, 100);
            var mo = new MessageObject(maxTestSize * 4 + 4);
            var val = new int[count];
            for (var i = 0; i < count; i++)
            {
                val[i] = rnd.Next();
                mo.Stream.Write(val[i]);
            }

            Assert.Equal(4 * count, mo.Stream.Length);
            mo.Stream.Seek(0, SeekOrigin.Begin);
            for (var i = 0; i < count; i++)
            {
                Assert.Equal(val[i], mo.Stream.ReadInt32());
            }

            var arraySegment = mo.GetPacketArraySegment();
            Assert.Equal(4 * count + Utils.MeasureWriteSize((uint)(4 * count)), arraySegment.Count);
        }
    }
}