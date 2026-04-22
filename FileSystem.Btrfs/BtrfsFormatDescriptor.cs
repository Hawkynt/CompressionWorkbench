#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Btrfs;

public sealed class BtrfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints {
  // WORM-minimal writer constraints: a single leaf node holds ≤64 file
  // tuples (INODE_ITEM + DIR_INDEX + inline EXTENT_DATA). No chunk tree is
  // emitted — the reader's identity LogicalToPhysical fallback maps blocks.
  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "Btrfs WORM image: up to 64 flat files with inline extents, single fs-tree leaf node.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) { reason = "Btrfs writer only supports flat file lists (no directories)."; return false; }
    reason = null;
    return true;
  }

  public string Id => "Btrfs";
  public string DisplayName => "Btrfs Filesystem Image";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".btrfs";
  public IReadOnlyList<string> Extensions => [".btrfs", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("_BHRfS_M"u8.ToArray(), Offset: 0x10040, Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Btrfs copy-on-write filesystem image. The writer emits a populated
  /// <c>sys_chunk_array</c> inside the superblock and a real chunk tree
  /// with three chunks (<c>SYSTEM</c>, <c>METADATA</c>, <c>DATA</c>) that
  /// map every logical range used by the image to its physical offset,
  /// a dev tree with a <c>DEV_ITEM</c> for the single device, a root
  /// tree, and an FS tree leaf with inode + dir-index + inline
  /// <c>EXTENT_DATA</c> items per file. All metadata blocks carry
  /// CRC-32C (Castagnoli) at the start.
  /// </summary>
  public string Description => "Btrfs copy-on-write filesystem image with real chunk tree + CRC-32C metadata checksums";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new BtrfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored",
      e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new BtrfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new BtrfsWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
    w.WriteTo(output);
  }
}
