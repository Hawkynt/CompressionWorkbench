#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dmg;

/// <summary>
/// Apple disk image (DMG/UDIF) — "koly" trailer + XML plist block map (blkx) with zlib/bzip2/ADC-compressed chunks.
///
/// References:
/// <list type="bullet">
///   <item><description><c>http://newosxbook.com/DMG.html</c> — Jonathan Levin's UDIF format write-up — the standard unofficial reference (Apple never published a spec)</description></item>
///   <item><description><c>https://github.com/darlinghq/darling-dmg</c> — darling-dmg — open-source DMG/UDIF implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Apple_Disk_Image</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class DmgFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
  IArchiveModifiable, IArchiveDefragmentable, IArchiveShrinkable {


  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Dmg";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "DMG";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".dmg";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".dmg"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("dmg", "DMG")];
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
  public string Description => "Apple disk image";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new DmgReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, stream.Length,
      "DMG", false, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new DmgReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single DMG partition as a bounded read-only <see cref="Stream"/>.
  /// The reader's per-entry extractor reconstructs the partition's raw
  /// sectors; they are wrapped in a <see cref="BoundedEntryStream"/> sized
  /// to the entry's size.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new DmgReader(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false),
        bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream(System.Array.Empty<byte>(), writable: false),
      0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new DmgWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddPartition(i.ArchiveName, i.ReadContent());
    }
    w.WriteTo(output);
  }

  /// <summary>
  /// Adds or replaces partitions in the raw UDIF profile emitted by this writer.
  /// Existing partition payload offsets are preserved; new data occupies the old
  /// plist tail and only the blkx/plist + koly index are rewritten.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => DmgInPlaceModifier.Add(archive, inputs);

  /// <summary>
  /// Removes partitions from the raw UDIF profile by dropping their blkx records.
  /// Payload bytes are left as unreachable data-fork slack so unrelated partitions
  /// never need to move.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames)
    => DmgInPlaceModifier.Remove(archive, entryNames);
}
