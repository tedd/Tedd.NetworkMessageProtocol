using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tedd.NetworkMessageProtocol.Tests
{
    public class ClientServerTest
    {
        private Logger<MessageObjectTests> _logger = new Logger<MessageObjectTests>(new NullLoggerFactory());

        [Fact]
        public async Task AllTest()
        {
            var port = 9011;
            var rnd = new Random();
            string strBack = null;
            using var server = new NmpTcpServer(_logger, 1_000_000);
            server.Listen(port);
            NmpTcpClient cc = null;
            server.ConnectionRequest += (tcpServer, request) => { };
            server.NewConnection += (tcpServer, client) =>
            {
                cc = client;
                cc.ProcessIncomingAsync();
                cc.MessageObjectReceived += (NmpTcpClient tcpClient, MessageObject messageObject, ref bool preventAutoRecycling) =>
                {
                    strBack = messageObject.Stream.SizedReadString(out var stringSize);
                    Assert.Equal(stringSize-1, strBack.Length);
                };
            };
            using var c = new NmpTcpClient(_logger, 1_000_000);
            c.MessageObjectReceived += (NmpTcpClient client, MessageObject messageObject, ref bool preventAutoRecycling) =>
            {

            };
            await c.Connect("127.0.0.1", port);
            c.ProcessIncomingAsync();
            await Task.Delay(1000);
            Assert.NotNull(cc);
            var str = "Test";
            await c.SendMessageObject(mo =>
            {
                mo.Stream.SizedWrite(in str);
            });
            await Task.Delay(100);
            Assert.Equal(str, strBack);

            cc.Dispose();
            server.Stop();
        }
    }
}