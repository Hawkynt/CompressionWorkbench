#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.JpegArchive;

/// <summary>
/// Byte-level WebP chunk manipulation for XMP metadata. WebP is a RIFF
/// container: <c>"RIFF" + size(u32 LE) + "WEBP" + chunks</c>. Each chunk is
/// <c>[fourcc(4)] [size(u32 LE)] [payload] [optional pad byte to even size]</c>.
/// XMP lives in an <c>"XMP "</c> chunk (space-padded fourcc).
/// <para>
/// Simple WebP files use a single <c>"VP8 "</c> (lossy) or <c>"VP8L"</c>
/// (lossless) chunk and have no place for metadata. Extended WebP uses
/// <c>"VP8X"</c> as the first chunk with a 10-byte header whose flags word
/// advertises which optional chunks are present (bit 2 = XMP, bit 3 = EXIF,
/// bit 4 = alpha, bit 5 = animation, bit 8 = ICC). To add XMP to a simple
/// WebP we promote it to VP8X first.
/// </para>
/// </summary>
public static class WebpChunkSurgery {
  private const uint VP8XXmpFlag = 1u << 2;

  /// <summary>
  /// Replaces or inserts the <c>XMP </c> chunk in a WebP file. For simple
  /// WebPs, promotes to VP8X form so the XMP flag is set (readers will
  /// otherwise ignore the chunk). Canvas width/height are copied from the
  /// original VP8/VP8L bitstream.
  /// </summary>
  public static byte[] ReplaceXmpChunk(ReadOnlySpan<byte> input, byte[] xmpBytes) {
    ArgumentNullException.ThrowIfNull(xmpBytes);

    var parsed = Parse(input);

    // Promote to VP8X if we don't already have it.
    byte[] vp8x;
    if (parsed.Vp8x is null) {
      if (parsed.Bitstream is null)
        throw new InvalidDataException("WebP has no VP8/VP8L bitstream — cannot derive canvas size to promote to VP8X.");

      var (width, height) = DeriveCanvasSize(parsed.Bitstream);
      vp8x = BuildVp8x(width, height, xmpFlag: true);
    } else {
      // Set the XMP flag; keep other flags.
      vp8x = SetXmpFlag(parsed.Vp8x);
    }

    var newChunks = new List<RawChunk>();
    newChunks.Add(new RawChunk("VP8X", vp8x));

    // Keep every chunk that isn't XMP or VP8X; we'll append a fresh XMP at the end.
    foreach (var chunk in parsed.Chunks) {
      if (chunk.Fourcc is "VP8X" or "XMP ")
        continue;
      newChunks.Add(chunk);
    }

    newChunks.Add(new RawChunk("XMP ", xmpBytes));

    return BuildWebp(newChunks);
  }

  /// <summary>
  /// Returns the raw XMP chunk payload, or null if the WebP doesn't carry one.
  /// </summary>
  public static byte[]? TryReadXmpChunk(ReadOnlySpan<byte> input) {
    try {
      var parsed = Parse(input);
      foreach (var chunk in parsed.Chunks)
        if (chunk.Fourcc == "XMP ")
          return chunk.Payload;
    } catch (InvalidDataException) {
      return null;
    }
    return null;
  }

  internal sealed record RawChunk(string Fourcc, byte[] Payload);

  internal sealed record ParsedWebp(byte[]? Vp8x, byte[]? Bitstream, IReadOnlyList<RawChunk> Chunks);

  internal static ParsedWebp Parse(ReadOnlySpan<byte> input) {
    if (input.Length < 12)
      throw new InvalidDataException("WebP file is too short.");
    if (input[0] != 'R' || input[1] != 'I' || input[2] != 'F' || input[3] != 'F')
      throw new InvalidDataException("File is not RIFF.");
    if (input[8] != 'W' || input[9] != 'E' || input[10] != 'B' || input[11] != 'P')
      throw new InvalidDataException("RIFF form type is not WEBP.");

    var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(4, 4));
    var riffEnd = (int)Math.Min(8 + declaredSize, input.Length);

    var chunks = new List<RawChunk>();
    byte[]? vp8x = null;
    byte[]? bitstream = null;

    var pos = 12;
    while (pos + 8 <= riffEnd) {
      var fourcc = Encoding.ASCII.GetString(input.Slice(pos, 4));
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(pos + 4, 4));
      if (pos + 8 + size > input.Length)
        throw new InvalidDataException($"WebP chunk '{fourcc}' at offset {pos} declares size {size} but extends past file.");

