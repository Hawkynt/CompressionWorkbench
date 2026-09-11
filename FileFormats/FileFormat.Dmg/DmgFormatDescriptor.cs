#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dmg;

/// <summary>
/// Apple disk image (DMG/UDIF) — "koly" trailer + XML plist block map (blkx) with
/// per-chunk raw/zlib/bzip2/ADC/LZFSE/LZMA storage.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://newosxbook.com/DMG.html</c> — Jonathan Levin's UDIF format write-up — the standard unofficial reference (Apple never published a specification)</description></item>
///   <item><description><c>https://github.com/libyal/libmodi/blob/main/documentation/Mac%20OS%20disk%20image%20types.asciidoc</c> — independently documented koly/blkx structures</description></item>
///   <item><description><c>https://github.com/SecurityRonin/dmg-forensic</c> — Apache-2.0 independent reader used as a behavioural cross-reference; no implementation code is copied here</description></item>
/// </list>
/// </summary>
public sealed class DmgFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
  IArchiveModifiable, IArchiveDefragmentable, IArchiveShrinkable, IArchiveLayoutMap {

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
  /// Adds or replaces partitions. The raw UDIF profile emitted here uses a
  /// metadata-tail edit; other readable UDIF profiles are decoded, rebuilt and
  /// verified before the original stream is replaced.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => DmgInPlaceModifier.Add(archive, inputs);

  /// <summary>
  /// Removes partitions. Raw-profile payload bytes become reclaimable slack;
  /// foreign readable profiles are rebuilt into the canonical raw profile.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames)
    => DmgInPlaceModifier.Remove(archive, entryNames);

  /// <summary>
  /// Enumerates the byte-level container layout. Foreign/unknown gaps are
  /// conservatively reserved; only provably unreachable bytes in this writer's
  /// raw profile are exposed as free and therefore wipeable.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive)
    => DmgLayoutMap.Enumerate(archive);
}
