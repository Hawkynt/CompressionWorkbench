using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Compression.Core.Crypto.OpenPgp;

/// <summary>One OpenPGP packet: its tag and its whole body (partial body lengths joined).</summary>
public sealed record OpenPgpPacket(int Tag, byte[] Body);

/// <summary>
/// The OpenPGP packet framing (RFC 9580 section 4.2, RFC 4880 section 4.2): old-format headers
/// with one-, two-, four-byte or indeterminate lengths, new-format headers with one-, two- and
/// five-byte and partial body lengths; MPIs (section 3.2) and ASCII armor (section 6).
/// </summary>
public static class OpenPgpPackets {
  /// <summary>Splits <paramref name="data"/> (binary or ASCII-armored) into packets.</summary>
  /// <exception cref="InvalidDataException">The packet framing is damaged.</exception>
  public static List<OpenPgpPacket> Read(ReadOnlySpan<byte> data) {
    if (LooksArmored(data))
      return Read(Dearmor(Encoding.ASCII.GetString(data)));
    var packets = new List<OpenPgpPacket>();
    var pos = 0;
    while (pos < data.Length) {
      var ctb = data[pos++];
      if ((ctb & 0x80) == 0)
        throw new InvalidDataException($"Byte 0x{ctb:X2} at {pos - 1} does not start an OpenPGP packet.");
      int tag;
      using var body = new MemoryStream();
      if ((ctb & 0x40) == 0) { // old format
        tag = (ctb >> 2) & 0x0F;
        long length = (ctb & 3) switch {
          0 => Need(data, pos, 1)[0],
          1 => BinaryPrimitives.ReadUInt16BigEndian(Need(data, pos, 2)),
          2 => BinaryPrimitives.ReadUInt32BigEndian(Need(data, pos, 4)),
          _ => -1,
        };
        pos += (ctb & 3) switch { 0 => 1, 1 => 2, 2 => 4, _ => 0 };
        if (length < 0)
          length = data.Length - pos; // indeterminate: to the end of the data
        body.Write(Need(data, pos, length));
        pos += (int)length;
      } else {
        tag = ctb & 0x3F;
        while (true) {
          var first = Need(data, pos, 1)[0];
          long length;
          var partial = false;
          if (first < 192) {
            length = first;
            pos += 1;
          } else if (first < 224) {
            length = ((first - 192) << 8) + Need(data, pos + 1, 1)[0] + 192;
            pos += 2;
          } else if (first == 255) {
            length = BinaryPrimitives.ReadUInt32BigEndian(Need(data, pos + 1, 4));
            pos += 5;
          } else {
            length = 1L << (first & 0x1F);
            partial = true;
            pos += 1;
          }
          body.Write(Need(data, pos, length));
          pos += (int)length;
          if (!partial)
            break;
        }
      }
      packets.Add(new OpenPgpPacket(tag, body.ToArray()));
    }
    return packets;
  }

  private static ReadOnlySpan<byte> Need(ReadOnlySpan<byte> data, int pos, long length) {
    if (length < 0 || pos + length > data.Length)
      throw new InvalidDataException("An OpenPGP packet runs past the end of its data.");
    return data.Slice(pos, (int)length);
  }

  /// <summary>Writes a new-format packet header and body.</summary>
  public static void Write(Stream output, int tag, ReadOnlySpan<byte> body) {
    output.WriteByte((byte)(0xC0 | tag));
    var length = body.Length;
    if (length < 192)
      output.WriteByte((byte)length);
    else if (length < 8384) {
      output.WriteByte((byte)(((length - 192) >> 8) + 192));
      output.WriteByte((byte)((length - 192) & 0xFF));
    } else {
      output.WriteByte(255);
      Span<byte> four = stackalloc byte[4];
      BinaryPrimitives.WriteUInt32BigEndian(four, (uint)length);
      output.Write(four);
    }
    output.Write(body);
  }

  /// <summary>Reads an MPI: a two-byte bit count and the big-endian magnitude.</summary>
  public static byte[] ReadMpi(ReadOnlySpan<byte> body, ref int pos) {
    var bits = BinaryPrimitives.ReadUInt16BigEndian(Need(body, pos, 2));
    var bytes = (bits + 7) / 8;
    var value = Need(body, pos + 2, bytes).ToArray();
    pos += 2 + bytes;
    return value;
  }

  /// <summary>Writes <paramref name="magnitude"/> (big-endian, leading zeros dropped) as an MPI.</summary>
  public static void WriteMpi(Stream output, ReadOnlySpan<byte> magnitude) {
    var start = 0;
    while (start < magnitude.Length && magnitude[start] == 0)
      ++start;
    var value = magnitude[start..];
    var bits = value.Length == 0 ? 0 : (value.Length - 1) * 8 + (32 - BitOperations.LeadingZeroCount((uint)value[0]));
    output.WriteByte((byte)(bits >> 8));
    output.WriteByte((byte)bits);
    output.Write(value);
  }

  private static bool LooksArmored(ReadOnlySpan<byte> data) {
    var head = data[..Math.Min(data.Length, 64)];
    return head.IndexOf("-----BEGIN PGP"u8) >= 0;
  }

  /// <summary>The binary data of an ASCII-armored block; the CRC-24 line, when present, is checked.</summary>
  /// <exception cref="InvalidDataException">The armor is malformed or its checksum differs.</exception>
  public static byte[] Dearmor(string text) {
    var lines = text.Replace("\r", "").Split('\n');
    var begin = Array.FindIndex(lines, l => l.StartsWith("-----BEGIN PGP", StringComparison.Ordinal));
    if (begin < 0)
      throw new InvalidDataException("No OpenPGP armor header.");
    var at = begin + 1;
    while (at < lines.Length && lines[at].Length > 0)
      ++at; // armor headers up to the blank line
    var base64 = new StringBuilder();
    string? crc = null;
    for (++at; at < lines.Length && !lines[at].StartsWith("-----END PGP", StringComparison.Ordinal); ++at) {
      var line = lines[at].Trim();
      if (line.StartsWith('='))
        crc = line[1..];
      else
        base64.Append(line);
    }
    byte[] data;
    try {
      data = Convert.FromBase64String(base64.ToString());
    } catch (FormatException e) {
      throw new InvalidDataException($"The OpenPGP armor holds no valid base64: {e.Message}", e);
    }
    if (crc is { Length: > 0 }) {
      var expected = Convert.FromBase64String(crc);
      var actual = Crc24(data);
      if (expected.Length != 3 || expected[0] != (byte)(actual >> 16) || expected[1] != (byte)(actual >> 8) || expected[2] != (byte)actual)
        throw new InvalidDataException("The OpenPGP armor checksum does not match.");
    }
    return data;
  }

  /// <summary>The CRC-24 of RFC 9580 section 6.1.1 (init 0xB704CE, polynomial 0x864CFB).</summary>
  public static uint Crc24(ReadOnlySpan<byte> data) {
    var crc = 0xB704CEu;
    foreach (var b in data) {
      crc ^= (uint)b << 16;
      for (var i = 0; i < 8; ++i) {
        crc <<= 1;
        if ((crc & 0x1000000) != 0)
          crc ^= 0x864CFB;
      }
    }
    return crc & 0xFFFFFF;
  }
}
