#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Pvf;

/// <summary>
/// mgetty Portable Voice Format (.pvf) parser. The file opens with two ASCII header
/// lines:
/// <list type="bullet">
///   <item>line 1 — the magic <c>"PVF1"</c> (binary samples) or <c>"PVF2"</c> (ASCII
///     samples).</item>
///   <item>line 2 — three space-separated decimal integers: channel count, sample rate
///     and bits per sample.</item>
/// </list>
/// PVF1 sample data is one big-endian signed 32-bit integer per sample, of which only
/// the low <c>bits</c> are significant. PVF2 sample data is whitespace-separated ASCII
/// decimal integers. Both are decoded here to 32-bit integer samples (still significant
/// in their original <c>bits</c> width); <see cref="PvfFormatDescriptor"/> shifts them
/// to 16-bit.
/// </summary>
public sealed class PvfReader {

  /// <summary>
  /// Represents a parsed pvf.
  /// </summary>
  public sealed record ParsedPvf(
    bool Ascii,
    int NumChannels,
    int SampleRate,
    int Bits,
    int[] Samples);

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public ParsedPvf Read(ReadOnlySpan<byte> data) {
    if (data.Length < 5 || data[0] != 'P' || data[1] != 'V' || data[2] != 'F')
      throw new InvalidDataException("Missing PVF magic.");

    var ascii = data[3] switch {
      (byte)'1' => false,
      (byte)'2' => true,
      _ => throw new InvalidDataException("Unsupported PVF version (expected PVF1 or PVF2)."),
    };

    var line1End = IndexOf(data, (byte)'\n', 0);
    if (line1End < 0) throw new InvalidDataException("PVF missing header line break.");
    var line2End = IndexOf(data, (byte)'\n', line1End + 1);
    if (line2End < 0) throw new InvalidDataException("PVF missing second header line.");

    var line2 = Encoding.ASCII.GetString(data.Slice(line1End + 1, line2End - line1End - 1));
    var parts = line2.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 3)
      throw new InvalidDataException("PVF header line 2 needs channels, rate and bits.");
    var channels = int.Parse(parts[0]);
    var sampleRate = int.Parse(parts[1]);
    var bits = int.Parse(parts[2]);
    if (channels < 1) channels = 1;

    var dataStart = line2End + 1;
    var body = data[dataStart..];

    var samples = ascii ? ParseAsciiSamples(body) : ParseBinarySamples(body);

    return new ParsedPvf(ascii, channels, sampleRate, bits, samples);
  }

  private static int[] ParseBinarySamples(ReadOnlySpan<byte> body) {
    var count = body.Length / 4;
    var samples = new int[count];
    for (var i = 0; i < count; ++i)
      samples[i] = BinaryPrimitives.ReadInt32BigEndian(body[(i * 4)..]);
    return samples;
  }

  private static int[] ParseAsciiSamples(ReadOnlySpan<byte> body) {
    var text = Encoding.ASCII.GetString(body);
    var tokens = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    var samples = new int[tokens.Length];
    for (var i = 0; i < tokens.Length; ++i)
      samples[i] = int.Parse(tokens[i]);
    return samples;
  }

  private static int IndexOf(ReadOnlySpan<byte> data, byte value, int start) {
    for (var i = start; i < data.Length; ++i)
      if (data[i] == value) return i;
    return -1;
  }
}
