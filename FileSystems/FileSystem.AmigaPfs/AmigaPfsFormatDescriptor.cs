#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.AmigaPfs;

/// <summary>
/// R/W descriptor for the Amiga Professional File System (PFS3 / PFS3aio).
/// Signature "PFS\x02"/"PFS\x03"/"PFSa" at offset 0 of the boot block.
///
/// Stage 1 caveat: only direct-block file references are extractable; multi-
/// block files requiring full anode-tree traversal will report a partial
/// extraction. The reader robustly lists all dirblock entries regardless.
/// Stage 1 writer emits boot + root + linear dirblock chain + contiguous
/// per-file data extents (anode-as-direct-block convention) — self-round-trip
/// clean with the matching reader. Stage 1 R/W (this descriptor) adds in-place
/// Add/Remove against the same shape via <see cref="AmigaPfsModifier"/>; image
/// is still <em>not</em> FS-UAE/WinUAE mountable (full PFS3aio anode-table /
/// bitmap / rootinfo emission deferred to a future Stage 2 promotion).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/tonioni/pfs3aio</c> — PFS3 All-In-One source (Toni Wilen), the canonical open-source PFS3 on-disk implementation</description></item>
///   <item><description>Professional File System 3 by Michiel Pelt (original Aminet release + documentation)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Professional_File_System</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class AmigaPfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The writer-honoured knob is the volume label, written as the BCPL disk name
  /// at root-block offset +26. Block size and root-block location are fixed at the
  /// floppy convention and are not exposed.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 31),
  ];

  public string Id => "AmigaPfs";
  public string DisplayName => "Amiga Professional FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".pfs";
  public IReadOnlyList<string> Extensions => [".pfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'P', (byte)'F', (byte)'S', 0x02], Offset: 0, Confidence: 0.95),
    new([(byte)'P', (byte)'F', (byte)'S', 0x03], Offset: 0, Confidence: 0.95),
    new([(byte)'P', (byte)'F', (byte)'S', (byte)'a'], Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga Professional File System (PFS3/PFS3aio) image — Stage 1 R/W " +
    "(boot block + root + linear dirblock chain + contiguous file extents; anode-as-direct-block " +
    "convention; in-place Add/Remove against the same shape; full anode-table/bitmap emission " +
    "deferred — not yet FS-UAE/WinUAE mountable).";

  /// <summary>
  /// Appends or replaces files inside an existing Stage 1 PFS3 image. Each
  /// <paramref name="inputs"/> entry is removed by name first (so callers
  /// get replace-by-name semantics) and then written through
  /// <see cref="AmigaPfsModifier"/> — touching only the affected dirblock,
  /// any newly chained dirblock, and the file's contiguous data extent.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        AmigaPfsModifier.AddDirectory(archive, input.ArchiveName);
        continue;
      }
      AmigaPfsModifier.AddFile(archive, input.ArchiveName, input.ReadContent());
    }
  }

  /// <summary>
  /// Removes the named entries from an existing Stage 1 PFS3 image. The
  /// dirblock entry bytes and the file's data extent are zeroed; the freed
  /// blocks are not currently re-used by subsequent <see cref="Add"/> calls
  /// (Stage 1 has no free-list bookkeeping — extents grow past the
  /// high-water mark).
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      AmigaPfsModifier.RemoveFile(archive, name, wipeData: true);
  }

  /// <summary>
  /// Creates a fresh PFS3 image from <paramref name="inputs"/>. Directories
  /// surface as PFS dirblock entries with the directory type bit set; nested
  /// paths flatten into the root dirblock for parity with the Stage 1 reader.
  /// Image grows past the conventional 880 KB DD floppy when content requires.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new AmigaPfsWriter();
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        w.AddDirectory(input.ArchiveName);
        continue;
      }
      if (input.InMemoryContent is { } bytes) {
        w.AddFile(input.ArchiveName, bytes);
        continue;
      }
      var path = input.FullPath;
      w.AddStreamingFile(input.ArchiveName, new FileInfo(path).Length, () => File.OpenRead(path));
    }
    var label = options?.GetOption("VolumeLabel", "DISK") ?? "DISK";
    if (string.IsNullOrEmpty(label)) label = "DISK";
    w.BuildTo(output, label);
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new AmigaPfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new AmigaPfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      var target = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
      using var output = File.Create(target);
      r.ExtractTo(e, output);
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    using var r = new AmigaPfsReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      // A file is one contiguous extent, so the entry is a plain window onto
      // the image -- no copy, whatever its size.
      var (offset, length) = r.Locate(e);
      return new BoundedEntryStream(new ReadOnlyStreamSlice(archive, offset, length), length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  // ── IFilesystemExtentMap + IWipeEmpty ─────────────────────────────────

  /// <summary>
  /// Reports the volume's layout: the boot block, root block and dirblock chain,
  /// then each file's contiguous extent, with everything between and after them
  /// free. Stage 1 lays a file out as one run starting at its anode number, so the
  /// map is exact.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    List<DefragBlockInfo> result = [];
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new AmigaPfsReader(image);
      var blockSize = reader.BlockSize;

      // Everything before the first file extent is metadata: the boot block, the
      // root block and the dirblock chain all precede the data area.
      var firstData = reader.Length;
      List<DefragBlockInfo> files = [];
      foreach (var e in reader.Entries) {
        if (e.IsDirectory) continue;
        var (offset, length) = reader.Locate(e);
        if (length <= 0) continue;
        if (offset < firstData) firstData = offset;
        files.Add(new DefragBlockInfo(offset, length, DefragBlockKind.Used, e.Name));
      }

      var metadataEnd = files.Count > 0 ? firstData : Math.Min(reader.Length, 82L * blockSize);
      result.Add(new DefragBlockInfo(0, metadataEnd, DefragBlockKind.MetadataReserved,
        "Boot block, root block and dirblock chain"));
      result.AddRange(files);
    } catch {
      return [];
    }
    return result;
  }

  /// <summary>
  /// Zeros every byte no live file occupies. A file is one contiguous extent, so
  /// the gaps between them — and the tail past the last one — are the volume's
  /// free space. Cluster-tip wiping is inherent: an extent is reported at its
  /// logical length, so the padding to the block boundary counts as free.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;
    _ = wipeDeletedEntries;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }

}
