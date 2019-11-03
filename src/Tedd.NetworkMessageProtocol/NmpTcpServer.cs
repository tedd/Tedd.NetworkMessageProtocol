using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Tedd.NetworkMessageProtocol
{
    public class NmpTcpServer : IDisposable
    {
        private readonly ILogger _logger;
        private TcpListener _tcpListener;
        private CancellationTokenSource _cancellationTokenSource;
        public Int32 Port { get; private set; }
        public bool Listening { get; private set; }
        private readonly object _startStopLockObject = new Object();

        public delegate void ConnectionDelegate(NmpTcpServer server, NmpTcpClient newClient);

        public event ConnectionDelegate NewConnection;

        public NmpTcpServer(ILogger logger)
        {
            _logger = logger;
        }


        public async Task Listen(int port)
        {
            lock (_startStopLockObject)
            {
                Port = port;
                _logger.LogInformation($"Starting listen on Tcp port {Port}");
                if (Listening)
                    throw new Exception("Already listening.");
                Listening = true;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = new CancellationTokenSource();
                _tcpListener = new TcpListener(IPAddress.Any, Port);
                _tcpListener.Start();
            }

            for (; ; )
            {
                // Wait for connection
                Socket socket = null;
                var tcpl = _tcpListener;
                if (tcpl == null)
                    break;
                try
                {
                    socket = await tcpl.AcceptSocketAsync();
                }
                catch (ObjectDisposedException)
                {
                    // Socket is disposed, most likely because we called Stop
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                        break;
                    // No?
                    throw;
                }

                if (socket == null || _cancellationTokenSource.Token.IsCancellationRequested)
                    break;

                // Got new connection, create a protocol handler for it and fire off event
                var client = new NmpTcpClient(_logger, socket);
                _logger.LogInformation($"Accepted new client connection from {((IPEndPoint)socket.RemoteEndPoint).Address} port {((IPEndPoint)socket.RemoteEndPoint).Port}");
                NewConnection?.Invoke(this, client);
            }

            _logger.LogInformation($"Ended listening on port {Port}");
        }

        public void Stop()
        {
            lock (_startStopLockObject)
            {
                _cancellationTokenSource?.Cancel();
                _tcpListener?.Stop();
                _tcpListener = null;
                Listening = false;
            }
        }

        public void Dispose()
        {
            Stop();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }
}
