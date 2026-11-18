using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Plugin.NaughtyDog.Content;

/// <summary>
/// Writes a PSARC 1.3 archive the way Uncharted 2's own are built (the
/// <c>dlc-patch*.psarc</c> in its update): zlib, 64 KiB blocks, two-byte block
/// sizes, relative names without a leading slash (flags 0), entry 0 the
/// manifest of names separated by newlines, each entry keyed by the MD5 of its
/// name. A block that does not shrink is stored raw; a raw full block's size is
/// written as 0.
/// </summary>
public static class PsarcWriter
{
    private const int BlockSize = 0x10000;
    private const int EntrySize = 30;

    public static byte[] Write(IReadOnlyList<(string Name, byte[] Data)> files)
    {
        List<byte[]> bodies = [Encoding.ASCII.GetBytes(string.Join('\n', files.Select(f => f.Name))), .. files.Select(f => f.Data)];
        List<ushort> sizes = [];
        List<byte[]> blocks = [];
        List<(int FirstBlock, long Length, long Offset)> entries = [];
        long offset = 0;
        foreach (byte[] body in bodies)
        {
            int first = blocks.Count;
            for (int at = 0; at < body.Length || (body.Length == 0 && at == 0); at += BlockSize)
            {
                byte[] raw = body.AsSpan(at, Math.Min(BlockSize, body.Length - at)).ToArray();
                byte[] packed = Deflate(raw);
                byte[] block = packed.Length < raw.Length ? packed : raw;
                blocks.Add(block);
                sizes.Add((ushort)(block.Length == BlockSize ? 0 : block.Length));
                if (body.Length == 0)
                    break;
            }
            entries.Add((first, body.Length, offset));
            offset += blocks.Skip(first).Sum(b => (long)b.Length);
        }

        int tocLength = 32 + EntrySize * bodies.Count + 2 * sizes.Count;
        using MemoryStream archive = new();
        byte[] header = new byte[32];
        Encoding.ASCII.GetBytes("PSAR").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 0x00010003);
        Encoding.ASCII.GetBytes("zlib").CopyTo(header, 8);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), (uint)tocLength);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), EntrySize);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), (uint)bodies.Count);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), BlockSize);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), 0);
        archive.Write(header);
        for (int i = 0; i < entries.Count; i++)
        {
            byte[] entry = new byte[EntrySize];
            if (i > 0)
                MD5.HashData(Encoding.ASCII.GetBytes(files[i - 1].Name)).CopyTo(entry, 0);
            BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(16), (uint)entries[i].FirstBlock);
            WriteU40(entry.AsSpan(20), entries[i].Length);
            WriteU40(entry.AsSpan(25), tocLength + entries[i].Offset);
            archive.Write(entry);
        }
        byte[] two = new byte[2];
        foreach (ushort size in sizes)
        {
            BinaryPrimitives.WriteUInt16BigEndian(two, size);
            archive.Write(two);
        }
        foreach (byte[] block in blocks)
            archive.Write(block);
        return archive.ToArray();
    }

    private static byte[] Deflate(byte[] raw)
    {
        using MemoryStream output = new();
        using (ZLibStream zlib = new(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);
        return output.ToArray();
    }

    private static void WriteU40(Span<byte> span, long value)
    {
        for (int i = 4; i >= 0; i--, value >>= 8)
            span[i] = (byte)value;
    }
}
