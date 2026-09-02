#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Akb;

/// <summary>
/// Square Enix AKB audio bank descriptor — surfaces per-entry raw audio payloads plus a synthetic
/// <c>metadata.ini</c> entry containing bank-wide header fields (sample rate, channel mode, loop points).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/vgmstream/vgmstream</c> — vgmstream — implements AKB parsing; the de-facto reference</description></item>
///   <item><description>Square Enix never published the AKB layout; header fields were recovered by the VGM ripping community</description></item>
/// </list>
/// </summary>
public sealed class AkbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new AkbReader(archive);
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Akb";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Square Enix AKB";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".akb";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".akb"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("AKB1"u8.ToArray(), Confidence: 0.95),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("akb-v2", "AKB v2")];
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
  public string Description => "Square Enix audio bank (Final Fantasy / Kingdom Hearts era)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var origin = stream.Position;
    try {
      stream.Position = 0;
      using var reader = new AkbReader(stream, leaveOpen: true);
      var result = new List<ArchiveEntryInfo>(reader.Entries.Count + 1);
      for (var i = 0; i < reader.Entries.Count; ++i) {
        var e = reader.Entries[i];
        result.Add(new ArchiveEntryInfo(i, e.Name, e.Size, e.Size, "Stored", false, false, null));
      }
      var meta = BuildMetadata(reader);
      result.Add(new ArchiveEntryInfo(reader.Entries.Count, AkbConstants.MetadataEntryName, meta.Length, meta.Length, "Stored", false, false, null));
      return result;
    } finally {
      stream.Position = origin;
    }
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);
    stream.Position = 0;
    using var reader = new AkbReader(stream, leaveOpen: true);
    foreach (var e in reader.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, reader.Extract(e));
    }
    if (files == null || MatchesFilter(AkbConstants.MetadataEntryName, files))
      WriteFile(outputDir, AkbConstants.MetadataEntryName, BuildMetadata(reader));
  }

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. The synthetic
  /// <c>metadata.ini</c> entry is materialised on the fly; all other
  /// entries delegate to the reader's per-entry extract and are wrapped
  /// in a <see cref="Compression.Registry.Streaming.BoundedEntryStream"/>
  /// sized to their logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    using var reader = new AkbReader(archive, leaveOpen: true);
    if (string.Equals(entryName, AkbConstants.MetadataEntryName, StringComparison.OrdinalIgnoreCase)) {
      var meta = BuildMetadata(reader);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(meta, writable: false), meta.Length, leaveOpen: false);
    }
    foreach (var e in reader.Entries) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = reader.Extract(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
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
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new AkbWriter(output, leaveOpen: true);
    foreach (var (name, data) in FlatFiles(inputs)) {
      // Skip a metadata.ini supplied as input — we synthesize that on read, persisting it as a real
      // payload would round-trip as a phantom audio entry on the next List() call.
      if (string.Equals(name, AkbConstants.MetadataEntryName, StringComparison.OrdinalIgnoreCase))
        continue;
      w.AddEntry(name, data);
    }
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new AkbReader(stream, leaveOpen: true);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new AkbWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddEntry(n, d);
        }
        return ms.ToArray();
      });
  }

  private static byte[] BuildMetadata(AkbReader reader) {
    var sb = new StringBuilder();
    sb.AppendLine("[akb]");
    sb.Append("version = ").AppendLine(reader.VersionByte.ToString(CultureInfo.InvariantCulture));
    sb.Append("channel_mode = ").AppendLine(reader.ChannelMode.ToString(CultureInfo.InvariantCulture));
    sb.Append("sample_rate = ").AppendLine(reader.SampleRate.ToString(CultureInfo.InvariantCulture));
    sb.Append("loop_start = ").AppendLine(reader.LoopStart.ToString(CultureInfo.InvariantCulture));
    sb.Append("loop_end = ").AppendLine(reader.LoopEnd.ToString(CultureInfo.InvariantCulture));
    sb.Append("content_offset = ").AppendLine(reader.ContentOffset.ToString(CultureInfo.InvariantCulture));
    sb.Append("content_size = ").AppendLine(reader.ContentSize.ToString(CultureInfo.InvariantCulture));
    sb.Append("entry_count = ").AppendLine(reader.Entries.Count.ToString(CultureInfo.InvariantCulture));
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
