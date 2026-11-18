using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Plugin.NaughtyDog.Tests;

/// <summary>Synthetic stand-ins for game data (none is committed) and a PSARC reader to check the writer.</summary>
internal static class TestData
{
    public const string BlowfishKey = "test-blowfish-key-20";
    public const string HmacKey = "test hmac key, any length works for HMAC-SHA1";

    public const uint ConfigSymbol = 0x1d38f2f9;
    public const uint ListSymbol = 0x5a4731d2;

    /// <summary>
    /// A DC00 module laid out like UC2's dc1/net.bin: a config record whose
    /// +0x00 and +0x10 are host strings and +0x04/+0x14 ports, and a
    /// {count, pointer} list of three 8-byte records {u32 id, char* name}.
    /// </summary>
    public static byte[] NetModule()
    {
        List<byte> d = [];
        List<int> relocs = [];
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); d.AddRange(b.ToArray()); }
        void Ptr(int v) { relocs.Add(d.Count); U32((uint)v); }
        void At(int offset) { while (d.Count < offset) d.Add(0); }

        U32(0x44433030); U32(1); U32(0); U32(0);           // header, reloc offset patched below
        Ptr(0x18); U32(0x916aa605);                        // root
        U32(1); U32(2);                                    // table at 0x18: 2 symbols
        U32(ConfigSymbol); Ptr(0x40);
        U32(ListSymbol); Ptr(0x80);
        At(0x40);                                          // config record
        Ptr(0x100); U32(3235); U32(0); U32(0); Ptr(0x110); U32(3233);
        At(0x80);                                          // list
        U32(3); Ptr(0x90);
        At(0x90);
        for (int i = 0; i < 3; i++) { U32((uint)(10 + i)); Ptr(0x120 + 0x10 * i); }
        At(0x100); d.AddRange(Encoding.ASCII.GetBytes("174.129.210.135\0"));
        At(0x110); d.AddRange(Encoding.ASCII.GetBytes("174.129.16.161\0"));
        for (int i = 0; i < 3; i++) { At(0x120 + 0x10 * i); d.AddRange(Encoding.ASCII.GetBytes($"actor-{i}\0")); }
        At(0x160);
        int relocOffset = d.Count;
        U32((uint)relocs.Count);
        foreach (int r in relocs)
            U32((uint)r);
        byte[] bytes = [.. d];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), (uint)relocOffset);
        return bytes;
    }

    /// <summary>Reads a PSARC 1.3 (zlib) the way a client does; checks each entry's name MD5.</summary>
    public static Dictionary<string, byte[]> ReadPsarc(byte[] archive)
    {
        Assert.Equal("PSAR", Encoding.ASCII.GetString(archive, 0, 4));
        Assert.Equal(0x00010003u, BinaryPrimitives.ReadUInt32BigEndian(archive.AsSpan(4)));
        int tocLength = (int)BinaryPrimitives.ReadUInt32BigEndian(archive.AsSpan(12));
        int entrySize = (int)BinaryPrimitives.ReadUInt32BigEndian(archive.AsSpan(16));
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(archive.AsSpan(20));
        int blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(archive.AsSpan(24));
        int sizesAt = 32 + entrySize * count;
        ushort Size(int i) => BinaryPrimitives.ReadUInt16BigEndian(archive.AsSpan(sizesAt + 2 * i));

        byte[] Entry(int i, out byte[] md5)
        {
            ReadOnlySpan<byte> e = archive.AsSpan(32 + entrySize * i, entrySize);
            md5 = e[..16].ToArray();
            int block = (int)BinaryPrimitives.ReadUInt32BigEndian(e[16..]);
            long length = ReadU40(e[20..]), offset = ReadU40(e[25..]);
            List<byte> body = [];
            while (body.Count < length)
            {
                int stored = Size(block) == 0 ? blockSize : Size(block);
                byte[] raw = archive.AsSpan((int)offset, stored).ToArray();
                int expected = (int)Math.Min(blockSize, length - body.Count);
                if (stored < expected)
                {
                    using ZLibStream zlib = new(new MemoryStream(raw), CompressionMode.Decompress);
                    using MemoryStream output = new();
                    zlib.CopyTo(output);
                    raw = output.ToArray();
                }
                body.AddRange(raw);
                offset += stored;
                block++;
            }
            return [.. body];
        }

        string[] names = Encoding.ASCII.GetString(Entry(0, out _)).Split('\n');
        Assert.Equal(count - 1, names.Length);
        Dictionary<string, byte[]> files = [];
        for (int i = 1; i < count; i++)
        {
            byte[] data = Entry(i, out byte[] md5);
            Assert.Equal(MD5.HashData(Encoding.ASCII.GetBytes(names[i - 1])), md5);
            files[names[i - 1]] = data;
        }
        Assert.True(tocLength >= sizesAt);
        return files;
    }

    private static long ReadU40(ReadOnlySpan<byte> s) => (long)s[0] << 32 | (long)s[1] << 24 | (long)s[2] << 16 | (long)s[3] << 8 | s[4];
}
