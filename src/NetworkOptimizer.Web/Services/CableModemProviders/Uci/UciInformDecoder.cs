using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using NetworkOptimizer.Core;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace NetworkOptimizer.Web.Services.CableModemProviders.Uci;

/// <summary>The 40-byte header in front of every UniFi inform payload.</summary>
/// <param name="PacketVersion">Framing version.</param>
/// <param name="Mac">Sending device's MAC, lowercase colon form.</param>
/// <param name="Flags">Payload flags (see <see cref="UciInformDecoder"/> constants).</param>
/// <param name="Iv">16-byte IV / GCM nonce.</param>
/// <param name="DataVersion">Payload format version.</param>
/// <param name="DataLength">Bytes of payload after the header (GCM tag included).</param>
public sealed record TnbuHeader(int PacketVersion, string Mac, ushort Flags, byte[] Iv, int DataVersion, int DataLength);

/// <summary>Why a frame could not be decoded.</summary>
public enum UciDecodeFailure
{
    None,
    /// <summary>Not a TNBU frame, or truncated.</summary>
    Malformed,
    /// <summary>The key did not authenticate the frame (wrong or rotated inform key).</summary>
    KeyMismatch,
    /// <summary>A payload encoding this decoder does not handle (e.g. snappy).</summary>
    Unsupported,
}

/// <summary>
/// Decodes UniFi inform frames: TNBU header, then AES-128-GCM (header as AAD, 16-byte nonce)
/// or AES-128-CBC on older firmware, then zlib. Layout confirmed against the open-source
/// uci-inform-exporter. GCM goes through BouncyCastle because .NET's AesGcm only accepts
/// 12-byte nonces, and the inform nonce is 16 bytes.
/// </summary>
[VendorSpecific("UniFi", "Inform TNBU framing and payload encryption")]
public static class UciInformDecoder
{
    public const int HeaderLength = 40;

    public const ushort FlagEncrypted = 0x01;
    public const ushort FlagZlib = 0x02;
    public const ushort FlagSnappy = 0x04;
    public const ushort FlagGcm = 0x08;

    /// <summary>Largest payload accepted; an inform is a few KB, so anything near this is not one.</summary>
    private const int MaxPayloadBytes = 1024 * 1024;

    /// <summary>Parses the header; null when the frame is not a TNBU frame.</summary>
    public static TnbuHeader? ParseHeader(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderLength || !frame[..4].SequenceEqual("TNBU"u8))
            return null;
        return new TnbuHeader(
            PacketVersion: BinaryPrimitives.ReadInt32BigEndian(frame.Slice(4, 4)),
            Mac: string.Join(':', frame.Slice(8, 6).ToArray().Select(b => b.ToString("x2"))),
            Flags: BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(14, 2)),
            Iv: frame.Slice(16, 16).ToArray(),
            DataVersion: BinaryPrimitives.ReadInt32BigEndian(frame.Slice(32, 4)),
            DataLength: BinaryPrimitives.ReadInt32BigEndian(frame.Slice(36, 4)));
    }

    /// <summary>Hex inform key to 16 bytes; null when it is not a 128-bit hex key.</summary>
    public static byte[]? ParseKey(string? hexKey)
    {
        if (string.IsNullOrWhiteSpace(hexKey) || hexKey.Trim().Length != 32) return null;
        try { return Convert.FromHexString(hexKey.Trim()); }
        catch (FormatException) { return null; }
    }

    /// <summary>Decrypts and decompresses a frame into its JSON bytes.</summary>
    public static UciDecodeFailure TryDecode(byte[] frame, byte[] key, out byte[] json)
    {
        json = Array.Empty<byte>();
        var header = ParseHeader(frame);
        if (header == null)
            return UciDecodeFailure.Malformed;

        var available = frame.Length - HeaderLength;
        var length = header.DataLength > 0 && header.DataLength <= available ? header.DataLength : available;
        if (length <= 0 || length > MaxPayloadBytes)
            return UciDecodeFailure.Malformed;
        if ((header.Flags & FlagSnappy) != 0)
            return UciDecodeFailure.Unsupported;

        byte[] plain;
        var payload = frame.AsSpan(HeaderLength, length).ToArray();
        if ((header.Flags & FlagGcm) != 0)
        {
            try
            {
                var cipher = new GcmBlockCipher(new AesEngine());
                cipher.Init(false, new AeadParameters(new KeyParameter(key), 128, header.Iv, frame.AsSpan(0, HeaderLength).ToArray()));
                plain = new byte[cipher.GetOutputSize(payload.Length)];
                var written = cipher.ProcessBytes(payload, 0, payload.Length, plain, 0);
                written += cipher.DoFinal(plain, written);
                if (written != plain.Length) Array.Resize(ref plain, written);
            }
            catch (InvalidCipherTextException)
            {
                return UciDecodeFailure.KeyMismatch;
            }
        }
        else if ((header.Flags & FlagEncrypted) != 0)
        {
            try
            {
                using var aes = Aes.Create();
                aes.Key = key;
                plain = aes.DecryptCbc(payload, header.Iv, PaddingMode.PKCS7);
            }
            catch (CryptographicException)
            {
                return UciDecodeFailure.KeyMismatch;
            }
        }
        else
        {
            plain = payload;
        }

        if ((header.Flags & FlagZlib) != 0)
        {
            try
            {
                using var input = new MemoryStream(plain);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                plain = output.ToArray();
            }
            catch (InvalidDataException)
            {
                // CBC has no authentication: a wrong key usually shows up here as garbage.
                return (header.Flags & FlagGcm) != 0 ? UciDecodeFailure.Malformed : UciDecodeFailure.KeyMismatch;
            }
        }

        json = plain;
        return UciDecodeFailure.None;
    }
}
