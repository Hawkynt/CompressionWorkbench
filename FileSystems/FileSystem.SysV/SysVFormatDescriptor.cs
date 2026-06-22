#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.SysV;

/// <summary>
/// R/W descriptor for AT&amp;T UNIX System V (s5fs) filesystem images.
/// Magic <c>0xFD187E20</c> at file offset 1024+504 = 0x5F8.
/// </summary>
/// <remarks>
/// <para>
/// Reads any s5fs image with the documented superblock layout (1024-byte
/// blocks, 64-byte inodes, 24-bit zone pointers, 16-byte directory entries).
/// Writes a fresh image targeting the same classic AT&amp;T variant only —
/// other in-the-wild SysV-family flavours (Coherent, Xenix, SCO, AFS) use
/// distinct magics and inode shapes and are out of scope for the writer.
/// </para>
/// <para>
/// Mutation surface (<see cref="IArchiveModifiable"/>): true in-place R/W
/// via <see cref="SysVInPlaceModifier"/> — every Add/Remove/Replace mutates
/// the existing image at fixed byte offsets without rebuilding, including
/// the classic V7/SYSV chained free-block group cache (refill from chain
/// when <c>s_nfree</c> drops to 1; spill to a new chain block when it
/// would exceed 50) and the in-line <c>s_inode[100]</c> cache with re-scan
/// refill. Nested-path adds/removes fall back to the rebuild-from-scratch
/// path so the in-place engine never has to re-walk the directory tree.
/// Per-file size is bounded at 10 direct zones (10 KB); indirect blocks
/// are out of scope (same as the WORM writer).
/// </para>
/// <para>
/// Acceptance gates: round-trip via our own reader (necessary), spec
/// field-offset audit against linux/fs/sysv/super.c and the AT&amp;T System V
/// Interface Definition (sufficient — the writer comments cite the exact
/// offsets), and an opt-in WSL <c>mount -t sysv -o loop,ro</c> gate that
/// skips cleanly when the kernel's sysv driver isn't loadable (the default
/// WSL2 kernel ships without it).
/// </para>
/// </remarks>
public sealed class SysVFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, IFormatOptionsSchema, ILayoutOptimizable {
  /// <summary>
  /// s5fs geometry (1024-byte blocks, 64-byte inodes, single-group layout) is
  /// fixed at the classic AT&amp;T variant the writer emits, so the only honoured
  /// knob is the 6-byte volume name in the superblock <c>s_fname[6]</c> field.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "VolumeLabel", DisplayName: "Volume Label", Kind: FormatOptionKind.String, Default: "",
      Description: "s5fs volume name stored in s_fname (max 6 ASCII chars)."),
  ];

  public string Id => "SysV";
  public string DisplayName => "UNIX System V FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".s5";
  public IReadOnlyList<string> Extensions => [".s5", ".sysv"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 0xFD187E20 little-endian at file offset 512+504 = 1016 (0x3F8) — the
    // superblock sits at block 0 + BLOCK_SIZE/2, where the Linux sysv driver reads it.
    new([0x20, 0x7E, 0x18, 0xFD], Offset: 1016, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "AT&T UNIX System V s5fs filesystem image — true in-place R/W " +
    "(spec-audited writer + SysVInPlaceModifier mutating inode table and " +
    "data blocks at fixed byte offsets via the chained free-block group " +
    "cache + s_inode[100] cache with re-scan refill; Linux sysv kernel " +
    "driver mountable when host ships sysv.ko).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SysVReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SysVReader(stream);
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
    var r = new SysVReader(archive);
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
  /// Emits a fresh s5fs image (1024-byte blocks, classic AT&amp;T System V
  /// variant). Subdirectories are encoded from path separators in the input
  /// entry names; per-file size cap is 10 KB (10 direct zones).
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    using var w = new SysVWriter(output, leaveOpen: true);
    if (options.HasOption("VolumeLabel")) w.SetVolumeLabel(options.GetOption("VolumeLabel", ""));
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    w.Finish();
  }

  /// <summary>
  /// Adds (or replaces) files inside an existing s5fs image. Flat-root files
  /// are mutated truly in place by <see cref="SysVInPlaceModifier"/> (real
  /// free-block chain refill + inode re-scan, no rebuild); anything with a
  /// path separator or a capacity overflow falls back to
  /// <see cref="ModifyRebuilder"/> so the descriptor stays consistent for
  /// out-of-scope inputs.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    try {
      foreach (var (name, data) in FilesOnly(inputs)) {
        // Nested-path entries can't go through the in-place modifier — fall
        // through to the rebuild path for the entire input list so the
        // resulting image stays self-consistent.
        if (name.Contains('/') || name.Contains('\\'))
          throw new NotSupportedException("nested path");
        SysVInPlaceModifier.Add(archive, name, data);
      }
    } catch (NotSupportedException) {
      RebuildAdd(archive, inputs);
    } catch (InvalidOperationException) {
      RebuildAdd(archive, inputs);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing s5fs image via the in-place
  /// modifier (zeroes file data blocks before returning them to the free
  /// list, matching the <see cref="IArchiveModifiable.Remove"/> wipe
  /// contract). Falls back to the rebuild path for any nested-path entry
  /// the in-place engine won't touch.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var rebuildList = new List<string>();
    foreach (var name in entryNames) {
      if (name.Contains('/') || name.Contains('\\')) {
        rebuildList.Add(name);
        continue;
      }
      if (!SysVInPlaceModifier.Remove(archive, name))
        rebuildList.Add(name);   // not found at root — let the rebuild path filter from nested layers
    }
    if (rebuildList.Count > 0)
      RebuildRemove(archive, rebuildList.ToArray());
  }

  private static void RebuildAdd(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    archive.Position = 0;
    ModifyRebuilder.Add(archive, inputs,
      readEntries: stream => {
        var r = new SysVReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: BuildImage);
  }

  private static void RebuildRemove(Stream archive, string[] entryNames) {
    archive.Position = 0;
    ModifyRebuilder.Remove(archive, entryNames,
      readEntries: stream => {
        var r = new SysVReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: BuildImage);
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    using var ms = new MemoryStream();
    using var w = new SysVWriter(ms, leaveOpen: true);
    foreach (var (n, d) in files) w.AddFile(n, d);
    w.Finish();
    return ms.ToArray();
  }
}
