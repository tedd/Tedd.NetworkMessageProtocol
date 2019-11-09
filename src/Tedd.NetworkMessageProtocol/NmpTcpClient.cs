using System;
using System.Buffers;
using System.ComponentModel.Design;
using System.IO;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic;
using PipeOptions = System.IO.Pipelines.PipeOptions;

namespace Tedd.NetworkMessageProtocol
{
    public class NmpTcpClient : IDisposable
    {
        private readonly ILogger _logger;
        private Socket _socket;
        public IPEndPoint RemoteIPEndPoint { get; private set; }
        private bool _reading = false;
        public static ObjectPool<MessageObject> MessageObjectPool = new ObjectPool<MessageObject>(() => new MessageObject(), o => o.Reset(), 100);
        private bool _closing = false;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="client"></param>
        /// <param name="messageObject"></param>
        /// <param name="action"></param>
        /// <returns>True if object should be freed automatically</returns>
        public delegate Task MessageObjectReceivedAsyncDelegate(NmpTcpClient client, MessageObject messageObject, MessageObjectAction action);

        public delegate Task DisconnectAsyncEventDelegate(NmpTcpClient client, string reason);

        public event MessageObjectReceivedAsyncDelegate MessageObjectReceivedAsync;
        public event DisconnectAsyncEventDelegate DisconnectedAsync;

        /// <summary>
        /// Create client from existing socket
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="socket"></param>
        public NmpTcpClient(ILogger logger, Socket socket)
        {
            _logger = logger;
            _socket = socket;
            RemoteIPEndPoint = ((IPEndPoint)_socket.RemoteEndPoint);
            _socket.NoDelay = false;
        }

