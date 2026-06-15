#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Maud;

/// <summary>
/// IFF / MAUD ("MacroSystem audio") parser. The file is a big-endian IFF container —
/// <c>FORM</c> | uint32 size | <c>MAUD</c> | chunks — where each chunk is a 4-byte id,
/// a uint32 big-endian body length, the body and a pad byte when the length is odd.
/// Recognised chunks:
/// <list type="bullet">
///   <item><c>MHDR</c> — MaudHeader (32 bytes): sample count, the compressed and
///     uncompressed bit widths, the sample rate as a source/divide fraction, the
///     channel layout, the channel count and the compression mode.</item>
///   <item><c>MDAT</c> — sample data (signed PCM, 16-bit big-endian when 16-bit, or
///     A-law / μ-law bytes when the header marks it compressed).</item>
/// </list>
/// </summary>
public sealed class MaudReader {

  public const int CompressionNone = 0;
  public const int CompressionALaw = 2;
  public const int CompressionULaw = 3;

  public const int ChannelInfoMono = 0;
  public const int ChannelInfoStereo = 1;

  public sealed record ParsedMaud(
    uint SampleCount,
    int BitsCompressed,
    int BitsUncompressed,
    int SampleRate,
    int ChannelInfo,
    int NumChannels,
    int Compression,
    byte[] Data);

  public ParsedMaud Read(ReadOnlySpan<byte> data) {
    if (data.Length < 12)
      throw new InvalidDataException("MAUD too short for FORM header.");
    if (data[0] != 'F' || data[1] != 'O' || data[2] != 'R' || data[3] != 'M')
      throw new InvalidDataException("Missing FORM magic.");
    if (data[8] != 'M' || data[9] != 'A' || data[10] != 'U' || data[11] != 'D')
      throw new InvalidDataException("FORM type is not MAUD.");

    var formSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
    var end = Math.Min(data.Length, 8 + formSize);
    if (end > data.Length || end < 12) end = data.Length;

    uint sampleCount = 0;
    int bitsCompressed = 8, bitsUncompressed = 8, sampleRate = 0;
    int channelInfo = ChannelInfoMono, numChannels = 1, compression = CompressionNone;
    byte[]? body = null;

    var pos = 12;
    while (pos + 8 <= end) {
      var id = System.Text.Encoding.ASCII.GetString(data.Slice(pos, 4));
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 4)..]);
      var bodyStart = pos + 8;
      if (bodyStart + size > data.Length)
        throw new InvalidDataException($"MAUD chunk '{id}' truncated.");
      var chunk = data.Slice(bodyStart, size);

      switch (id) {
        case "MHDR":
          if (size >= 20) {
            sampleCount = BinaryPrimitives.ReadUInt32BigEndian(chunk);
            bitsCompressed = BinaryPrimitives.ReadUInt16BigEndian(chunk[4..]);
            bitsUncompressed = BinaryPrimitives.ReadUInt16BigEndian(chunk[6..]);
            var rateSource = BinaryPrimitives.ReadUInt32BigEndian(chunk[8..]);
            var rateDivide = BinaryPrimitives.ReadUInt16BigEndian(chunk[12..]);
            sampleRate = (int)(rateSource / Math.Max(1u, rateDivide));
            channelInfo = BinaryPrimitives.ReadUInt16BigEndian(chunk[14..]);
            numChannels = BinaryPrimitives.ReadUInt16BigEndian(chunk[16..]);
            compression = BinaryPrimitives.ReadUInt16BigEndian(chunk[18..]);
          }
          break;
        case "MDAT":
          body = chunk.ToArray();
          break;
      }

      // Chunks are word-aligned: skip a pad byte after an odd-length body.
      pos = bodyStart + size + (size & 1);
    }

    if (numChannels < 1) numChannels = 1;
    return new ParsedMaud(sampleCount, bitsCompressed, bitsUncompressed, sampleRate,
      channelInfo, numChannels, compression, body ?? []);
  }
}
