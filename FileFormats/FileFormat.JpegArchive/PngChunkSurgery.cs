#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.JpegArchive;

/// <summary>
/// Byte-level PNG chunk manipulation for XMP metadata. PNG layout:
///   8-byte signature (<c>89 50 4E 47 0D 0A 1A 0A</c>) then a sequence of
///   chunks, each of form <c>[len BE u32] [type BE u32] [data] [crc32 BE]</c>
///   where <c>len</c> counts data bytes only and <c>crc32</c> is computed
///   over type+data. Chunk order: IHDR first, IEND last; everything else in
///   between. XMP lives in an <c>iTXt</c> chunk with keyword
///   <c>XML:com.adobe.xmp</c>.
/// This module stays in the shared library so PNG, JPEG, and future formats
/// can share one home for low-level container ops.
/// </summary>
public static class PngChunkSurgery {
  private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
  private static readonly byte[] XmpKeyword = Encoding.ASCII.GetBytes("XML:com.adobe.xmp");

  /// <summary>
  /// Replaces or inserts an iTXt XMP chunk into <paramref name="input"/> so
  /// the resulting PNG carries <paramref name="xmpBytes"/>. Existing iTXt
  /// XMP chunks are removed; the new one is inserted right after IHDR where
  /// PNG readers reliably pick it up. Image data (IDAT) is untouched.
  /// </summary>
  public static byte[] ReplaceXmpChunk(ReadOnlySpan<byte> input, byte[] xmpBytes) {
    ArgumentNullException.ThrowIfNull(xmpBytes);
    if (!HasPngSignature(input))
      throw new InvalidDataException("File does not start with PNG signature.");

    using var output = new MemoryStream();
    output.Write(PngSignature, 0, PngSignature.Length);

    var pos = PngSignature.Length;
    var insertedXmp = false;

    while (pos + 12 <= input.Length) {
      var length = (int)BinaryPrimitives.ReadUInt32BigEndian(input.Slice(pos, 4));
      if (pos + 12 + length > input.Length)
        throw new InvalidDataException($"PNG chunk at offset {pos} declares length {length} but extends past file.");

      var type = Encoding.ASCII.GetString(input.Slice(pos + 4, 4));
      var chunkTotal = 12 + length;  // length + type + data + crc

      if (string.Equals(type, "iTXt", StringComparison.Ordinal) && IsXmpItxt(input.Slice(pos + 8, length))) {
        // Skip — we'll emit our new version.
        pos += chunkTotal;
        continue;
      }

      // Right after IHDR is the canonical place for XMP — insert before emitting the next chunk.
      if (!insertedXmp && !string.Equals(type, "IHDR", StringComparison.Ordinal)) {
        WriteXmpItxtChunk(output, xmpBytes);
        insertedXmp = true;
      }

      output.Write(input.Slice(pos, chunkTotal));

      if (string.Equals(type, "IEND", StringComparison.Ordinal))
        break;

      pos += chunkTotal;
    }

    if (!insertedXmp)
      throw new InvalidDataException("PNG has no chunks after IHDR — file is malformed.");

    return output.ToArray();
  }

  /// <summary>
  /// Returns the XMP UTF-8 payload from the first iTXt XMP chunk, or null.
  /// </summary>
  public static byte[]? TryReadXmpChunk(ReadOnlySpan<byte> input) {
    if (!HasPngSignature(input))
      return null;

    var pos = PngSignature.Length;
    while (pos + 12 <= input.Length) {
      var length = (int)BinaryPrimitives.ReadUInt32BigEndian(input.Slice(pos, 4));
      if (pos + 12 + length > input.Length)
        return null;

      var type = Encoding.ASCII.GetString(input.Slice(pos + 4, 4));
      if (string.Equals(type, "iTXt", StringComparison.Ordinal)) {
        var data = input.Slice(pos + 8, length);
        if (IsXmpItxt(data))
          return ExtractXmpTextFromItxt(data);
      }

      if (string.Equals(type, "IEND", StringComparison.Ordinal))
        break;

      pos += 12 + length;
    }

    return null;
  }

