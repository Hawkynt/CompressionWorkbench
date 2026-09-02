#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Lustre;

/// <summary>
/// R/O descriptor for Lustre MDT/OST images via ldiskfs (ext4-compatible)
/// reader delegation. Surfaces the ldiskfs view of a single MDT or OST
/// backing store — NOT the Lustre logical view (which would require combining
/// MDT inode metadata with file data striped across multiple OSTs, out of
/// scope without live cluster metadata).
///
/// Detection is extension-routed (.lustre / .ost / .mdt) and the legacy
/// "LUSTRE" / "LUst" object-header magic at offset 0; ext4 superblock magic is
/// deliberately NOT registered here (it would steal detection from generic ext4
/// images). When opened with an ldiskfs MDT/OST image (recognised by the .ost /
/// .mdt / .lustre extension), <see cref="LustreReader"/> delegates the file walk
/// to <c>FileSystem.Ext.ExtReader</c>.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.lustre.org/</c> — project home</description></item>
///   <item><description><c>https://wiki.lustre.org/</c> — Lustre wiki (architecture, ldiskfs/MDT/OST layout)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Lustre_(file_system)</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class LustreFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Lustre";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Lustre";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".lustre";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".lustre", ".ost", ".mdt"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ASCII "LUSTRE" (6 bytes) at offset 0 — legacy OST object-header dump.
    new("LUSTRE"u8.ToArray(), Offset: 0, Confidence: 0.90),
    // Bytes 0x4C 0x55 0x73 0x74 (= 0x4C557374 BE) at offset 0 — short variant.
    new([0x4C, 0x55, 0x73, 0x74], Offset: 0, Confidence: 0.85),
    // NOTE: ext4 magic (0xEF53 at offset 1080) is intentionally NOT registered
    // here — it would steal detection from generic ext4 images. ldiskfs MDT/OST
    // images surface through Lustre only via the .lustre/.ost/.mdt extension.
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
public string Description =>
    "Lustre R/O via ldiskfs (ext4) reader delegation. Surfaces the ldiskfs view of one " +
    "MDT or OST backing store (file walk over the ext4-compatible block layout); Lustre " +
    "xattrs (LMA, LOV EA striping, FID) are preserved in the raw image but not interpreted. " +
    "The Lustre logical view (combining MDT inode metadata with file data striped across " +
    "multiple OSTs) requires live cluster metadata and is out of scope. Legacy 'LUSTRE'/'LUst' " +
    "object-header dumps still surface as raw bytes + metadata.ini.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new LustreReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new LustreReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new LustreReader(archive);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"Lustre entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}
