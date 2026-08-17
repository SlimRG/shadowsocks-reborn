using Shadowsocks.Encryption.AEAD;

namespace Shadowsocks.Controller.Service
{
    /// <summary>Shared transport buffer limits used by the protocol and Windows relay.</summary>
    public static class TransportBufferSizes
    {
        public const int ReceiveSize = 2048;
        public const int ChunkOverheadSize = 16 * 2 + AEADEncryptor.CHUNK_LEN_BYTES;
        public const uint MaxChunkSize = AEADEncryptor.CHUNK_LEN_MASK + AEADEncryptor.CHUNK_LEN_BYTES + 16 * 2;
        public const int BufferSize = ReceiveSize + (int)MaxChunkSize + 32;
    }
}