  private static bool HasPngSignature(ReadOnlySpan<byte> input) {
    if (input.Length < PngSignature.Length)
      return false;
    for (var i = 0; i < PngSignature.Length; i++)
      if (input[i] != PngSignature[i])
        return false;
    return true;
  }

  private static bool IsXmpItxt(ReadOnlySpan<byte> itxtData) {
    if (itxtData.Length < XmpKeyword.Length + 1)
      return false;
    for (var i = 0; i < XmpKeyword.Length; i++)
      if (itxtData[i] != XmpKeyword[i])
        return false;
    // Keyword must be followed by a NUL separator per the PNG iTXt spec.
    return itxtData[XmpKeyword.Length] == 0;
  }

  private static byte[]? ExtractXmpTextFromItxt(ReadOnlySpan<byte> itxtData) {
    // Layout: keyword NUL compressionFlag(1) compressionMethod(1) langTag NUL translatedKeyword NUL text
    if (!IsXmpItxt(itxtData))
      return null;

    var idx = XmpKeyword.Length + 1;  // past keyword + NUL
    if (idx + 2 > itxtData.Length) return null;
    var compressionFlag = itxtData[idx]; idx++;
    // compressionMethod byte skipped (we don't support compressed XMP on read — rare in practice).
    idx++;
    if (compressionFlag != 0)
      return null;

    // langTag up to NUL
    idx = IndexOfNull(itxtData, idx);
    if (idx < 0) return null;
    idx++;  // past langTag NUL

    // translatedKeyword up to NUL
    idx = IndexOfNull(itxtData, idx);
    if (idx < 0) return null;
    idx++;

    return itxtData[idx..].ToArray();
  }

  private static int IndexOfNull(ReadOnlySpan<byte> data, int start) {
    for (var i = start; i < data.Length; i++)
      if (data[i] == 0)
        return i;
    return -1;
  }

  private static void WriteXmpItxtChunk(Stream output, byte[] xmpBytes) {
    // iTXt data: keyword NUL compressionFlag(0) compressionMethod(0) langTag(empty) NUL translatedKey(empty) NUL text
    using var dataStream = new MemoryStream();
    dataStream.Write(XmpKeyword, 0, XmpKeyword.Length);
    dataStream.WriteByte(0);  // keyword NUL
    dataStream.WriteByte(0);  // compressionFlag (uncompressed)
    dataStream.WriteByte(0);  // compressionMethod
    dataStream.WriteByte(0);  // langTag NUL (empty)
    dataStream.WriteByte(0);  // translatedKey NUL (empty)
    dataStream.Write(xmpBytes, 0, xmpBytes.Length);
    var data = dataStream.ToArray();

    var typeBytes = Encoding.ASCII.GetBytes("iTXt");
    var lengthBytes = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)data.Length);

    // CRC-32 over type + data.
    var crcInput = new byte[typeBytes.Length + data.Length];
    Array.Copy(typeBytes, crcInput, typeBytes.Length);
    Array.Copy(data, 0, crcInput, typeBytes.Length, data.Length);
    var crc = ComputeCrc32(crcInput);
    var crcBytes = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);

    output.Write(lengthBytes, 0, 4);
    output.Write(typeBytes, 0, 4);
    output.Write(data, 0, data.Length);
    output.Write(crcBytes, 0, 4);
  }

  /// <summary>
  /// IEEE 802.3 CRC-32 (reversed 0xEDB88320 polynomial) — what PNG, zlib,
  /// and plenty else use. Built lazily; the table lives in <see cref="Crc32Table"/>.
  /// </summary>
  internal static uint ComputeCrc32(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    foreach (var b in data)
      crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFFu;
  }

  private static readonly uint[] Crc32Table = BuildCrc32Table();

  private static uint[] BuildCrc32Table() {
    var table = new uint[256];
    for (var i = 0u; i < 256; i++) {
      var c = i;
      for (var k = 0; k < 8; k++)
        c = ((c & 1) != 0) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
      table[i] = c;
    }
    return table;
  }
}
