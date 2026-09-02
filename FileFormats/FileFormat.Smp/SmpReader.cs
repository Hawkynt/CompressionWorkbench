#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Smp;

/// <summary>
/// Turtle Beach SampleVision (.smp) parser. A little-endian, mono-only sample format:
/// <list type="bullet">
///   <item>char[18] magic <c>"SOUND SAMPLE DATA "</c>.</item>
///   <item>char[4] version (e.g. <c>"2.1 "</c>).</item>
///   <item>char[60] comment, char[30] name.</item>
///   <item>uint32 sample count, then that many signed 16-bit little-endian samples.</item>
///   <item>trailer: 8 loop records (uint32 start, uint32 end, byte type, uint16 count),
///     8 markers (char[10] name + uint32 position), byte MIDI unity note,
///     uint32 sample rate in Hz.</item>
/// </list>
/// </summary>
public sealed class SmpReader {

  /// <summary>
  /// Defines the magic constant value.
  /// </summary>
  public const string Magic = "SOUND SAMPLE DATA ";
  /// <summary>
  /// Defines the magic length constant value.
  /// </summary>
  public const int MagicLength = 18;
  /// <summary>
  /// Defines the header size constant value.
  /// </summary>
  public const int HeaderSize = 18 + 4 + 60 + 30; // 112: magic + version + comment + name
  /// <summary>
  /// Defines the loop record size constant value.
  /// </summary>
  public const int LoopRecordSize = 4 + 4 + 1 + 2;  // 11
  /// <summary>
  /// Defines the marker record size constant value.
  /// </summary>
  public const int MarkerRecordSize = 10 + 4;       // 14
  /// <summary>
  /// Defines the loop count constant value.
  /// </summary>
  public const int LoopCount = 8;
  /// <summary>
  /// Defines the marker count constant value.
  /// </summary>
  public const int MarkerCount = 8;
  // trailer after the samples: loops + markers + MIDI unity byte + uint32 rate.
  /// <summary>
  /// Defines the trailer size constant value.
  /// </summary>
  public const int TrailerSize = LoopCount * LoopRecordSize + MarkerCount * MarkerRecordSize + 1 + 4;

  /// <summary>
  /// Represents a parsed smp.
  /// </summary>
  public sealed record ParsedSmp(
    string Version,
    string Comment,
    string Name,
    uint SampleCount,
    int SampleRate,
    int MidiUnity,
    byte[] SamplesLe);

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public ParsedSmp Read(ReadOnlySpan<byte> data) {
    if (data.Length < HeaderSize + 4)
      throw new InvalidDataException("SMP too short for header + sample count.");
    if (!data[..MagicLength].SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
      throw new InvalidDataException("Missing SampleVision magic.");

    var version = TrimText(data.Slice(18, 4));
    var comment = TrimText(data.Slice(22, 60));
    var name = TrimText(data.Slice(82, 30));
    var sampleCount = BinaryPrimitives.ReadUInt32LittleEndian(data[112..]);

    var samplesStart = HeaderSize + 4;
    var sampleBytes = checked((int)sampleCount * 2);
    if (samplesStart + sampleBytes > data.Length)
      throw new InvalidDataException("SMP sample data truncated.");
    var samplesLe = data.Slice(samplesStart, sampleBytes).ToArray();

    var sampleRate = 0;
    var midiUnity = 60;
    var trailerStart = samplesStart + sampleBytes;
    if (trailerStart + TrailerSize <= data.Length) {
      var rateOffset = trailerStart + LoopCount * LoopRecordSize + MarkerCount * MarkerRecordSize;
      midiUnity = data[rateOffset];
      sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(rateOffset + 1)..]);
    }

    return new ParsedSmp(version, comment, name, sampleCount, sampleRate, midiUnity, samplesLe);
  }

  private static string TrimText(ReadOnlySpan<byte> raw) {
    var len = raw.Length;
    while (len > 0 && (raw[len - 1] == 0 || raw[len - 1] == ' ')) --len;
    return Encoding.ASCII.GetString(raw[..len]);
  }
}
