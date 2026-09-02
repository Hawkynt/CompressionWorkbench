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
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/mjuhasz/BDSup2Sub</c> — BDSup2Sub — canonical open tool for PGS (.sup) subtitle streams</description></item>
///   <item><description>PGS is defined in the Blu-ray Disc Read-Only Format specifications (BDA, not public); segment layout community-documented</description></item>
/// </list>
/// </summary>
public sealed class SupFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Sup";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Blu-ray PGS Subtitles";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".sup";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".sup"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x50, 0x47], Confidence: 0.85), // "PG" at offset 0
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Blu-ray Presentation Graphic Stream subtitle bitmap segments grouped by epoch.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false,
      LastModified: null, Kind: e.Kind)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Opens a single SUP entry as a bounded read-only stream. Each subtitle
  /// epoch's pre-decoded byte buffer is wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to its logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    foreach (var e in BuildEntries(archive)) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(e.Data, writable: false), e.Data.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
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
