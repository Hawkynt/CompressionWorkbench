using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Xpk;

namespace FileFormat.Xpk;

/// <summary>
/// The Amiga XPK container (<c>XPKF</c>): a 36-byte stream header naming the sub-library,
/// then checksummed chunks (0 = stored, 1 = packed by the sub-library, 15 = end).
/// </summary>
/// <remarks>
/// Header: <c>XPKF</c>, packed length (file size − 8), sub-library id, raw length, the
/// first 16 raw bytes, flags (1 = long chunk headers, 2 = password, 4 = extra header),
/// a check byte making the XOR of the 36 bytes zero, two version bytes. Chunk header
/// (8 bytes, or 12 with long headers): type, check byte, 16-bit XOR of the chunk data
/// taken as words, packed length, raw length; chunk data is padded to 4 bytes.
/// Layout as decoded by ancient (Teemu Suutari, BSD-2-Clause).
/// </remarks>
public static class XpkStream {

  private const int HeaderSize = 36;
  private const int MaxChunk = 0x8000;

  /// <summary>Returns the sub-library id of an XPK stream.</summary>
  public static string ReadType(ReadOnlySpan<byte> data) {
    if (data.Length < HeaderSize || !data[..4].SequenceEqual("XPKF"u8))
      throw new InvalidDataException("XPK: missing XPKF header.");
    return Encoding.ASCII.GetString(data.Slice(8, 4));
  }

