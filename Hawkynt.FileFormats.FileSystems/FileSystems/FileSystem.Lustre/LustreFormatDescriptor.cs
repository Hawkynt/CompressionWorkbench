#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Lustre;

/// <summary>
/// Descriptor for Lustre MDT/OST images backed by ldiskfs (ext4-compatible).
/// The archive projection surfaces one target's backing-store namespace — NOT the
/// distributed Lustre logical view, which requires correlating MDT metadata with
/// objects striped across multiple OSTs.
///
/// <para>
/// Maintenance is intentionally narrower than generic ext editing. Free-space wipe
/// follows the ldiskfs block allocation bitmap, and shrink updates ext geometry in
/// place, preserving every surviving allocated block. Add/replace/remove, defrag and
/// structural relayout are not advertised because rebuilding through the generic ext
/// writer would discard Lustre LMA/LOV/FID extended attributes and target metadata.
/// Legacy "LUSTRE" / "LUst" object-header dumps remain inspection-only.
/// </para>
///
/// Detection is extension-routed (.lustre / .ost / .mdt) and the legacy
/// "LUSTRE" / "LUst" object-header magic at offset 0; ext4 superblock magic is
/// deliberately NOT registered here because that would steal generic ext4 images.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://wiki.lustre.org/Configuring_the_Lustre_File_System</c> — mkfs.lustre examples showing ldiskfs backing targets</description></item>
///   <item><description><c>https://wiki.lustre.org/Understanding_Lustre_Internals</c> — backing ldiskfs inspection and Lustre target internals</description></item>
///   <item><description><c>https://docs.kernel.org/filesystems/ext4/bitmaps.html</c> — ext4 allocation bitmap semantics and BLOCK_UNINIT warning</description></item>
///   <item><description><c>https://docs.kernel.org/filesystems/ext4/group_descr.html</c> — group descriptor and bitmap locations</description></item>
/// </list>
/// </summary>
public sealed class LustreFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IWipeEmpty,
  IArchiveShrinkable,
  ILayoutOptimizable {

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
    "Lustre target inspection via ldiskfs (ext4) reader delegation, with conservative " +
    "offline maintenance on genuine ldiskfs images: bitmap-proven free-block wipe and " +
    "in-place trailing-free shrink/compact. Lustre xattrs (LMA, LOV EA striping, FID) " +
    "remain opaque and are preserved because maintenance never reconstructs live inodes. " +
    "Add/remove/purge, defrag and block-size relayout are intentionally not claimed: a " +
    "generic ext rebuild would lose Lustre target semantics. The distributed logical view " +
    "still requires live cluster metadata and is out of scope. Legacy 'LUSTRE'/'LUst' dumps " +
    "remain raw inspection-only objects.";

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

  /// <summary>
  /// Zeroes complete ldiskfs blocks whose initialized allocation bitmap proves them free.
  /// Cluster-tip and deleted-dirent requests are deliberately ignored: those byte ranges
  /// cannot be proven dead without interpreting Lustre-specific inode/xattr semantics.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true)
    => LustreLdiskfsMaintenance.WipeFreeBlocks(image);

  /// <summary>
  /// Shrinks an offline ldiskfs target by trimming trailing free blocks while preserving
  /// all surviving allocated blocks and Lustre-specific inode/xattr bytes.
  /// </summary>
  public void Shrink(Stream input, Stream output)
    => LustreLdiskfsMaintenance.Shrink(input, output);

  /// <summary>
  /// Reports the current ldiskfs block geometry. Structural relayout is deliberately not
  /// offered until a Lustre-aware writer can preserve all target metadata.
  /// </summary>
  public LayoutAnalysis AnalyzeLayout(Stream image)
    => LustreLdiskfsMaintenance.AnalyzeLayout(image);
}
