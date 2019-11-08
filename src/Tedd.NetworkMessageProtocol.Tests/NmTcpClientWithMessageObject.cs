using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tedd.NetworkMessageProtocol.Tests
{
    public class NmTcpClientWithMessageObject
    {
        private Random _random = new Random();
        private Logger<NPTcpClientServerTests> _logger = new Logger<NPTcpClientServerTests>(new NullLoggerFactory());

        [Fact]
        public void MessageObjectSend()
        {
            const int port = 11234;
            using (var server = new NmpTcpServer(_logger))
            {
                var listenTask = server.Listen(port);
                Thread.Sleep(100); // Listen socket needs time to start
                NmpTcpClient sClient = null;
                server.NewConnection += (tcpServer, client) => sClient = client;

                var client = new NmpTcpClient(_logger);
                client.ConnectAsync("127.0.0.1", port).Wait();
                var clientTask = client.ReadPacketsAsync();

                Assert.True(WaitFor(1000, () => sClient != null));
                var sClientTasks = sClient.ReadPacketsAsync();

                MessageObject smo = null;
                MessageObject cmo = null;
                sClient.MessageObjectReceived += (NmpTcpClient client, MessageObject messageObject, ref bool free) => { smo = messageObject; };
                client.MessageObjectReceived += (NmpTcpClient client, MessageObject messageObject, ref bool free) => { cmo = messageObject; };


                client.SendAsync(3, o => o.Write("Hello")).Wait();
                Assert.True(WaitFor(1000, () => smo != null));
                Assert.Equal(3, smo.MessageType);
                Assert.Equal("Hello", smo.ReadString());

            }
        }

        private Boolean WaitFor(Int32 i, Func<Boolean> func)
        {
            var sw = new Stopwatch();
            sw.Start();
            for (; ; )
            {
                Thread.Sleep(10);
                if (sw.ElapsedMilliseconds > i)
                    return false;
                if (func())
                    return true;
            }
        }

    }
}