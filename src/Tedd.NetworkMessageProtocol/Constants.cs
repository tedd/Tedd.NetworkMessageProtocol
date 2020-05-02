using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tedd.NetworkMessageProtocol
{
    internal static class Constants
    {
        public const int MaxReceiveFragmentsPerPacket = 100;
        public const int MessageObjectMaxSize = 1024 * 1024 * 32;
        public const int ObjectPoolMessageObjectCount = 10;

        //public const Int32 MaxPacketSize = 10 * 1024 * 1024; // 10 MB
        //public const Int32 MaxPacketHeaderSize = 4;
        //public const Int32 MaxPacketBodySize = MaxPacketSize - MaxPacketHeaderSize;
        //public const Int32 DefaultClientBufferSize = 1024 * 1024 * 32;  // 32 MB default incoming buffer

        //public const Int32 ReceiveBufferSize = 4096 * 32; // 128 KB
    }
}
