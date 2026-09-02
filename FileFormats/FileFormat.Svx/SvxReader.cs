#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Svx;

/// <summary>
/// IFF / 8SVX (Amiga "8-bit sampled voice") parser. The file is a big-endian IFF
/// container — <c>FORM</c> | uint32 size | <c>8SVX</c> | chunks — where each chunk
/// is a 4-byte id, a uint32 big-endian body length, the body, and a pad byte when
/// the length is odd. Recognised chunks:
/// <list type="bullet">
///   <item><c>VHDR</c> — Voice8Header: one-shot / repeat sample counts, samples per
///     high-octave cycle, sample rate, octave count, compression mode and volume.</item>
///   <item><c>CHAN</c> — channel allocation: 2 = left, 4 = right, 6 = stereo. For
///     stereo the body stores all left samples then all right samples (planar
///     halves) of octave 0.</item>
///   <item><c>BODY</c> — 8-bit signed PCM, or Fibonacci-delta compressed bytes when
///     <c>VHDR.sCompression</c> is 1.</item>
///   <item><c>NAME</c> / <c>ANNO</c> / <c>AUTH</c> / <c>(c) </c> — text metadata.</item>
/// </list>
/// </summary>
public sealed class SvxReader {

    /// <summary>
  /// Defines the compression none constant value.
  /// </summary>
public const int CompressionNone = 0;
    /// <summary>
  /// Defines the compression fibonacci constant value.
  /// </summary>
public const int CompressionFibonacci = 1;

    /// <summary>
  /// Defines the channel left constant value.
  /// </summary>
public const int ChannelLeft = 2;
    /// <summary>
  /// Defines the channel right constant value.
  /// </summary>
public const int ChannelRight = 4;
    /// <summary>
  /// Defines the channel stereo constant value.
  /// </summary>
public const int ChannelStereo = 6;

    /// <summary>
  /// Represents a parsed svx.
  /// </summary>
public sealed record ParsedSvx(
    uint OneShotHiSamples,
    uint RepeatHiSamples,
    uint SamplesPerHiCycle,
    int SampleRate,
    int Octaves,
    int Compression,
    int Channels,
    byte[] Body,
    IReadOnlyList<(string Id, string Text)> Tags);

    /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
public ParsedSvx Read(ReadOnlySpan<byte> data) {
    if (data.Length < 12)
      throw new InvalidDataException("8SVX too short for FORM header.");
    if (data[0] != 'F' || data[1] != 'O' || data[2] != 'R' || data[3] != 'M')
      throw new InvalidDataException("Missing FORM magic.");
    if (data[8] != '8' || data[9] != 'S' || data[10] != 'V' || data[11] != 'X')
      throw new InvalidDataException("FORM type is not 8SVX.");

    var formSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
    var end = Math.Min(data.Length, 8 + formSize);
    if (end > data.Length || end < 12) end = data.Length;

    uint oneShot = 0, repeat = 0, perCycle = 0;
    var sampleRate = 0;
    var octaves = 1;
    var compression = CompressionNone;
    var channels = ChannelLeft;
    byte[]? body = null;
    var tags = new List<(string, string)>();

    var pos = 12;
    while (pos + 8 <= end) {
      var id = Encoding.ASCII.GetString(data.Slice(pos, 4));
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 4)..]);
      var bodyStart = pos + 8;
      if (bodyStart + size > data.Length)
        throw new InvalidDataException($"8SVX chunk '{id}' truncated.");
      var chunk = data.Slice(bodyStart, size);

      switch (id) {
        case "VHDR":
          if (size >= 20) {
            oneShot = BinaryPrimitives.ReadUInt32BigEndian(chunk);
            repeat = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
            perCycle = BinaryPrimitives.ReadUInt32BigEndian(chunk[8..]);
            sampleRate = BinaryPrimitives.ReadUInt16BigEndian(chunk[12..]);
            octaves = chunk[14];
            compression = chunk[15];
          }
          break;
        case "CHAN":
          if (size >= 4) channels = (int)BinaryPrimitives.ReadUInt32BigEndian(chunk);
          break;
        case "BODY":
          body = chunk.ToArray();
          break;
        case "NAME":
        case "ANNO":
        case "AUTH":
        case "(c) ":
          tags.Add((id, TrimText(chunk)));
          break;
      }

      // Chunks are word-aligned: skip a pad byte after an odd-length body.
      pos = bodyStart + size + (size & 1);
    }

    if (octaves < 1) octaves = 1;
    return new ParsedSvx(oneShot, repeat, perCycle, sampleRate, octaves, compression,
      channels, body ?? [], tags);
  }

  private static string TrimText(ReadOnlySpan<byte> raw) {
    var len = raw.Length;
    while (len > 0 && raw[len - 1] == 0) --len;
    return Encoding.ASCII.GetString(raw[..len]);
  }

  // Fibonacci/exponential delta table used by 8SVX sCompression == 1.
  private static readonly sbyte[] _codeToDelta =
    [-34, -21, -13, -8, -5, -3, -2, -1, 0, 1, 2, 3, 5, 8, 13, 21];

  /// <summary>
  /// Decodes Fibonacci-delta compressed sample bytes. The stream begins with one
  /// pad byte and one initial sample value; every subsequent byte holds two 4-bit
  /// codes (high nibble first) that index <see cref="_codeToDelta"/>. Each output
  /// sample is the previous sample plus the delta, wrapped to a signed byte. The
  /// decoded length is <c>2 * (compressed.Length - 2)</c>.
  /// </summary>
  public static byte[] DecodeFibonacciDelta(ReadOnlySpan<byte> compressed) {
    if (compressed.Length < 2) return [];
    var output = new byte[2 * (compressed.Length - 2)];
    var current = unchecked((sbyte)compressed[1]);
    var o = 0;
    for (var i = 2; i < compressed.Length; ++i) {
      var packed = compressed[i];
      var highCode = (packed >> 4) & 0x0F;
      var lowCode = packed & 0x0F;
      current = unchecked((sbyte)(current + _codeToDelta[highCode]));
      output[o++] = unchecked((byte)current);
      current = unchecked((sbyte)(current + _codeToDelta[lowCode]));
      output[o++] = unchecked((byte)current);
    }
    return output;
  }
}
