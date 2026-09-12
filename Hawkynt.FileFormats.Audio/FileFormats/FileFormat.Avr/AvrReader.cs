#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Avr;

/// <summary>
/// AVR (Audio Visual Research, Atari ST / Mac) parser. A fixed 128-byte big-endian
/// header precedes the sample data:
/// <list type="bullet">
///   <item>4-byte magic <c>2BIT</c>.</item>
///   <item>char[8] sample name.</item>
///   <item>uint16 mono flag (0 = mono, 0xFFFF = stereo).</item>
///   <item>uint16 resolution (8 or 16 bits).</item>
///   <item>uint16 sign (0 = unsigned, 0xFFFF = signed).</item>
///   <item>uint16 loop, uint16 midi.</item>
///   <item>uint32 rate — only the low 24 bits are the sample rate; the high byte is a
///     flags byte.</item>
///   <item>uint32 size in samples, uint32 loop begin, uint32 loop end.</item>
///   <item>char[26] reserved, char[64] user comment.</item>
/// </list>
/// Sample data follows (interleaved when stereo, big-endian when 16-bit).
/// </summary>
public sealed class AvrReader {

  /// <summary>
  /// Defines the header size constant value.
  /// </summary>
  public const int HeaderSize = 128;

  /// <summary>
  /// Represents a parsed avr.
  /// </summary>
  public sealed record ParsedAvr(
    string Name,
    int NumChannels,
    int BitsPerSample,
    bool Signed,
    int SampleRate,
    uint SizeInSamples,
    uint LoopBegin,
    uint LoopEnd,
    string User,
    byte[] SampleData);

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public ParsedAvr Read(ReadOnlySpan<byte> data) {
    if (data.Length < HeaderSize)
      throw new InvalidDataException("AVR too short for 128-byte header.");
    if (data[0] != '2' || data[1] != 'B' || data[2] != 'I' || data[3] != 'T')
      throw new InvalidDataException("Missing 2BIT magic.");

    var name = TrimText(data.Slice(4, 8));
    var monoFlag = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
    var resolution = BinaryPrimitives.ReadUInt16BigEndian(data[14..]);
    var signFlag = BinaryPrimitives.ReadUInt16BigEndian(data[16..]);
    // loop (18) and midi (20) are not surfaced.
    var rateRaw = BinaryPrimitives.ReadUInt32BigEndian(data[22..]);
    var sizeInSamples = BinaryPrimitives.ReadUInt32BigEndian(data[26..]);
    var loopBegin = BinaryPrimitives.ReadUInt32BigEndian(data[30..]);
    var loopEnd = BinaryPrimitives.ReadUInt32BigEndian(data[34..]);
    var user = TrimText(data.Slice(64, 64));

    var channels = monoFlag == 0 ? 1 : 2;
    var bits = resolution == 16 ? 16 : 8;
    var signed = signFlag != 0;
    var sampleRate = (int)(rateRaw & 0x00FFFFFF); // high byte is a flags byte

    var sampleData = data[HeaderSize..].ToArray();

    return new ParsedAvr(name, channels, bits, signed, sampleRate, sizeInSamples,
      loopBegin, loopEnd, user, sampleData);
  }

  private static string TrimText(ReadOnlySpan<byte> raw) {
    var len = raw.Length;
    // AVR strings are NUL- or space-padded.
    while (len > 0 && (raw[len - 1] == 0 || raw[len - 1] == ' ')) --len;
    return Encoding.ASCII.GetString(raw[..len]);
  }
}
