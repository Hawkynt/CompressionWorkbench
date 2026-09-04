#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Flv;

/// <summary>
/// Pseudo-archive descriptor for Flash Video (<c>.flv</c>). The container is
/// demuxed into one entry per codec stream — AVC video as an Annex-B H.264
/// elementary stream, AAC audio as ADTS, MP3 as raw frames, every other codec
/// as its concatenated frame payloads — plus the raw AMF0 script tags and a
/// <c>metadata.ini</c> carrying the header flags and the decoded
/// <c>onMetaData</c> values.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://rtmp.veriskope.com/pdf/video_file_format_spec_v10_1.pdf</c> — Adobe Flash Video File Format Specification v10.1, Annex E (FLV) and the AUDIODATA/VIDEODATA tag layouts</description></item>
///   <item><description><c>https://rtmp.veriskope.com/pdf/amf0-file-format-specification.pdf</c> — Adobe AMF0 file format specification (script data tags)</description></item>
///   <item><description>ISO/IEC 14496-15 §5.2.4 — <c>AVCDecoderConfigurationRecord</c>; ISO/IEC 14496-3 §1.6 — <c>AudioSpecificConfig</c> and ADTS</description></item>
/// </list>
/// </summary>
public sealed class FlvFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Flv";
  public string DisplayName => "Flash Video (FLV)";
  public FormatCategory Category => FormatCategory.Video;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".flv";
  public IReadOnlyList<string> Extensions => [".flv"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'F', (byte)'L', (byte)'V', 0x01], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Flash Video container demuxed into per-codec elementary streams and script data.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false,
      LastModified: null, Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input))
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var flv = FlvReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));

    var result = new List<(string, string, byte[])> {
      ("metadata.ini", "Tag", BuildMetadata(flv)),
    };
    for (var i = 0; i < flv.Scripts.Count; ++i)
      result.Add(($"script_{i:D3}_{SafeName(flv.Scripts[i].Name)}.amf", "Tag", flv.Scripts[i].Body));
    foreach (var es in flv.Streams)
      result.Add((es.EntryName, "Payload", es.Payload));
    return result;
  }

  private static string SafeName(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var ch in name)
      sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
    return sb.Length == 0 ? "script" : sb.ToString();
  }

  private static byte[] BuildMetadata(FlvReader.FlvFile flv) {
    var sb = new StringBuilder();
    sb.Append("[flv]\n");
    sb.Append(CultureInfo.InvariantCulture, $"version = {flv.Version}\n");
    sb.Append(CultureInfo.InvariantCulture, $"has_audio = {(flv.HasAudioFlag ? "yes" : "no")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"has_video = {(flv.HasVideoFlag ? "yes" : "no")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"tag_count = {flv.TagCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"stream_count = {flv.Streams.Count}\n");
    sb.Append(CultureInfo.InvariantCulture, $"last_timestamp_ms = {flv.LastTimestampMs}\n");
    foreach (var s in flv.Streams) {
      sb.Append('\n');
      sb.Append(CultureInfo.InvariantCulture, $"[{s.EntryName}]\n");
      sb.Append(CultureInfo.InvariantCulture, $"kind = {s.Kind}\n");
      sb.Append(CultureInfo.InvariantCulture, $"codec = {s.Codec}\n");
      sb.Append(CultureInfo.InvariantCulture, $"tags = {s.TagCount}\n");
      sb.Append(CultureInfo.InvariantCulture, $"bytes = {s.Payload.Length}\n");
      sb.Append(CultureInfo.InvariantCulture, $"first_timestamp_ms = {s.FirstTimestampMs}\n");
      sb.Append(CultureInfo.InvariantCulture, $"last_timestamp_ms = {s.LastTimestampMs}\n");
    }
    if (flv.Metadata.Count > 0) {
      sb.Append("\n[onMetaData]\n");
      foreach (var kv in flv.Metadata.OrderBy(k => k.Key, StringComparer.Ordinal))
        sb.Append(CultureInfo.InvariantCulture, $"{kv.Key} = {kv.Value}\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
