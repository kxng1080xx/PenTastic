using System.Buffers.Binary;
using System.Security.Cryptography;

using System.Drawing;
using System.Windows.Forms;

namespace PenTastic;

sealed class FrameCipher : IDisposable
{





























     
    public const int TagSize = 16;
    readonly AesGcm _aes;
    readonly byte _out, _in;
    ulong _sent, _received;

    public FrameCipher(byte[] secret, byte[] nonce, byte[] clientId, bool server)
    {
        _aes = new AesGcm(Key(secret, nonce, clientId), TagSize);
        (_out, _in) = server ? ((byte)1, (byte)2) : ((byte)2, (byte)1);
    }

    public static byte[] Key(byte[] secret, byte[] nonce, byte[] clientId) =>
        HMACSHA256.HashData(secret, "PNTC key"u8.ToArray().Concat(nonce).Concat(clientId).ToArray());

    static byte[] Iv(byte direction, ulong counter)
    {
        var iv = new byte[12];
        iv[0] = direction;
        BinaryPrimitives.WriteUInt64LittleEndian(iv.AsSpan(4), counter);
        return iv;
    }

    public byte[] Seal(byte[] frame)
    {
        var body = frame.AsSpan(2);
        var sealedFrame = new byte[frame.Length + TagSize];
        _aes.Encrypt(Iv(_out, _sent++), body, sealedFrame.AsSpan(2, body.Length), sealedFrame.AsSpan(2 + body.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(sealedFrame, checked((ushort)(body.Length + TagSize)));
        return sealedFrame;
    }

    public byte[] Open(byte[] body)
    {
        if (body.Length <= TagSize) throw new InvalidDataException("encrypted frame too short");
        var plain = new byte[body.Length - TagSize];
        try
        {
            _aes.Decrypt(Iv(_in, _received++), body.AsSpan(0, plain.Length), body.AsSpan(plain.Length), plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("frame failed authentication");
        }
        return plain;
    }

    public void Dispose() => _aes.Dispose();
}
