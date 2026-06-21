#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Xenix;

/// <summary>
/// Descriptor for Microsoft/SCO Xenix System V filesystem images.
/// Carries the genuine Xenix superblock magic 0x2B5544 at s_magic (struct
/// offset 0x3F8 → file offset 2040), the value the Linux sysv driver matches.
/// Reads existing Xenix images and emits fresh WORM images via
/// <see cref="XenixWriter"/>.
/// </summary>
public sealed class XenixFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable {
  public string Id => "Xenix";
  public string DisplayName => "Xenix FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".xnx";
  public IReadOnlyList<string> Extensions => [".xnx", ".xenix"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Genuine Xenix s_magic 0x2B5544 (LE) at file offset 2040 (block 1 + 0x3F8).
    new([0x44, 0x55, 0x2B, 0x00], Offset: 2040, Confidence: 0.70),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Microsoft/SCO Xenix filesystem image — read + WORM emit + in-place Add/Remove via s_free/s_inode cache (Xenix V variant).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new XenixReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new XenixReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new XenixReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// WORM-emits a fresh Xenix V image to <paramref name="output"/> containing
  /// the supplied <paramref name="inputs"/>. Directory components in input
  /// archive names become real intermediate directory inodes. Names are
  /// truncated to 14 ASCII bytes per the on-disk dir-entry budget; each file is
  /// stored through the inode's 10 direct zone slots (max 10 KB with the 1 KB
  /// block size we emit). Failing those constraints throws
  /// <see cref="InvalidOperationException"/> with the offending path.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    using var w = new XenixWriter(output, leaveOpen: true);
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    w.Finish();
  }

  /// <summary>
  /// Adds (or replaces by leaf name) files inside an existing Xenix V image
  /// via <see cref="XenixModifier"/> — O(touched bytes) random-access I/O using
  /// the s5fs s_free / s_inode caches (refilled from a full scan on first
  /// mutation, since the WORM writer leaves the caches zeroed). Files are
  /// flattened to their leaf names (single-level root scope); name length is
  /// truncated to 14 ASCII bytes per the on-disk dirent budget.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FilesOnly(inputs)) {
      // Idempotent replace: drop any existing copy first so the inode + zones
      // get freed back into the caches before we re-allocate them.
      XenixModifier.RemoveFile(archive, LeafName(name), wipeData: true);
      XenixModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing Xenix V image. Names are
  /// matched against their on-disk (leaf, 14-char-truncated) form so callers
  /// can pass either the leaf or the original nested path supplied to
  /// <see cref="Add"/>.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      XenixModifier.RemoveFile(archive, LeafName(name), wipeData: true);
  }

  private static string LeafName(string name) {
    var leaf = name;
    var slash = Math.Max(leaf.LastIndexOf('/'), leaf.LastIndexOf('\\'));
    if (slash >= 0) leaf = leaf[(slash + 1)..];
    return leaf;
  }
}