  /// <summary>Decodes a complete XPK stream.</summary>
  /// <exception cref="InvalidDataException">The stream is malformed or a checksum fails.</exception>
  /// <exception cref="NotSupportedException">Encrypted, or a sub-library not implemented.</exception>
  public static byte[] Decode(byte[] data) {
    var type = ReadType(data);
    var packedSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
    var rawSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12));
    if (packedSize == 0 || rawSize == 0 || packedSize + 8L > data.Length || rawSize > int.MaxValue)
      throw new InvalidDataException("XPK: header sizes out of range.");
    if (Xor(data.AsSpan(0, HeaderSize)) != 0)
      throw new InvalidDataException("XPK: header checksum mismatch.");
    var flags = data[32];
    if ((flags & 2) != 0)
      throw new NotSupportedException("XPK: password-protected streams are not supported.");
    var longHeaders = (flags & 1) != 0;
    var offset = (flags & 4) != 0 ? 38 + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(36)) : HeaderSize;
    var end = (int)(packedSize + 8);
    var chunkHeader = longHeaders ? 12 : 8;

    var output = new byte[rawSize];
    var o = 0;
    var last = false;
    while (!last) {
      if (offset + chunkHeader > end) throw new InvalidDataException("XPK: chunk header past the end of the stream.");
      var header = data.AsSpan(offset, chunkHeader);
      if (Xor(header) != 0) throw new InvalidDataException("XPK: chunk header checksum mismatch.");
      var chunkType = header[0];
      var check = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
      var cLen = longHeaders ? (int)BinaryPrimitives.ReadUInt32BigEndian(header[4..]) : BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
      var uLen = longHeaders ? (int)BinaryPrimitives.ReadUInt32BigEndian(header[8..]) : BinaryPrimitives.ReadUInt16BigEndian(header[6..]);
      var dataStart = offset + chunkHeader;
      if (cLen < 0 || dataStart + cLen > end) throw new InvalidDataException("XPK: chunk past the end of the stream.");
      var chunk = data.AsSpan(dataStart, cLen);
      if (cLen > 0 && WordXor(chunk) != check) throw new InvalidDataException("XPK: chunk checksum mismatch.");

      switch (chunkType) {
        case 0:
          if (uLen != cLen) throw new InvalidDataException("XPK: stored chunk sizes differ.");
          Copy(chunk);
          break;
        case 1:
          if (uLen > 0) Copy(XpkSubLibraries.Decode(type, chunk.ToArray(), uLen));
          break;
        case 15:
          last = true;
          break;
        default:
          throw new InvalidDataException($"XPK: unknown chunk type {chunkType}.");
      }
      offset = dataStart + ((cLen + 3) & ~3);
    }
    if (o != output.Length) throw new InvalidDataException($"XPK: decoded {o} bytes, header says {rawSize}.");
    if (!output.AsSpan(0, Math.Min(16, output.Length)).SequenceEqual(data.AsSpan(16, Math.Min(16, output.Length))))
      throw new InvalidDataException("XPK: first bytes disagree with the header copy.");
    return output;

    void Copy(ReadOnlySpan<byte> bytes) {
      if (o + bytes.Length > output.Length) throw new InvalidDataException("XPK: chunks exceed the raw size.");
      bytes.CopyTo(output.AsSpan(o));
      o += bytes.Length;
    }
  }

  /// <summary>
  /// Encodes <paramref name="data"/> with sub-library <paramref name="type"/>
  /// (<c>NONE</c> or <c>RLEN</c>); a chunk that does not shrink is stored.
  /// </summary>
  /// <exception cref="ArgumentException">Empty input (XPK has no empty streams) or unknown type.</exception>
  public static byte[] Encode(ReadOnlySpan<byte> data, string type = "RLEN") {
    if (data.Length == 0) throw new ArgumentException("XPK cannot hold an empty stream.", nameof(data));
    if (type is not ("NONE" or "RLEN")) throw new ArgumentException($"XPK sub-library '{type}' cannot be written.", nameof(type));

    using var ms = new MemoryStream();
    var header = new byte[HeaderSize];
    "XPKF"u8.CopyTo(header);
    Encoding.ASCII.GetBytes(type, header.AsSpan(8, 4));
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), (uint)data.Length);
    data[..Math.Min(16, data.Length)].CopyTo(header.AsSpan(16));
    ms.Write(header);

    for (var pos = 0; pos < data.Length; pos += MaxChunk) {
      var raw = data.Slice(pos, Math.Min(MaxChunk, data.Length - pos));
      byte chunkType = 0;
      ReadOnlySpan<byte> payload = raw;
      if (type == "RLEN") {
        var packed = XpkSubLibraries.EncodeRlen(raw);
        if (packed.Length < raw.Length) {
          chunkType = 1;
          payload = packed;
        }
      }
      WriteChunk(ms, chunkType, payload, raw.Length);
    }
    WriteChunk(ms, 15, [], 0);

    var result = ms.ToArray();
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), (uint)(result.Length - 8));
    result[33] = 0;
    result[33] = Xor(result.AsSpan(0, HeaderSize));
    return result;
  }

  private static void WriteChunk(Stream s, byte type, ReadOnlySpan<byte> payload, int rawLength) {
    Span<byte> h = stackalloc byte[8];
    h[0] = type;
    h[1] = 0;
    BinaryPrimitives.WriteUInt16BigEndian(h[2..], WordXor(payload));
    BinaryPrimitives.WriteUInt16BigEndian(h[4..], (ushort)payload.Length);
    BinaryPrimitives.WriteUInt16BigEndian(h[6..], (ushort)rawLength);
    h[1] = Xor(h);
    s.Write(h);
    s.Write(payload);
    for (var pad = (4 - payload.Length % 4) % 4; pad > 0; --pad) s.WriteByte(0);
  }

  private static byte Xor(ReadOnlySpan<byte> bytes) {
    byte x = 0;
    foreach (var b in bytes) x ^= b;
    return x;
  }

  private static ushort WordXor(ReadOnlySpan<byte> bytes) {
    byte hi = 0, lo = 0;
    for (var i = 0; i < bytes.Length; ++i)
      if ((i & 1) == 0) hi ^= bytes[i];
      else lo ^= bytes[i];
    return (ushort)((hi << 8) | lo);
  }
}
