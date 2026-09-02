#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Paf;

/// <summary>
/// Ensoniq PARIS Audio File (.paf) parser. The 24-byte header is stored in the file's
/// own byte order, which the four-byte magic announces: <c>" paf"</c> marks a
/// big-endian file, <c>"fap "</c> a little-endian one. Header fields:
/// <list type="bullet">
///   <item>uint32 magic, uint32 version.</item>
///   <item>uint32 endianness (0 = big, 1 = little — informational; the magic is
///     authoritative).</item>
///   <item>uint32 sample rate.</item>
///   <item>uint32 format (0 = 16-bit PCM, 1 = 24-bit PCM packed 3-byte LSB-first).</item>
///   <item>uint32 channel count.</item>
/// </list>
/// Sample data begins at offset 2048, interleaved, in the file's byte order.
/// </summary>
public sealed class PafReader {

    /// <summary>
  /// Defines the data offset constant value.
  /// </summary>
public const int DataOffset = 2048;

    /// <summary>
  /// Defines the format pcm 16 constant value.
  /// </summary>
public const int FormatPcm16 = 0;
    /// <summary>
  /// Defines the format pcm 24 constant value.
  /// </summary>
public const int FormatPcm24 = 1;

    /// <summary>
  /// Represents a parsed paf.
  /// </summary>
public sealed record ParsedPaf(
    bool LittleEndian,
    uint Version,
    int SampleRate,
    int Format,
    int NumChannels,
    byte[] Data);

    /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
public ParsedPaf Read(ReadOnlySpan<byte> data) {
    if (data.Length < 24)
      throw new InvalidDataException("PAF too short for header.");

    bool littleEndian;
    if (data[0] == ' ' && data[1] == 'p' && data[2] == 'a' && data[3] == 'f')
      littleEndian = false; // " paf" → big-endian file
    else if (data[0] == 'f' && data[1] == 'a' && data[2] == 'p' && data[3] == ' ')
      littleEndian = true;  // "fap " → little-endian file
    else
      throw new InvalidDataException("Missing PAF magic (' paf' or 'fap ').");

    var version = ReadU32(data, 4, littleEndian);
    // endianness field at offset 8 is informational; the magic is authoritative.
    var sampleRate = (int)ReadU32(data, 12, littleEndian);
    var format = (int)ReadU32(data, 16, littleEndian);
    var channels = (int)ReadU32(data, 20, littleEndian);
    if (channels < 1) channels = 1;

    var dataStart = Math.Min(DataOffset, data.Length);
    var body = data[dataStart..].ToArray();

    return new ParsedPaf(littleEndian, version, sampleRate, format, channels, body);
  }

  private static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    => littleEndian
      ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..])
      : BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
}
