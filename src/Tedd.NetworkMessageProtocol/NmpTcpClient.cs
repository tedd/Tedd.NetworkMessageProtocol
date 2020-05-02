using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Tedd.NetworkMessageProtocol
{
    public class NmpTcpClient : IDisposable
    {
        private readonly ObjectPool<MessageObject> _messageObjectPool;
        private readonly ILogger _logger;
        public Socket Socket { get; private set; }
        private readonly int _maxClientPacketSize;
        private bool _closing = false;
        public string RemoteEndPoint { get; private set; }

        public delegate void MessageObjectReceivedDelegate(NmpTcpClient client, MessageObject messageObject, ref bool preventAutoRecycling);
        public event MessageObjectReceivedDelegate MessageObjectReceived;

        public delegate void ClosedDelegate(NmpTcpClient client);

        public event ClosedDelegate Closed;

        /// <summary>
        /// Attaches client to socket.
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="socket"></param>
        /// <param name="maxClientPacketSize"></param>
        public NmpTcpClient(ILogger logger, Socket socket, int maxClientPacketSize)
        {
            _logger = logger;
            Socket = socket;
            _maxClientPacketSize = maxClientPacketSize;

            RemoteEndPoint = $"{((IPEndPoint)socket.RemoteEndPoint).Address}:{((IPEndPoint)socket.RemoteEndPoint).Port}";
            _messageObjectPool = new ObjectPool<MessageObject>(() => new MessageObject(maxClientPacketSize), mo => mo.Stream.Clear(), Constants.ObjectPoolMessageObjectCount);
        }

        public NmpTcpClient(ILogger logger, int maxClientPacketSize)
        {
            _logger = logger;
            _maxClientPacketSize = maxClientPacketSize;

            _messageObjectPool = new ObjectPool<MessageObject>(() => new MessageObject(maxClientPacketSize), mo => mo.Stream.Clear(), Constants.ObjectPoolMessageObjectCount);
        }

        public async Task Connect(string host, int port)
        {
            if (Socket != null)
                throw new Exception("Socket already set up.");

            Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            await Socket.ConnectAsync(host, port);
            RemoteEndPoint = $"{((IPEndPoint)Socket.RemoteEndPoint).Address}:{((IPEndPoint)Socket.RemoteEndPoint).Port}";
        }


        /// <summary>
        /// Send MessageObject.
        /// Note that Stream.Position must point to last byte.
        /// </summary>
        /// <param name="messageObject">The message object to send.</param>
        /// <returns>Bytes sent</returns>
        public async Task<int> SendPacket(MessageObject messageObject, bool recycleMessageObject)
        {
            var arraySegment = messageObject.GetPacketArraySegment();
            try
            {
                return await Socket.SendAsync(arraySegment, SocketFlags.None);
            }
            finally
            {
                if (recycleMessageObject)
                    _messageObjectPool.Free(messageObject);
            }
        }

        public void Close()
        {
            _closing = true;
            Socket.Close();
        }

        public async Task ProcessIncomingAsync()
        {
            var header = new byte[4];
            var headerAS = new ArraySegment<byte>(header, 0, header.Length);
            try
            {
                while (!_closing)
                {
                    // Waiting for first byte
                    var len = await Socket.ReceiveAsync(headerAS, SocketFlags.Peek);
                    // Null read means socket is closed
                    if (len == 0)
                    {
                        _logger.LogInformation($"Client {RemoteEndPoint}: Socket closed.");
                        Close();
                        break;
                    }

                    // Check if we have enough to determine size. WriteSize uses first two bits to determine how many bytes the size is in total.
                    // The two upper bits will tell us, 1-4.
                    var headerLen = (header[0] >> 6)+1;
                    if (len < headerLen)
                    {
                        // We do not have enough bytes for a header. That's weird.
                        // Probably a protocol error, but we'll keep waiting.
                        await Task.Delay(50);
                        continue;
                    }

                    // Ok, so we have enough for a header. Therefore we know how much we need in total.
                    var size = (int)SpanReadSize(header, out var headerLength2);
                    if (headerLength2 != headerLen)
                        throw new Exception("Sanity check: Header length mismatch. This should not happen.");

                    var messageObject = _messageObjectPool.Allocate();
                    // Set Stream length to size of payload
                    messageObject.Stream.SetLength(size);

                    var received = 0;
                    var counter = 0;
                    // Loop while we fill the buffer
                    var totalSize = size + headerLength2;
                    while (received < totalSize)
                    {
                        counter++;
                        if (counter > Constants.MaxReceiveFragmentsPerPacket)
                            throw new Exception($"Exceeded {Constants.MaxReceiveFragmentsPerPacket} fragments per packet.");

                        // Size header is variable size 1-4 bytes. We want it to be before the MessageObject.Stream, which starts at position 4.
                        // So our start pos is 4-the size descriptor.
                        var startPos = 4 - headerLength2 + received;
                        var remaining = totalSize - received;
                        // Get buffer for remaining data.
                        var buffer = messageObject.GetPacketArraySegment(startPos, remaining);

                        // Read data
                        len = await Socket.ReceiveAsync(buffer, SocketFlags.None);
                        received += len;

                        // Null read means socket is closed
                        if (len == 0)
                        {
                            _logger.LogInformation($"Client {RemoteEndPoint}: Socket closed.");
                            Close();
                            break;
                        }
                    }

                    // We have received a complete message object
                    var preventAutoRecycling = false;
                    MessageObjectReceived?.Invoke(this, messageObject, ref preventAutoRecycling);
                    if (!preventAutoRecycling)
                        _messageObjectPool.Free(messageObject);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, $"Client {RemoteEndPoint}");
                Close();
            }
            Closed?.Invoke(this);
        }

        private UInt32 SpanReadSize(byte[] header, out int totalLength) => ((Span<byte>)header).ReadSize(out totalLength);

        #region IDisposable

        /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
        public void Dispose()
        {
            Socket?.Dispose();
        }

        #endregion

        public MessageObject RentMessageObject() => _messageObjectPool.Allocate();

        public async Task SendMessageObject(Action<MessageObject> action)
        {

            _messageObjectPool.AllocateExecuteDeallocate(async mo =>
            {
                action.Invoke(mo);
                await SendPacket(mo, false);
            }, mo => mo.Stream.Clear());
        }
    }
}