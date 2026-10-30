#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.DiskCopy42;

/// <summary>
/// Apple Disk Copy 4.2 floppy image (<c>.dc42</c>, <c>.image</c>): exposes the raw sector
/// data as <c>disk.img</c>, the per-sector tag bytes as <c>tags.bin</c> and the header as a
/// synthetic <c>metadata.ini</c>. Creating and editing recompute both checksums; every other
/// header byte of an existing image is kept.
/// </summary>
/// <remarks>
/// Extension-only detection: the header's only constant is a two-byte 0x0100 at offset 0x52,
/// too weak to claim generic files by content.
/// </remarks>
public sealed class DiskCopy42FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, ISyntheticEntryNames {

  public const string DiskName = "disk.img";
  public const string TagsName = "tags.bin";
  public const string MetadataName = "metadata.ini";

  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MetadataName };

  public string Id => "DiskCopy42";
  public string DisplayName => "Apple Disk Copy 4.2";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dc42";
  public IReadOnlyList<string> Extensions => [".dc42", ".image"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Apple Disk Copy 4.2 floppy image (400K/800K GCR, 720K/1440K MFM) with tag data and checksums";
  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;

  /// <summary>A Disk Copy image is a floppy: there is no empty one for a purge to leave behind.</summary>
  public bool CanPurgeToEmpty => false;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    DiskCopy42Image image;
    try {
      image = DiskCopy42Image.Read(stream);
    } catch (InvalidDataException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [
        new(0, "FULL.dc42", length, length, "stored", false, false, null),
        new(1, MetadataName, 0, 0, "stored", false, false, null),
      ];
    }
    return Entries(image).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
      "stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var image = DiskCopy42Image.Read(stream);
    foreach (var (name, data) in Entries(image)) {
      if (files is { Length: > 0 } && !MatchesFilter(name, files)) continue;
      if (name == DiskName && !image.DataChecksumValid)
        throw new InvalidDataException(
          $"Disk Copy 4.2: data checksum 0x{image.StoredDataChecksum:X8} does not match the sectors (0x{image.ComputedDataChecksum:X8}).");
      if (name == TagsName && !image.TagChecksumValid)
        throw new InvalidDataException(
          $"Disk Copy 4.2: tag checksum 0x{image.StoredTagChecksum:X8} does not match the tags (0x{image.ComputedTagChecksum:X8}).");
      WriteFile(outputDir, name, data);
    }
  }

  /// <summary>
  /// Writes a fresh image from <c>disk.img</c> (or the single other file given), an optional
  /// <c>tags.bin</c>, and an optional <c>metadata.ini</c> carrying name/encoding/format. Without
  /// metadata the encoding and format byte follow from the data size.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var (disk, tags, metadata) = Classify(inputs);
    if (disk is null)
      throw new ArgumentException("Disk Copy 4.2 needs the sector data (disk.img).", nameof(inputs));
    var (name, encoding, format) = metadata is null ? (null, null, null) : DiskCopy42Image.ParseMetadata(metadata);
    DiskCopy42Image.Create(name ?? "Untitled", disk, tags ?? [], encoding, format).Write(output);
  }

  /// <summary>Replaces <c>disk.img</c>, <c>tags.bin</c> and/or the editable header fields from <c>metadata.ini</c>.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var image = DiskCopy42Image.Read(archive);
    var (disk, tags, metadata) = Classify(inputs);
    if (metadata is not null) {
      var (name, encoding, format) = DiskCopy42Image.ParseMetadata(metadata);
      image = image.WithHeaderFields(name, encoding, format);
    }
    image = image.WithPayload(disk ?? image.Data, tags ?? image.Tags);
    Commit(archive, image);
  }

  /// <summary>Only <c>tags.bin</c> can be removed; the sector data is the image itself.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var image = DiskCopy42Image.Read(archive);
    var dropTags = false;
    foreach (var raw in entryNames) {
      var leaf = Path.GetFileName(raw.Replace('\\', '/'));
      if (leaf.Equals(TagsName, StringComparison.OrdinalIgnoreCase)) dropTags = true;
      else throw new NotSupportedException($"Disk Copy 4.2 cannot remove '{raw}': only {TagsName} is optional.");
    }
    if (dropTags)
      Commit(archive, image.WithPayload(image.Data, []));
  }

  private static void Commit(Stream archive, DiskCopy42Image image)
    => WholeImageRebuildCommitter.Replace(archive, image.Write, candidate => DiskCopy42Image.Read(candidate));

  private static IEnumerable<(string Name, byte[] Data)> Entries(DiskCopy42Image image) {
    yield return (MetadataName, image.RenderMetadata());
    yield return (DiskName, image.Data);
    if (image.Tags.Length > 0)
      yield return (TagsName, image.Tags);
  }

  private static (byte[]? Disk, byte[]? Tags, byte[]? Metadata) Classify(IReadOnlyList<ArchiveInputInfo> inputs) {
    byte[]? disk = null, tags = null, metadata = null;
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
      var leaf = Path.GetFileName(input.ArchiveName.Replace('\\', '/'));
      if (leaf.Equals(MetadataName, StringComparison.OrdinalIgnoreCase)) metadata = input.ReadContent();
      else if (leaf.Equals(TagsName, StringComparison.OrdinalIgnoreCase)) tags = input.ReadContent();
      else if (disk is null) disk = input.ReadContent();
      else throw new ArgumentException("Disk Copy 4.2 holds exactly one disk image.", nameof(inputs));
    }
    return (disk, tags, metadata);
  }
}