        public NmpTcpClient(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Create client connection to remote address and port
        /// </summary>
        /// <param name="remoteAddress"></param>
        /// <param name="remotePort"></param>
        public async Task ConnectAsync(string remoteAddress, int remotePort)
        {

            _logger.LogInformation($"Establishing connection to {remoteAddress} port {remotePort}");
            IPAddress ipAddr;
            if (!IPAddress.TryParse(remoteAddress, out ipAddr))
            {
                IPHostEntry ipHost = Dns.GetHostEntry(remoteAddress);
                ipAddr = ipHost.AddressList[0];
            }

            if (_socket != null)
                throw new Exception("Connect called twice.");
            if (_reading)
                throw new Exception("Socket already open");

            IPEndPoint endPoint = new IPEndPoint(ipAddr, remotePort);
            _socket = new Socket(ipAddr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _socket.NoDelay = false;
            await _socket.ConnectAsync(endPoint);
            _logger.LogInformation($"Connection to {remoteAddress} ({ipAddr}) port {remotePort} established");
        }

        /// <summary>
        /// Close socket
        /// </summary>
        public void Close()
        {
            _closing = true;
            if (_socket.Connected)
            {
                _socket.Shutdown(SocketShutdown.Receive);
                _socket.Close(100);
            }
        }


        /// <summary>
        /// Send MessageObject.
        /// </summary>
        /// <param name="messageObject"></param>
        /// <returns>Bytes sent</returns>
        public async Task<int> SendAsync(MessageObject messageObject)
        {
            return await SendAsync(messageObject.GetPacketMemory());
        }

        /// <summary>
        /// Send raw data.
        /// </summary>
        /// <remarks>Dangerous! Malformed data will prevent packet reassembly on consecutive packets.</remarks>
        /// <param name="memory"></param>
        /// <returns>Bytes sent</returns>
        public async Task<int> SendAsync(ReadOnlyMemory<byte> memory)
        {
            var total = 0;
            var count = 0;
            for (; ; )
            {
                var bytes = await _socket.SendAsync(memory, SocketFlags.None);
                // No data sent? Done!
                if (bytes == 0)
                    break;

                total += bytes;

                // Some, but not all data sent? Move buffer and do another round
                if (bytes < memory.Length)
                    memory = memory.Slice(bytes);
                else
                    // All sent, we are done.
                    break;

                count++;
                if (count == 1000)
                    throw new Exception($"{count} attempts at socket send (should be 1).");
            }

            return total;
        }

        /// <summary>
        /// Async read new packets until socket is disconnected
        /// </summary>
        /// <returns></returns>
        public async Task ReadPacketsAsync()
        {
            if (_reading)
                throw new Exception("Already reading from socket.");
            _reading = true;
            var pipe = new Pipe();
            var writing = FillPipeAsync(_socket, pipe.Writer);
            var reading = ReadPipeAsync(pipe.Reader);

            await Task.WhenAll(reading, writing);
            _reading = false;
        }

        private async Task FillPipeAsync(Socket socket, PipeWriter writer)
        {
            _closing = false;
            const int minimumBufferSize = 1024 * 10000;
            var disconnectReason = string.Empty;
            try
            {
                while (true)
                {
                    // Allocate minimum buffer from the PipeWriter
                    Memory<byte> memory = writer.GetMemory(minimumBufferSize);
                    try
                    {
                        int bytesRead = await socket.ReceiveAsync(memory, SocketFlags.None);
                        if (bytesRead == 0)
                            break;

                        // Tell the PipeWriter how much was read from the Socket
                        writer.Advance(bytesRead);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Reading from remote socket {RemoteIPEndPoint.Address} {RemoteIPEndPoint.Port}");
                        break;
                    }

                    // Make the data available to the PipeReader
                    FlushResult result = await writer.FlushAsync();

                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (SocketException socketException)
            {
                disconnectReason = $"{{socketException.ErrorCode}}/{socketException.SocketErrorCode} ({socketException.SocketErrorCode.ToString()}): {socketException.Message}";
            }
            catch (Exception exception)
            {
                disconnectReason = exception.Message;
            }

            // Tell the PipeReader that there's no more data coming
            writer.Complete();

            // If we called closing manually we won't call disconnect.
            if (!_closing && DisconnectedAsync != null)
                await DisconnectedAsync.Invoke(this, disconnectReason);
        }

        private async Task ReadPipeAsync(PipeReader reader)
        {
            var mo = MessageObjectPool.Allocate();
            while (true)
            {
                ReadResult result = await reader.ReadAsync();

                ReadOnlySequence<byte> buffer = result.Buffer;

                for (; ; )
                {
                    // In a worst case scenario we will receive 1 byte at the time over TCP,
                    // causing us to rewrite header a few times. But it simplifies logic a bit so we'll do it that way.

                    // Missing packet header?
                    if (!mo.HasHeader)
                    {
                        // or have enough in buffer for a packet header?
                        if (buffer.Length < Constants.MaxPacketHeaderSize)
                            break;

                        // Write header to packet
                        mo.RawSeek(0, SeekOrigin.Begin);
                        mo.RawWrite(buffer.Slice(0, Constants.MaxPacketHeaderSize));
                        // Move buffer past header
                        buffer = buffer.Slice(Constants.MaxPacketHeaderSize);
                    }

                    // How much are we missing before packet is complete?
                    var packetSize = mo.PacketSizeAccordingToHeader;
                    var remaining = packetSize - mo.RawSize;
                    // Remainder we can add
                    remaining = Math.Min(remaining, (int)buffer.Length);

                    // Write as much as we can from buffer into packet
                    if (remaining > 0)
                        mo.RawWrite(buffer.Slice(0, remaining));

                    // Do we have a full packet?
                    if (mo.RawSize == packetSize)
                    {
                        // Trigger received packet
                        mo.Seek(0, SeekOrigin.Begin);
                        mo.MessageObjectAction.AutoFree = false;
                        if (MessageObjectReceivedAsync != null)
                            await MessageObjectReceivedAsync.Invoke(this, mo, mo.MessageObjectAction);
                        if (mo.MessageObjectAction.AutoFree)
                            // Reuse current object
                            mo.Reset();
                        else
                            // Get fresh object
                            mo = MessageObjectPool.Allocate();
                    }

                    // Move buffer to this pos
                    buffer = buffer.Slice(remaining);
                    // Empty buffer? Signal we want more
                    if (buffer.Length == 0)
                        break;
                }

                // Tell the PipeReader how much of the buffer we have consumed
                reader.AdvanceTo(buffer.Start, buffer.End);

                // Stop reading if there's no more data coming
                if (result.IsCompleted)
                    break;
            }

            // Mark the PipeReader as complete
            reader.Complete();
        }

        public void Dispose()
        {
            _socket?.Dispose();
        }

        /// <summary>
        /// Return MessageObject to pool. This must be done when processing of incoming message object is completed.
        /// </summary>
        /// <param name="messageObject"></param>
        public void FreeMessageObject(MessageObject messageObject)
        {
            MessageObjectPool.Free(messageObject);
        }

        /// <summary>
        /// Get a MessageObject from pool for use for sending. Must be returned manually to pool after sending.
        /// </summary>
        /// <returns></returns>
        public MessageObject AllocateMessageObject()
        {
            return MessageObjectPool.Allocate();
        }

        /// <summary>
        /// Shortcut for common action: Allocate MessageObject, perform populate action, send and free it.
        /// </summary>
        /// <param name="populateAction">Action to populate MessageObject before sending it</param>
        /// <returns></returns>
        public async Task<int> SendAsync(Action<MessageObject> populateAction)
        {
            var mo = AllocateMessageObject();
            populateAction(mo);
            var ret = await SendAsync(mo);
            FreeMessageObject(mo);
            return ret;
        }

        /// <summary>
        /// Shortcut for common action: Allocate MessageObject, perform populate action, send and free it.
        /// </summary>
        /// <param name="messageType">MessageType</param>
        /// <param name="populateAction">Action to populate MessageObject before sending it</param>
        /// <returns></returns>
        public async Task<int> SendAsync(byte messageType, Action<MessageObject> populateAction)
        {
            var mo = AllocateMessageObject();
            mo.MessageType = messageType;
            populateAction(mo);
            var ret = await SendAsync(mo);
            FreeMessageObject(mo);
            return ret;
        }
    }
}
