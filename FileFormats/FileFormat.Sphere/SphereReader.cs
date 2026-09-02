#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Sphere;

/// <summary>
/// NIST SPHERE (<c>.sph</c>) header parser. The file opens with the fixed magic
/// line <c>NIST_1A\n</c> followed by a line giving the total header size in ASCII
/// decimal (canonically <c>"   1024\n"</c>). The remaining header is a sequence of
/// <c>name -type value</c> object lines terminated by a line reading
/// <c>end_head</c>; sample data starts at the declared header offset.
/// <para>The header fields relevant to decoding are <c>channel_count</c>,
/// <c>sample_rate</c>, <c>sample_n_bytes</c>, <c>sample_byte_format</c>
/// (<c>01</c> = little-endian, <c>10</c> = big-endian) and <c>sample_coding</c>
/// (<c>pcm</c>, <c>ulaw</c> or a compressed variant such as
/// <c>pcm,embedded-shorten-…</c>).</para>
/// </summary>
public sealed class SphereReader {
    /// <summary>
  /// Represents a parsed sphere.
  /// </summary>
public sealed record ParsedSphere(
    int ChannelCount,
    int SampleRate,
    int SampleNBytes,
    string SampleByteFormat,
    string SampleCoding,
    byte[] SampleData,
    IReadOnlyList<(string Name, string Value)> Fields);

    /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
public ParsedSphere Read(ReadOnlySpan<byte> data) {
    if (data.Length < 16 || !data[..7].SequenceEqual("NIST_1A"u8))
      throw new InvalidDataException("Missing NIST_1A SPHERE magic.");

    // Magic line "NIST_1A\n" then the header-size line.
    var firstNewline = data.IndexOf((byte)'\n');
    if (firstNewline < 0)
      throw new InvalidDataException("SPHERE header has no newline after magic.");
    var afterMagic = data[(firstNewline + 1)..];
    var secondNewline = afterMagic.IndexOf((byte)'\n');
    if (secondNewline < 0)
      throw new InvalidDataException("SPHERE header missing header-size line.");
    var sizeText = Encoding.ASCII.GetString(afterMagic[..secondNewline]).Trim();
    if (!int.TryParse(sizeText, out var headerSize) || headerSize <= 0 || headerSize > data.Length)
      throw new InvalidDataException($"Invalid SPHERE header size '{sizeText}'.");

    var headerText = Encoding.ASCII.GetString(data[..headerSize]);
    var fields = new List<(string, string)>();
    int channelCount = 1, sampleRate = 0, sampleNBytes = 0;
    string sampleByteFormat = "01", sampleCoding = "pcm";

    foreach (var rawLine in headerText.Split('\n')) {
      var line = rawLine.Trim('\r', ' ', '\t');
      if (line.Length == 0 || line == "NIST_1A" || line == sizeText)
        continue;
      if (line == "end_head")
        break;
      // name -type value  (value may contain spaces; type token starts with '-').
      var firstSpace = line.IndexOf(' ');
      if (firstSpace <= 0)
        continue;
      var name = line[..firstSpace];
      var rest = line[(firstSpace + 1)..].TrimStart();
      if (rest.Length == 0 || rest[0] != '-')
        continue;
      var typeEnd = rest.IndexOf(' ');
      if (typeEnd < 0)
        continue;
      var value = rest[(typeEnd + 1)..].Trim();
      fields.Add((name, value));

      switch (name) {
        case "channel_count": int.TryParse(value, out channelCount); break;
        case "sample_rate": int.TryParse(value, out sampleRate); break;
        case "sample_n_bytes": int.TryParse(value, out sampleNBytes); break;
        case "sample_byte_format": sampleByteFormat = value; break;
        case "sample_coding": sampleCoding = value; break;
      }
    }

    var sampleData = data[headerSize..].ToArray();
    return new ParsedSphere(
      channelCount <= 0 ? 1 : channelCount,
      sampleRate, sampleNBytes, sampleByteFormat, sampleCoding, sampleData, fields);
  }
}
