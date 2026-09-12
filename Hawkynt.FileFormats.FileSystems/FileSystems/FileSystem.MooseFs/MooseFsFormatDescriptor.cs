#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.MooseFs;

/// <summary>
/// Descriptor for MooseFS master-metadata images (<c>metadata.mfs</c>).
/// The standalone image contains namespace/chunk metadata, not file payloads;
/// payload bytes live on chunk servers. The descriptor therefore exposes a
/// synthetic forensic view of the metadata envelope rather than pretending a
/// metadata dump is a self-contained MooseFS volume.
///
/// <para>
/// Safe standalone-image maintenance is deliberately narrow: byte layout can
/// be mapped, unused-space wiping fails closed (valid metadata dumps are tightly
/// packed), and purge resets the image to MooseFS's official <c>MFSM NEW</c>
/// empty bootstrap. Add/replace/remove, shrink and defragmentation require
/// semantic NODE/EDGE/CHNK updates and/or live cluster coordination and are not
/// advertised here.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/moosefs/moosefs</c> — canonical implementation and metadata loader/store format</description></item>
///   <item><description><c>https://moosefs.com/</c> — vendor documentation</description></item>
/// </list>
/// </summary>
public sealed class MooseFsFormatDescriptor
    : IFormatDescriptor, IArchiveFormatOperations, IArchiveLayoutMap, IArchivePurgeable {

  /// <summary>Gets the id.</summary>
  public string Id => "MooseFs";

  /// <summary>Gets the display name.</summary>
  public string DisplayName => "MooseFS";

  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;

  /// <summary>Gets the default extension.</summary>
  public string DefaultExtension => ".mfsm";

  // ".mfs" intentionally is not claimed: it collides with Macintosh File System (FileSystem.Mfs).
  // MooseFS detection is by the MFSM signature at offset zero.
  /// <summary>Gets the extensions.</summary>
  public IReadOnlyList<string> Extensions => [".mfsm"];

  /// <summary>Gets the compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>Gets the magic signatures.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MFSM"u8.ToArray(), Offset: 0, Confidence: 0.90),
  ];

  /// <summary>Gets the methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];

  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;

  /// <summary>Gets the family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>Gets the description.</summary>
  public string Description =>
    "MooseFS master metadata image. Reads the versioned metadata envelope and section table; " +
    "file payloads remain on chunk servers. Standalone maintenance provides byte-layout mapping, " +
    "safe unused-space wiping and destructive reset to the official empty MFSM NEW bootstrap; " +
    "full namespace R/W, shrink and defragmentation are intentionally not claimed.";

  /// <summary>Lists the synthetic entries in the supplied metadata image.</summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new MooseFsReader(stream);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index, entry.Name, entry.Size, entry.Size, "Stored", entry.IsDirectory, false, null)).ToList();
  }

  /// <summary>Extracts the synthetic metadata-image views.</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new MooseFsReader(stream);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory)
        continue;
      if (files != null && !MatchesFilter(entry.Name, files))
        continue;
      WriteFile(outputDir, entry.Name, reader.Extract(entry));
    }
  }

  /// <summary>
  /// Enumerates the exact bytes occupied by a valid MooseFS metadata image.
  /// Unknown/truncated images return no extents so generic maintenance fails closed.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanSeek)
      return [];

    var originalPosition = archive.Position;
    try {
      archive.Position = 0;
      using var reader = new MooseFsReader(archive);
      if (reader.ParseStatus != "ok")
        return [];

      if (reader.IsEmptyBootstrap)
        return [new DefragBlockInfo(0, reader.ImageSize, DefragBlockKind.MetadataReserved, "MFSM NEW bootstrap")];

      if (reader.FileFormatVersion is byte version && version < 0x16)
        return [new DefragBlockInfo(0, reader.ImageSize, DefragBlockKind.MetadataReserved, "legacy MooseFS metadata")];

      var result = new List<DefragBlockInfo> {
        new(0, 24, DefragBlockKind.MetadataReserved, "MooseFS metadata header"),
      };

      foreach (var section in reader.Sections) {
        result.Add(new DefragBlockInfo(
          section.Offset - 16, 16, DefragBlockKind.MetadataReserved, $"{section.Tag} section header"));
        if (section.Length > 0)
          result.Add(new DefragBlockInfo(section.Offset, section.Length, DefragBlockKind.Used, section.Tag));
      }

      result.Add(new DefragBlockInfo(
        reader.ImageSize - MooseFsReader.EofMarker.Length,
        MooseFsReader.EofMarker.Length,
        DefragBlockKind.MetadataReserved,
        "MooseFS EOF marker"));
      return result;
    } catch (InvalidDataException) {
      return [];
    } finally {
      archive.Position = originalPosition;
    }
  }

  /// <summary>
  /// Resets a valid metadata image to MooseFS's official empty bootstrap state.
  /// This only rewrites <c>metadata.mfs</c>; any chunk-server data is outside the
  /// image and therefore outside this operation.
  /// </summary>
  public void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException(
        "MooseFS purge requires a readable, writable, seekable stream.", nameof(archive));

    archive.Position = 0;
    using (var reader = new MooseFsReader(archive)) {
      if (reader.ParseStatus != "ok")
        throw new InvalidDataException(
          $"MooseFS purge requires a structurally valid metadata image; parse status is '{reader.ParseStatus}'.");
    }

    archive.Position = 0;
    archive.Write("MFSM NEW"u8);
    archive.SetLength(8);
    archive.Flush();
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    using var reader = new MooseFsReader(archive);
    var entry = reader.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"MooseFS entry not found: {entryName}");
    var data = reader.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}
