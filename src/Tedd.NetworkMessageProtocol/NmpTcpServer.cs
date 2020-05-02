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
    public class RequestDetails
    {
        public bool AcceptRequest = true;

        public EndPoint RemoteEndpoint;
    }
    public class NmpTcpServer : IDisposable
    {
        private readonly ILogger _logger;
        private readonly int _maxClientPacketSize;
        private TcpListener _tcpListener;
        private CancellationTokenSource _cancellationTokenSource;
        public Int32 Port { get; private set; }
        public bool Listening { get; private set; }
        private readonly object _startStopLockObject = new Object();

        public delegate void ConnectionRequestDelegate(NmpTcpServer server, RequestDetails request);
        public event ConnectionRequestDelegate ConnectionRequest;
        public delegate void ConnectionDelegate(NmpTcpServer server, NmpTcpClient newClient);
        public event ConnectionDelegate NewConnection;

        public NmpTcpServer(ILogger logger, int maxClientPacketSize = Constants.MessageObjectMaxSize)
        {
            _logger = logger;
            _maxClientPacketSize = maxClientPacketSize;
        }


        /// <summary>
        /// Start listening for clients.
        /// </summary>
        /// <param name="port"></param>
        /// <returns></returns>
        public async Task Listen(int port)
        {
            lock (_startStopLockObject)
            {
                Port = port;
                _logger.LogInformation($"Starting listen on TCP port {Port}");
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

                var rd=new RequestDetails()
                {
                    RemoteEndpoint = socket.RemoteEndPoint,
                    AcceptRequest = true
                };
                try
                {
                    ConnectionRequest?.Invoke(this, rd);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "ConnectionRequest event handler threw exception. Disposing of socket safely, effectively ignoring incoming connection.");
                    socket.Close(10);
                    socket.Dispose();
                    continue;
                }

                if (!rd.AcceptRequest)
                {
                    _logger.LogInformation($"Denied new client connection on TCP port {Port} from {((IPEndPoint)socket.RemoteEndPoint).Address}:{((IPEndPoint)socket.RemoteEndPoint).Port}");
                    socket.Close(10);
                    socket.Dispose();
                    continue;
                }

                // Got new connection, create a protocol handler for it and fire off event
                var client = new NmpTcpClient(_logger, socket, _maxClientPacketSize);
                _logger.LogInformation($"Accepted new client connection on TCP port {Port} from {((IPEndPoint)socket.RemoteEndPoint).Address}:{((IPEndPoint)socket.RemoteEndPoint).Port}");
                NewConnection?.Invoke(this, client);
            }

            _logger.LogInformation($"Ended listening on TCP port {Port}");
        }

        /// <summary>
        /// Stop listening for clients
        /// </summary>
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
