#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.JuiceFs;

/// <summary>
/// Descriptor for portable JuiceFS metadata backups produced by
/// <c>juicefs dump</c> (JSON) and <c>juicefs dump --binary</c> (v1.3+).
/// </summary>
/// <remarks>
/// JuiceFS itself is distributed: metadata lives in a metadata engine and file
/// payloads live in object storage. A dump therefore cannot provide offline file
/// bytes. This descriptor exposes the backup's metadata faithfully, including a
/// namespace manifest for JSON dumps and individual protobuf segments for binary
/// dumps. JSON backups can be shrunk losslessly by removing insignificant
/// whitespace; block-layout verbs do not apply to these metadata artefacts.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://juicefs.com/docs/community/metadata_dump_load/</c> — official dump/load documentation</description></item>
///   <item><description><c>https://github.com/juicedata/juicefs/blob/main/pkg/meta/dump.go</c> — canonical JSON backup schema</description></item>
///   <item><description><c>https://github.com/juicedata/juicefs/blob/main/pkg/meta/backup.go</c> — canonical binary backup framing</description></item>
/// </list>
/// </remarks>
public sealed class JuiceFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveShrinkable {
  /// <summary>Gets the format id.</summary>
  public string Id => "JuiceFs";
  /// <summary>Gets the display name.</summary>
  public string DisplayName => "JuiceFS";
  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>Gets the supported archive capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  /// <summary>Gets the default extension.</summary>
  public string DefaultExtension => ".juicefs";
  /// <summary>Gets recognised extensions.</summary>
  public IReadOnlyList<string> Extensions => [".juicefs"];
  /// <summary>Gets compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets conservative JSON signatures. Binary backups deliberately have no
  /// offset-zero signature: their identifying magic is in the footer.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("{\n  \"Setting\":"u8.ToArray(), Offset: 0, Confidence: 0.72),
    new("{\"Setting\":"u8.ToArray(), Offset: 0, Confidence: 0.68),
  ];
  /// <summary>Gets storage methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("metadata", "JuiceFS metadata backup")];
  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;
  /// <summary>Gets the algorithm family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>Gets a description of the supported JuiceFS artefacts.</summary>
  public string Description =>
    "JuiceFS portable metadata backups: real juicefs dump JSON and v1.3+ segmented protobuf backups. " +
    "The backup contains namespace/chunk metadata but no file payload bytes; those remain in the configured " +
    "object store. JSON dumps support lossless representation-only shrink by stripping insignificant whitespace. " +
    "Defrag, wipe, layout and offline purge are intentionally not claimed because those operations belong to the " +
    "live metadata-engine/object-store pair, not to a metadata backup file.";

  /// <summary>Lists inspectable metadata artefacts from the supplied backup.</summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new JuiceFsReader(stream);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index, entry.Name, entry.Size, entry.Size, "metadata", entry.IsDirectory, false, null)).ToList();
  }

  /// <summary>Extracts inspectable metadata artefacts from the supplied backup.</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new JuiceFsReader(stream);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory || files != null && !MatchesFilter(entry.Name, files))
        continue;
      WriteFile(outputDir, entry.Name, reader.Extract(entry));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
    using var reader = new JuiceFsReader(archive);
    var entry = reader.Entries.FirstOrDefault(candidate => candidate.Name == entryName)
      ?? throw new FileNotFoundException($"JuiceFS metadata-backup entry not found: {entryName}");
    var data = reader.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.LongLength, leaveOpen: false);
  }

  void IArchiveShrinkable.Shrink(Stream input, Stream output) => JuiceFsShrinker.Shrink(input, output);
}
