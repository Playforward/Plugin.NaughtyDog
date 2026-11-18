using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Plugin.NaughtyDog.Crypto;

/// <summary>
/// Naughty Dog's net crypt (Uncharted 2, game/net/http.cpp and the ticket
/// check): Blowfish in ECB over every whole 8-byte block, big-endian words,
/// with a trailing partial block left as it is, plus HMAC-SHA1. Both keys are
/// strings the EBOOT keeps (tools/re/uc2keys.py prints them); they come from
/// the plugin config, never from code.
/// </summary>
/// <remarks>
/// [C 1.09] Block decrypt 0x431324 / encrypt 0x430b38 / key setup 0x431b40
/// (standard Blowfish, key up to 56 bytes, set once by 0x468388); HMAC 0x44a978
/// (ipad 0x36, 64-byte block, SHA-1 0xa7e684). Two framings use them:
/// <see cref="Seal"/> (a file, <c>*.psarc.crypt</c>) and <see cref="SealMessage"/>
/// (a datagram).
/// </remarks>
public sealed class NdCrypt
{
    /// <summary>A sealed file starts with the plain length and the HMAC [C 0x44d9b0].</summary>
    public const int FileHeaderSize = 4 + HmacSize;
    public const int HmacSize = 20;

    private readonly byte[] _blowfishKey;
    private readonly byte[] _hmacKey;

    public NdCrypt(string blowfishKey, string hmacKey)
    {
        if (string.IsNullOrEmpty(blowfishKey) || string.IsNullOrEmpty(hmacKey))
            throw new ArgumentException("both net crypt keys are needed");
        _blowfishKey = Encoding.Latin1.GetBytes(blowfishKey);
        _hmacKey = Encoding.Latin1.GetBytes(hmacKey);
        if (_blowfishKey.Length > 56)
            throw new ArgumentException("a Blowfish key is at most 56 bytes", nameof(blowfishKey));
    }

    public byte[] Hmac(ReadOnlySpan<byte> data) => HMACSHA1.HashData(_hmacKey, data);

    /// <summary>Encrypts the whole 8-byte blocks of <paramref name="data"/> in place.</summary>
    public void EncryptBlocks(Span<byte> data) => Process(data, forEncryption: true);

    /// <summary>Decrypts the whole 8-byte blocks of <paramref name="data"/> in place.</summary>
    public void DecryptBlocks(Span<byte> data) => Process(data, forEncryption: false);

    private void Process(Span<byte> data, bool forEncryption)
    {
        BlowfishEngine engine = new();
        engine.Init(forEncryption, new KeyParameter(_blowfishKey));
        byte[] block = new byte[8];
        for (int offset = 0; offset + 8 <= data.Length; offset += 8)
        {
            data.Slice(offset, 8).CopyTo(block);
            engine.ProcessBlock(block, 0, block, 0);
            block.CopyTo(data.Slice(offset, 8));
        }
    }

    /// <summary>
    /// A file as the game's sealed download reads it [C 0x44d9b0]: u32 plain
    /// length, HMAC-SHA1 of the decrypted body, then the body encrypted. The
    /// body is the plain bytes zero-padded to whole blocks, because the client
    /// MACs every decrypted byte it received but writes only the plain length.
    /// </summary>
    public byte[] Seal(ReadOnlySpan<byte> plain)
    {
        int padded = (plain.Length + 7) & ~7;
        byte[] file = new byte[FileHeaderSize + padded];
        BinaryPrimitives.WriteUInt32BigEndian(file, (uint)plain.Length);
        Span<byte> body = file.AsSpan(FileHeaderSize);
        plain.CopyTo(body);
        Hmac(body).CopyTo(file, 4);
        EncryptBlocks(body);
        return file;
    }

    /// <summary>The inverse of <see cref="Seal"/>; null when the HMAC does not match.</summary>
    public byte[]? Open(ReadOnlySpan<byte> file)
    {
        if (file.Length < FileHeaderSize)
            return null;
        uint length = BinaryPrimitives.ReadUInt32BigEndian(file);
        byte[] body = file[FileHeaderSize..].ToArray();
        DecryptBlocks(body);
        if (length > body.Length || !CryptographicOperations.FixedTimeEquals(Hmac(body), file.Slice(4, HmacSize)))
            return null;
        return body[..(int)length];
    }

    /// <summary>
    /// A datagram as the game checks it [C 0x467fa8]: payload then the HMAC of
    /// the payload, all whole blocks encrypted.
    /// </summary>
    public byte[] SealMessage(ReadOnlySpan<byte> payload)
    {
        byte[] message = new byte[payload.Length + HmacSize];
        payload.CopyTo(message);
        Hmac(payload).CopyTo(message, payload.Length);
        EncryptBlocks(message);
        return message;
    }

    /// <summary>The inverse of <see cref="SealMessage"/>; null when the HMAC does not match.</summary>
    public byte[]? OpenMessage(ReadOnlySpan<byte> message)
    {
        if (message.Length < HmacSize)
            return null;
        byte[] plain = message.ToArray();
        DecryptBlocks(plain);
        int length = plain.Length - HmacSize;
        return CryptographicOperations.FixedTimeEquals(Hmac(plain.AsSpan(0, length)), plain.AsSpan(length)) ? plain[..length] : null;
    }
}