      var payload = input.Slice(pos + 8, size).ToArray();

      if (fourcc == "VP8X") {
        vp8x = payload;
      } else if (fourcc is "VP8 " or "VP8L") {
        bitstream = payload;
      }
      chunks.Add(new RawChunk(fourcc, payload));

      // Chunks are padded to an even size.
      var advance = size + (size % 2);
      pos += 8 + advance;
    }

    return new ParsedWebp(vp8x, bitstream, chunks);
  }

  /// <summary>
  /// VP8X chunk layout (10 bytes):
  ///   flags(1) reserved(3) widthMinus1(24 LE) heightMinus1(24 LE)
  /// Flag bit 2 = XMP, bit 3 = EXIF, bit 4 = alpha, bit 5 = animation, bit 8 = ICC.
  /// </summary>
  private static byte[] BuildVp8x(int width, int height, bool xmpFlag) {
    var buffer = new byte[10];
    buffer[0] = xmpFlag ? (byte)VP8XXmpFlag : (byte)0;
    WriteUInt24LE(buffer, 4, (uint)(width - 1));
    WriteUInt24LE(buffer, 7, (uint)(height - 1));
    return buffer;
  }

  private static byte[] SetXmpFlag(byte[] vp8x) {
    if (vp8x.Length < 10)
      throw new InvalidDataException("VP8X chunk shorter than 10 bytes.");
    var clone = (byte[])vp8x.Clone();
    clone[0] = (byte)(clone[0] | (byte)VP8XXmpFlag);
    return clone;
  }

  private static void WriteUInt24LE(byte[] buffer, int offset, uint value) {
    buffer[offset]     = (byte)(value & 0xFF);
    buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
  }

  /// <summary>
  /// Extracts canvas dimensions from a VP8 or VP8L bitstream. VP8 (lossy)
  /// encodes width/height in bits 14..25 each; VP8L (lossless) uses two
  /// 14-bit fields in its 5-byte header.
  /// </summary>
  private static (int Width, int Height) DeriveCanvasSize(ReadOnlySpan<byte> bitstream) {
    // VP8L header: 1 signature byte (0x2F) + 4 bytes holding width-1(14) height-1(14) alpha(1) version(3).
    if (bitstream.Length >= 5 && bitstream[0] == 0x2F) {
      var bits = BinaryPrimitives.ReadUInt32LittleEndian(bitstream.Slice(1, 4));
      var w = (int)((bits & 0x3FFF) + 1);
      var h = (int)(((bits >> 14) & 0x3FFF) + 1);
      return (w, h);
    }

    // VP8 (lossy) keyframe: 3 uncompressed bytes + start code 9D 01 2A then width/height (u16 LE, with scale bits).
    for (var i = 0; i + 6 < bitstream.Length; i++) {
      if (bitstream[i] == 0x9D && bitstream[i + 1] == 0x01 && bitstream[i + 2] == 0x2A) {
        var w = BinaryPrimitives.ReadUInt16LittleEndian(bitstream.Slice(i + 3, 2)) & 0x3FFF;
        var h = BinaryPrimitives.ReadUInt16LittleEndian(bitstream.Slice(i + 5, 2)) & 0x3FFF;
        return (w, h);
      }
    }

    throw new InvalidDataException("Could not find VP8/VP8L dimension markers.");
  }

  private static byte[] BuildWebp(List<RawChunk> chunks) {
    using var body = new MemoryStream();
    foreach (var chunk in chunks) {
      if (chunk.Fourcc.Length != 4)
        throw new InvalidDataException($"Invalid chunk fourcc '{chunk.Fourcc}' — must be exactly 4 bytes.");
      body.Write(Encoding.ASCII.GetBytes(chunk.Fourcc), 0, 4);
      var sizeBytes = new byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)chunk.Payload.Length);
      body.Write(sizeBytes, 0, 4);
      body.Write(chunk.Payload, 0, chunk.Payload.Length);
      if (chunk.Payload.Length % 2 != 0)
        body.WriteByte(0);
    }

    var bodyBytes = body.ToArray();
    using var output = new MemoryStream();
    output.Write(Encoding.ASCII.GetBytes("RIFF"), 0, 4);
    var riffSize = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(riffSize, (uint)(4 + bodyBytes.Length));  // "WEBP" + chunks
    output.Write(riffSize, 0, 4);
    output.Write(Encoding.ASCII.GetBytes("WEBP"), 0, 4);
    output.Write(bodyBytes, 0, bodyBytes.Length);
    return output.ToArray();
  }
}
