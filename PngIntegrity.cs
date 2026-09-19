using System.Buffers.Binary;
using System.Text;

namespace MicrosoftPurviewScreenGuard;

/// <summary>
/// Structural check of a PNG file: signature, chunk lengths, the CRC of every chunk, and IHDR first / IDAT / IEND present.
/// It exists because GDI+ happily decodes a truncated or partly corrupted PNG into half a picture without any error.
/// </summary>
internal static class PngIntegrity
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly uint[] CrcTable = BuildTable();

    /// <summary>Returns null when the data is not a PNG (the image decoder decides) or is an intact PNG; otherwise the damage.</summary>
    public static string? Check(ReadOnlySpan<byte> data)
    {
        if (data.Length < Signature.Length || !data[..Signature.Length].SequenceEqual(Signature))
        {
            return null;
        }

        int pos = Signature.Length;
        bool first = true;
        bool sawIdat = false;
        bool sawIend = false;

        while (pos + 12 <= data.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
            if (length > int.MaxValue || pos + 12L + length > data.Length)
            {
                return "PNG is truncated (a chunk runs past the end of the file)";
            }

            ReadOnlySpan<byte> typeAndData = data.Slice(pos + 4, 4 + (int)length);
            string type = Encoding.ASCII.GetString(typeAndData[..4]);
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos + 8 + (int)length, 4));
            if (Crc32(typeAndData) != stored)
            {
                return $"PNG chunk {type} failed its CRC check (the file is corrupt)";
            }

            if (first && type != "IHDR")
            {
                return "PNG does not start with an IHDR chunk";
            }

            first = false;
            sawIdat |= type == "IDAT";
            pos += 12 + (int)length;

            if (type == "IEND")
            {
                sawIend = true;
                break;
            }
        }

        if (first || !sawIdat || !sawIend)
        {
            return "PNG is incomplete (IHDR, IDAT or IEND missing)";
        }

        return null;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
