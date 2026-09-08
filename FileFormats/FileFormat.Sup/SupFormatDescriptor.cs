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
///   <item><description><c>https://patents.google.com/patent/US20080050091A1/en</c> — public Blu-ray presentation-graphics stream/display-set description</description></item>
///   <item><description><c>https://ffmpeg.org/doxygen/trunk/pgssubdec_8c_source.html</c> — FFmpeg PGS decoder, used as an interoperability oracle</description></item>
///   <item><description><c>https://github.com/mjuhasz/BDSup2Sub</c> — Apache-2.0 BDSup2Sub, established SUP reader/writer interoperability reference</description></item>
///   <item><description>PGS is defined in the Blu-ray Disc Read-Only Format specifications (BDA, not public); the standalone SUP envelope is community-documented</description></item>
/// </list>
/// </summary>
public sealed class SupFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveCreatable, IArchiveModifiable {

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
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
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
  /// SUP has no standalone zero-segment representation accepted by its own reader.
  /// Individual display sets can be removed, but the final one cannot be purged.
  /// </summary>
  public bool CanPurgeToEmpty => false;

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

  /// <summary>
  /// Muxes one or more complete PGS display-set streams into a standalone SUP file.
  /// The derived <c>metadata.ini</c> entry is ignored when a previously demuxed SUP is
  /// fed back through the generic archive rebuild path.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!output.CanWrite) throw new ArgumentException("SUP output stream must be writable.", nameof(output));

    var payloads = inputs
      .Where(input => !input.IsDirectory && !IsDerivedMetadata(input.ArchiveName))
      .ToList();
    if (payloads.Count == 0)
      throw new InvalidDataException("PGS mux requires at least one complete display-set payload.");

    if (payloads.All(input => TryGetGeneratedSubtitleIndex(input.ArchiveName, out _)))
      payloads.Sort(static (left, right) => {
        _ = TryGetGeneratedSubtitleIndex(left.ArchiveName, out var leftIndex);
        _ = TryGetGeneratedSubtitleIndex(right.ArchiveName, out var rightIndex);
        return leftIndex.CompareTo(rightIndex);
      });

    var parsedPayloads = new List<SupReader.Stream>(payloads.Count);
    foreach (var input in payloads)
      parsedPayloads.Add(ParseMuxPayload(input.ArchiveName, input.ReadContent()));

    if (output.CanSeek) {
      output.Position = 0;
      output.SetLength(0);
    }
    foreach (var payload in parsedPayloads)
      SupWriter.Write(output, payload.Segments);
  }

  private static SupReader.Stream ParseMuxPayload(string name, ReadOnlySpan<byte> data) {
    SupReader.Stream parsed;
    try {
      parsed = SupReader.ReadStrict(data);
    } catch (InvalidDataException ex) {
      throw new InvalidDataException($"PGS mux input '{name}' is not a complete SUP segment stream: {ex.Message}", ex);
    }

    var inDisplaySet = false;
    var displaySetCount = 0;
    foreach (var segment in parsed.Segments) {
      switch (segment.Type) {
        case SupReader.SegPresentationComposition:
          if (inDisplaySet)
            throw new InvalidDataException($"PGS mux input '{name}' starts a new PCS before the previous display set ended.");
          inDisplaySet = true;
          break;

        case SupReader.SegEnd:
          if (!inDisplaySet)
            throw new InvalidDataException($"PGS mux input '{name}' contains END outside a display set.");
          if (segment.Body.Length != 0)
            throw new InvalidDataException($"PGS mux input '{name}' contains an END segment with a non-empty body.");
          inDisplaySet = false;
          ++displaySetCount;
          break;

        case SupReader.SegPaletteDefinition or
             SupReader.SegObjectDefinition or
             SupReader.SegWindowDefinition:
          if (!inDisplaySet)
            throw new InvalidDataException($"PGS mux input '{name}' contains segment 0x{segment.Type:X2} before a PCS.");
          break;

        default:
          throw new InvalidDataException($"PGS mux input '{name}' contains unsupported segment type 0x{segment.Type:X2}.");
      }
    }

    if (inDisplaySet)
      throw new InvalidDataException($"PGS mux input '{name}' ends before its display set END segment.");
    if (displaySetCount == 0)
      throw new InvalidDataException($"PGS mux input '{name}' contains no complete PCS-to-END display set.");

    return parsed;
  }

  private static bool IsDerivedMetadata(string archiveName) =>
    Path.GetFileName(archiveName.Replace('\\', '/')).Equals("metadata.ini", StringComparison.OrdinalIgnoreCase);

  private static bool TryGetGeneratedSubtitleIndex(string archiveName, out int index) {
    index = 0;
    var fileName = Path.GetFileName(archiveName.Replace('\\', '/'));
    if (!fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
      return false;
    var stem = Path.GetFileNameWithoutExtension(fileName);
    return stem.StartsWith("subtitle_", StringComparison.OrdinalIgnoreCase)
      && int.TryParse(stem.AsSpan("subtitle_".Length), NumberStyles.None, CultureInfo.InvariantCulture, out index);
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
