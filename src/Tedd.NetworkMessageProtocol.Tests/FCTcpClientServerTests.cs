using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tedd.NetworkMessageProtocol.Tests
{
    public class NPTcpClientServerTests
    {
        private Random _random = new Random();
        private Logger<NPTcpClientServerTests> _logger = new Logger<NPTcpClientServerTests>(new NullLoggerFactory());

        [Fact]
        public void ShutdownListener()
        {
            const int port = 1234;
            using (var server = new NmpTcpServer(_logger))
            {
                var listenTask = server.Listen(port);
                var listenTwiceTask = server.Listen(port);
                Assert.Throws<AggregateException>(() => listenTwiceTask.Wait());
                Assert.IsType<Exception>(listenTwiceTask.Exception.InnerExceptions.First());

                Assert.Equal(port, server.Port);

                server.Stop();
                var result = Task.WaitAll(new[] { listenTask }, 1000);
                Assert.True(result);
            }
        }

        private class ClientServer
        {
            public TcpClient Client;
            public NmpTcpClient ServerClient;
        }

        [Fact]
        public void ServerMultipleClients()
        {
            const int port = 1235;
            using (var server = new NmpTcpServer(_logger))
            {
                NmpTcpClient serverClient = null;
                server.NewConnection += (tcpServer, client) => { serverClient = client; };
                var listenTask = server.Listen(port);
                var clients = new List<ClientServer>();

                try
                {
                    for (var i = 0; i < 100; i++)
                    {
                        // Connect another client
                        var tcpClient = new TcpClient();
                        var clientServer = new ClientServer()
                        {
                            Client = tcpClient
                        };
                        clients.Add(clientServer);
                        tcpClient.Connect("127.0.0.1", port);

                        // We have to wait until TcpClient is actually connected
                        Assert.True(WaitFor(100, () => tcpClient.Connected));

                        // Wait for server to spawn off client object
                        Assert.True(WaitFor(100, () => serverClient != null));

                        clientServer.ServerClient = serverClient;
                        // Assert that we have connection
                        Assert.Equal("127.0.0.1", serverClient.RemoteIPEndPoint.Address.ToString());
                    }

                    var header = new byte[] { 8, 0, 0, 5 };
                    foreach (var cs in clients)
                    {
                        MessageObject mo = null;
                        cs.ServerClient.MessageObjectReceived += (NmpTcpClient client, MessageObject messageObject) => { mo = messageObject; };
                        // Fire off background tasks
                        var processPacketsTask = cs.ServerClient.ReadPacketsAsync();

                        // Write a packet to client
                        var stream = cs.Client.GetStream();
                        var bodyInt = _random.Next(Int32.MinValue, Int32.MaxValue);
                        var body = BitConverter.GetBytes(bodyInt);

                        stream.Write(header, 0, header.Length);
                        stream.Write(body, 0, body.Length);
                        stream.Flush();

                        // Wait for packet to come back from server client
                        Assert.True(WaitFor(1000, () => mo != null));

                        // Check packet
                        Assert.Equal(header.Length + body.Length, mo.Size);
                        Assert.Equal(header.Length + body.Length, mo.PacketSizeAccordingToHeader);
                        mo.Seek(0, SeekOrigin.Begin);
                        mo.SkipHeader();
                        Assert.Equal(bodyInt, mo.ReadInt32());
                        NmpTcpClient.MessageObjectPool.Free(mo);
                    }

                    foreach (var cs in clients)
                    {
                        NmpTcpClient client = null;
                        cs.ServerClient.Disconnected += c => { client = c; };

                        cs.Client.Close();

                        Assert.True(WaitFor(100, () => client != null));
                    }
                }
                finally
                {
                    foreach (var client in clients)
                    {
                        client.Client.Dispose();
                        client.ServerClient.Dispose();
                    }
                }
            }
        }

        [Fact]
        public void ClientCloseConnection()
        {
            const int port = 1236;
            using (var server = new NmpTcpServer(_logger))
            {
                NmpTcpClient serverClient = null;
                server.NewConnection += (tcpServer, client) => { serverClient = client; };
                var listenTask = server.Listen(port);
                var tcpClient = new TcpClient();
                tcpClient.Connect("127.0.0.1", port);

                Assert.True(WaitFor(100, () => tcpClient.Connected));
                //var tcpClientReader = Task.Factory.StartNew(() => _ = tcpClient.GetStream().ReadByte());

                Assert.True(WaitFor(100, () => serverClient != null));

                serverClient.Close();
                serverClient.Dispose();

                Assert.Equal(-1, tcpClient.GetStream().ReadByte());
            }
        }

        [Fact]
        public void ClientConnectSendPacket()
        {
            const int port = 1237;
            using (var server = new NmpTcpServer(_logger))
            {
                NmpTcpClient serverClient = null;
                server.NewConnection += (tcpServer, client) => { serverClient = client; };
                var listenTask = server.Listen(port);
                Thread.Sleep(100); // Listen socket needs time to start
                var client = new NmpTcpClient(_logger, "127.0.0.1", port);
                var clientTask = client.ReadPacketsAsync();

                Assert.True(WaitFor(1000, () => serverClient != null));
                var serverClientTasks = serverClient.ReadPacketsAsync();

                MessageObject smo = null;
                MessageObject cmo = null;
                serverClient.MessageObjectReceived += (NmpTcpClient client, MessageObject messageObject) => { smo = messageObject; };
                client.MessageObjectReceived += (NmpTcpClient client, MessageObject messageObject) => { cmo = messageObject; };

                {
                    var mo1 = new MessageObject();
                    mo1.MessageType = 10;
                    mo1.SkipHeader();
                    mo1.Write(1234);
                    client.SendAsync(mo1.GetPacketMemory()).Wait();

                    Assert.True(WaitFor(100, () => smo != null));

                    Assert.Equal(10, smo.MessageType);
                    Assert.Equal(mo1.Size, smo.Size);
                    Assert.Equal(mo1.PacketSizeAccordingToHeader, smo.PacketSizeAccordingToHeader);
                    smo.SkipHeader();
                    Assert.Equal(1234, smo.ReadInt32());
                }

                {
                    var mo2 = new MessageObject();
                    mo2.MessageType = 11;
                    mo2.SkipHeader();
                    mo2.Write(4321);
                    serverClient.SendAsync(mo2.GetPacketMemory()).Wait();

                    Assert.True(WaitFor(100, () => cmo != null));

                    Assert.Equal(11, cmo.MessageType);
                    Assert.Equal(mo2.Size, cmo.Size);
                    Assert.Equal(mo2.PacketSizeAccordingToHeader, cmo.PacketSizeAccordingToHeader);
                    cmo.SkipHeader();
                    Assert.Equal(4321, cmo.ReadInt32());
                }
            }
        }

        [Fact]
        public void SendLargePackets()
        {
            const int port = 1238;
            using (var server = new NmpTcpServer(_logger))
            {
                NmpTcpClient serverClient = null;
                server.NewConnection += (tcpServer, client) => { serverClient = client; };
                var listenTask = server.Listen(port);
                Thread.Sleep(100); // Listen socket needs time to start
                var client = new NmpTcpClient(_logger, "127.0.0.1", port);
                var clientTask = client.ReadPacketsAsync();

                Assert.True(WaitFor(1000, () => serverClient != null));
                var serverClientTasks = serverClient.ReadPacketsAsync();

                MessageObject smo = null;
                MessageObject cmo = null;
                serverClient.MessageObjectReceived += (NmpTcpClient client, MessageObject messageObject) => { smo = messageObject; };
                client.MessageObjectReceived += (NmpTcpClient client, MessageObject messageObject) => { cmo = messageObject; };

                var maxSize = (int)(Constants.MaxPacketBodySize / sizeof(long));
                {
                    var mo1 = new MessageObject();
                    mo1.MessageType = 10;
                    mo1.SkipHeader();
                    Assert.Equal(4, mo1.Position);
                    for (var i = 0; i < maxSize; i++)
                        mo1.Write((long)i);
                    client.SendAsync(mo1.GetPacketMemory()).Wait();

                    Assert.True(WaitFor(1000000, () => smo != null));

                    Assert.Equal(10, smo.MessageType);
                    Assert.Equal(mo1.Size, smo.Size);
                    Assert.Equal(mo1.PacketSizeAccordingToHeader, smo.PacketSizeAccordingToHeader);
                    smo.SkipHeader();
                    for (var i = 0; i < maxSize; i++)
                        Assert.Equal(i, smo.ReadInt64());
                }

                {
                    var mo2 = new MessageObject();
                    mo2.MessageType = 11;
                    mo2.SkipHeader();
                    for (var i = 0; i < maxSize; i++)
                        mo2.Write((long)(maxSize - i));
                    serverClient.SendAsync(mo2.GetPacketMemory()).Wait();

                    Assert.True(WaitFor(100, () => cmo != null));

                    Assert.Equal(11, cmo.MessageType);
                    Assert.Equal(mo2.Size, cmo.Size);
                    Assert.Equal(mo2.PacketSizeAccordingToHeader, cmo.PacketSizeAccordingToHeader);
                    cmo.SkipHeader();
                    for (var i = 0; i < maxSize; i++)
                        Assert.Equal((maxSize - i), cmo.ReadInt64());
                }
            }
        }

        [Fact]
        public void SendSmallPacketsFragmented()
        {
            const int port = 1238;
            using (var server = new NmpTcpServer(_logger))
            {
                NmpTcpClient serverClient = null;
                server.NewConnection += (tcpServer, client) => { serverClient = client; };
                var listenTask = server.Listen(port);
                Thread.Sleep(100); // Listen socket needs time to start
                var client = new NmpTcpClient(_logger, "127.0.0.1", port);
                var clientTask = client.ReadPacketsAsync();

                Assert.True(WaitFor(1000, () => serverClient != null));
                var serverClientTasks = serverClient.ReadPacketsAsync();

                MessageObject smo = null;
                MessageObject cmo = null;
                serverClient.MessageObjectReceived += (NmpTcpClient client, MessageObject messageObject) => { smo = messageObject; };
                client.MessageObjectReceived += (NmpTcpClient client, MessageObject messageObject) => { cmo = messageObject; };

                var maxSizes = new int[] { 0, 1, 5, _random.Next(1, 255), _random.Next(1, 255), _random.Next(1, 255), _random.Next(1, 255) };
                //var maxSize = (int) (CommunicationConstants.MaxPacketBodySize / sizeof(long)) ;
                foreach (var maxSize in maxSizes)
                {
                    var mo1 = new MessageObject();
                    mo1.MessageType = (byte)maxSize;
                    mo1.SkipHeader();
                    Assert.Equal(4, mo1.Position);
                    for (var i = 0; i < maxSize; i++)
                        mo1.Write((byte)i);
                    var mem = mo1.GetPacketMemory();
                    for (var i = 0; i < mo1.Size; i++)
                    {
                        Thread.Sleep(_random.Next(0, 10));
                        client.SendAsync(mem.Slice(i, 1)).Wait();
                    }

                    Assert.True(WaitFor(1000000, () => smo != null));

                    Assert.Equal(maxSize, smo.MessageType);
                    Assert.Equal(mo1.Size, smo.Size);
                    Assert.Equal(mo1.PacketSizeAccordingToHeader, smo.PacketSizeAccordingToHeader);
                    smo.SkipHeader();
                    for (var i = 0; i < maxSize; i++)
                        Assert.Equal(i, smo.ReadByte());
                }
            }
        }

        [Fact]
        public void SendSmallPacketsClustered()
        {
            const int port = 1238;
            using (var server = new NmpTcpServer(_logger))
            {
                NmpTcpClient serverClient = null;
                server.NewConnection += (tcpServer, client) => { serverClient = client; };
                var listenTask = server.Listen(port);
                Thread.Sleep(100); // Listen socket needs time to start
                var client = new NmpTcpClient(_logger, "127.0.0.1", port);
                var clientTask = client.ReadPacketsAsync();

                Assert.True(WaitFor(1000, () => serverClient != null));
                var serverClientTasks = serverClient.ReadPacketsAsync();


                var mos = new List<MessageObject>();
                serverClient.MessageObjectReceived += (NmpTcpClient client, MessageObject messageObject) => { mos.Add(messageObject); };
                //client.MessageObjectReceived += (NPTcpClient client, MessageObject messageObject) => { cmo = messageObject; };

                var maxSizes = new int[] { 0, 1, 5, _random.Next(1, 255), _random.Next(1, 255), _random.Next(1, 255), _random.Next(1, 255) };
                //var maxSize = (int) (CommunicationConstants.MaxPacketBodySize / sizeof(long)) ;
                var ms = new MemoryStream();
                for (var i = 0; i < maxSizes.Length; i++)
                {
                    var maxSize = maxSizes[i];
                    var mo1 = new MessageObject();
                    mo1.MessageType = (byte)i;
                    mo1.SkipHeader();
                    Assert.Equal(4, mo1.Position);
                    for (var i2 = 0; i2 < maxSize; i2++)
                        mo1.Write((byte)unchecked((byte)i2 + i));
                    var mem = mo1.GetPacketMemory();
                    ms.Write(mem.Span);
                }

                var all = ms.ToArray();
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                client.SendAsync(new ReadOnlyMemory<Byte>(all));
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

                Assert.True(WaitFor(1000, () => mos.Count == maxSizes.Length));
                for (var i = 0; i < maxSizes.Length; i++)
                {
                    var mo = mos[i];
                    var maxSize = maxSizes[i];
                    Assert.Equal(i, mo.MessageType);
                    Assert.Equal(maxSize + Constants.MaxPacketHeaderSize, mo.Size);
                    Assert.Equal(maxSize + Constants.MaxPacketHeaderSize, mo.PacketSizeAccordingToHeader);
                    mo.SkipHeader();
                    for (var i2 = 0; i2 < maxSize; i2++)
                        Assert.Equal(unchecked((byte)i2 + i), mo.ReadByte());
                }
                //
                //                    Assert.Equal(maxSize, smo.MessageType);
                //                    Assert.Equal(mo1.Size, smo.Size);
                //                    Assert.Equal(mo1.PacketSizeAccordingToHeader, smo.PacketSizeAccordingToHeader);
                //                    smo.SkipHeader();
                //                    for (var i = 0; i < maxSize; i++)
                //                        Assert.Equal(i, smo.ReadByte());
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
