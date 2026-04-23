#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Sup;

/// <summary>
/// Pseudo-archive descriptor for Blu-ray PGS (<c>.sup</c>) subtitle bitmap streams.
/// Each subtitle epoch (PCS through END inclusive) is exposed as one entry, plus a
/// <c>metadata.ini</c> describing the overall stream.
/// </summary>
public sealed class SupFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Sup";
  public string DisplayName => "Blu-ray PGS Subtitles";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".sup";
  public IReadOnlyList<string> Extensions => [".sup"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x50, 0x47], Confidence: 0.85), // "PG" at offset 0
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Blu-ray Presentation Graphic Stream subtitle bitmap segments grouped by epoch.";

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
    var parsed = SupReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));

    var result = new List<(string, string, byte[])> {
      ("metadata.ini", "Tag", BuildMetadata(parsed)),
    };
    for (var i = 0; i < parsed.Epochs.Count; i++)
      result.Add(($"subtitle_{i:D3}.bin", "Payload", parsed.Epochs[i].RawBytes));
    return result;
  }

  private static byte[] BuildMetadata(SupReader.Stream parsed) {
    var sb = new StringBuilder();
    sb.AppendLine("[sup]");
    sb.Append("segment_count = ").Append(parsed.Segments.Count).Append('\n');
    sb.Append("subtitle_count = ").Append(parsed.Epochs.Count).Append('\n');
    if (parsed.Epochs.Count > 0) {
      var first = parsed.Epochs[0];
      var last = parsed.Epochs[^1];
      // PTS is in 90 kHz ticks per the PGS spec.
      var startMs = first.StartPtsRaw / 90.0;
      var endMs = last.EndPtsRaw / 90.0;
      sb.Append(CultureInfo.InvariantCulture, $"first_pts_ms = {startMs:F3}\n");
      sb.Append(CultureInfo.InvariantCulture, $"last_pts_ms = {endMs:F3}\n");
      sb.Append(CultureInfo.InvariantCulture, $"duration_ms = {endMs - startMs:F3}\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
