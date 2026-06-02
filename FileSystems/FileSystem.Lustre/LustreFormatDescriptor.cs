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
/// </summary>
public sealed class LustreFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Lustre";
  public string DisplayName => "Lustre";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".lustre";
  public IReadOnlyList<string> Extensions => [".lustre", ".ost", ".mdt"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ASCII "LUSTRE" (6 bytes) at offset 0 — legacy OST object-header dump.
    new("LUSTRE"u8.ToArray(), Offset: 0, Confidence: 0.90),
    // Bytes 0x4C 0x55 0x73 0x74 (= 0x4C557374 BE) at offset 0 — short variant.
    new([0x4C, 0x55, 0x73, 0x74], Offset: 0, Confidence: 0.85),
    // NOTE: ext4 magic (0xEF53 at offset 1080) is intentionally NOT registered
    // here — it would steal detection from generic ext4 images. ldiskfs MDT/OST
    // images surface through Lustre only via the .lustre/.ost/.mdt extension.
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Lustre R/O via ldiskfs (ext4) reader delegation. Surfaces the ldiskfs view of one " +
    "MDT or OST backing store (file walk over the ext4-compatible block layout); Lustre " +
    "xattrs (LMA, LOV EA striping, FID) are preserved in the raw image but not interpreted. " +
    "The Lustre logical view (combining MDT inode metadata with file data striped across " +
    "multiple OSTs) requires live cluster metadata and is out of scope. Legacy 'LUSTRE'/'LUst' " +
    "object-header dumps still surface as raw bytes + metadata.ini.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new LustreReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

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
