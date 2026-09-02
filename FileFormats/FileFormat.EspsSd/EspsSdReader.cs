#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.EspsSd;

/// <summary>
/// Parser for the common single-channel 16-bit Entropic ESPS sampled-data (<c>.sd</c>)
/// file. ESPS headers are self-describing, but a small fixed preamble pins the byte
/// order and the offset of the sample data:
/// <list type="bullet">
///   <item>A 4-byte <b>check code</b> <c>0x00006A1A</c> sits at offset 16. Reading it
///     big-endian vs little-endian reveals the file's byte order (it is the magic and
///     the endianness oracle in one).</item>
///   <item>The <b>data offset</b> (header size in bytes) is a 32-bit field at offset 8,
///     in the detected byte order.</item>
/// </list>
/// The sample rate is stored as a named generic header item; we scan the header region
/// for the ASCII tag <c>record_freq</c> and read the following IEEE-754 double in the
/// file's byte order, defaulting to 16000 Hz when the tag is absent. Sample data is the
/// 16-bit integers from the data offset to end-of-file.
/// </summary>
public sealed class EspsSdReader {

    /// <summary>
  /// Defines the check offset constant value.
  /// </summary>
public const int CheckOffset = 16;
    /// <summary>
  /// Defines the check code constant value.
  /// </summary>
public const uint CheckCode = 0x00006A1A;
  private const int DefaultRate = 16000;

    /// <summary>
  /// Represents a parsed esps.
  /// </summary>
public sealed record ParsedEsps(
    bool BigEndian,
    int DataOffset,
    int SampleRate,
    bool RateFromHeader,
    byte[] SampleData);

    /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
public ParsedEsps Read(ReadOnlySpan<byte> data) {
    if (data.Length < CheckOffset + 4)
      throw new InvalidDataException("ESPS .sd too short for a preamble.");

    bool bigEndian;
    if (BinaryPrimitives.ReadUInt32BigEndian(data[CheckOffset..]) == CheckCode)
      bigEndian = true;
    else if (BinaryPrimitives.ReadUInt32LittleEndian(data[CheckOffset..]) == CheckCode)
      bigEndian = false;
    else
      throw new InvalidDataException("ESPS .sd check code 0x00006A1A not found at offset 16.");

    var dataOffset = (int)(bigEndian
      ? BinaryPrimitives.ReadUInt32BigEndian(data[8..])
      : BinaryPrimitives.ReadUInt32LittleEndian(data[8..]));

    if (dataOffset <= 0 || dataOffset > data.Length)
      throw new InvalidDataException($"ESPS .sd data offset {dataOffset} is out of range.");

    var (rate, fromHeader) = ScanRecordFreq(data[..dataOffset], bigEndian);

    // 16-bit samples, byte-swapped to little-endian for canonical WAV output.
    var raw = data[dataOffset..];
    var sampleData = bigEndian ? SwapEndianness(raw, 2) : raw.ToArray();

    return new ParsedEsps(bigEndian, dataOffset, rate, fromHeader, sampleData);
  }

  private static (int rate, bool fromHeader) ScanRecordFreq(ReadOnlySpan<byte> header, bool bigEndian) {
    var tag = "record_freq"u8;
    for (var i = 0; i + tag.Length + 8 <= header.Length; ++i) {
      if (!header.Slice(i, tag.Length).SequenceEqual(tag)) continue;
      // The named double follows the tag; ESPS rounds the tag's stored field, so scan
      // the bytes just past the tag for the first plausible IEEE double.
      for (var p = i + tag.Length; p + 8 <= header.Length; ++p) {
        var bits = bigEndian
          ? BinaryPrimitives.ReadUInt64BigEndian(header[p..])
          : BinaryPrimitives.ReadUInt64LittleEndian(header[p..]);
        var value = BitConverter.Int64BitsToDouble((long)bits);
        if (value is > 100 and < 1_000_000)
          return ((int)Math.Round(value), true);
      }
    }
    return (DefaultRate, false);
  }

  private static byte[] SwapEndianness(ReadOnlySpan<byte> pcm, int bytesPerSample) {
    var len = pcm.Length - pcm.Length % bytesPerSample;
    var swapped = new byte[len];
    for (var i = 0; i + bytesPerSample <= pcm.Length; i += bytesPerSample)
      for (var j = 0; j < bytesPerSample; ++j)
        swapped[i + j] = pcm[i + bytesPerSample - 1 - j];
    return swapped;
  }
}
