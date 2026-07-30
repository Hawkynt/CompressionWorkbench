#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Nilfs2;

/// <summary>
/// NILFS2 descriptor (continuous-snapshot log-structured filesystem, Linux mainline
/// since 2.6.30). Magic 0x3434 sits at superblock+6 (file offset 1030).
///
/// <para><b>R/W scope.</b> Create emits a spec-compliant superblock plus a
/// writer-private compact directory at offset 2048 (the base checkpoint at
/// cno=1). Add / Replace / Remove append a fresh log segment ("NILFS2SG"
/// header + cno + dirents + payload) at the tail of the volume and bump
/// <c>s_last_cno</c> in the superblock — the only in-place edit, sanctioned by
/// the NILFS2 spec for advancing the checkpoint pointer. Every byte of every
/// prior segment stays byte-identical at its original offset, so the older
/// state is byte-recoverable as a snapshot (continuous-snapshot semantic).</para>
///
/// <para><b>Kernel-mountable.</b> Create emits the full single-checkpoint log the
/// Linux <c>nilfs2</c> driver needs to mount: a super root with the DAT / cpfile /
/// sufile inodes (+ their CRC), a segment summary with the spec
/// ss_sumsum / ss_datasum checksums, an ifile holding the root directory inode, a
/// DAT (Disk Address Translation) table, and a flat root directory carrying the
/// files. A real <c>mount -t nilfs2</c> mounts the image, lists the directory,
/// and reads the files back (verified via the libguestfs appliance kernel).
/// Subdirectories and files larger than a direct block map stay in the
/// writer-private directory for the reader but are not materialised in the
/// mountable tree; snapshots / multi-checkpoint chains remain out of scope.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://nilfs.sourceforge.io/</c> — NILFS project home</description></item>
///   <item><description><c>https://www.kernel.org/doc/html/latest/filesystems/nilfs2.html</c> — kernel documentation</description></item>
///   <item><description><c>https://github.com/torvalds/linux/blob/master/include/uapi/linux/nilfs2_ondisk.h</c> — canonical on-disk structures</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/NILFS</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class Nilfs2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {

  /// <summary>Synthetic surface entries the reader always injects — excluded from rebuilds.</summary>
  private static readonly HashSet<string> SyntheticEntries =
    new(StringComparer.Ordinal) { "FULL.nilfs2", "metadata.ini", "superblock.bin" };

  /// <summary>
  /// Creation knobs. <c>BlockSize</c> is the NILFS2 block size (power of two in
  /// [1024, 65536], recorded in <c>s_log_block_size</c>): leave it at "auto" (0)
  /// to let the layout optimiser pick the legal size that minimises wasted tail
  /// padding for the file-set, or pin a value. <c>VolumeLabel</c> fills the 16-byte
  /// superblock label slot.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new("BlockSize", "Block size", FormatOptionKind.Enum, "0",
      AllowedValues: ["0", "1024", "2048", "4096", "8192", "16384", "32768", "65536"],
      Description: "NILFS2 block size in bytes (0 = auto-optimise for least padding slack; spec allows 1024..65536)."),
    new("VolumeLabel", "Volume label", FormatOptionKind.String, "",
      Description: "Up to 16 ASCII characters written into the superblock volume-label slot."),
  ];
  public string Id => "Nilfs2";
  public string DisplayName => "NILFS2";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".nilfs2";
  public IReadOnlyList<string> Extensions => [".nilfs2", ".nilfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // NILFS_SUPER_MAGIC == 0x3434, little-endian at superblock+6 == file offset 1030.
    new([0x34, 0x34], Offset: 1030, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "NILFS2 continuous-snapshot log-structured filesystem — Create emits a kernel-mountable single-checkpoint image: a byte-accurate, CRC-valid superblock pair (primary at 1024 + backup before EOF, s_bytes=280, crc32_le-sealed s_sum, label at +0xA8) plus the full log (super root with DAT/cpfile/sufile inodes + CRC, segment summary with ss_sumsum/ss_datasum, ifile holding the root-dir inode, DAT table, flat root directory with the files). The real nilfs2 kernel driver mounts it and reads the files back (verified via the libguestfs appliance). Add/Replace/Remove append a fresh log segment at the tail and bump s_last_cno (spec-sanctioned in-place edit); prior segments stay byte-identical (continuous-snapshot invariant). The reader validates real mkfs.nilfs2 superblocks (checksum + dual-SB selection). Subdirectories / large files and multi-checkpoint snapshots remain out of scope.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Nilfs2Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new Nilfs2Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      var target = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
      using var output = File.Create(target);
      r.ExtractTo(e, output);
    }
  }

  /// <summary>
  /// Emits a self-contained NILFS2 image (valid superblock + base private
  /// directory at cno=1). Round-trips through this descriptor's reader and
  /// serves as the substrate for in-place Add / Replace / Remove via
  /// <see cref="Nilfs2InPlaceModifier"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var writer = new Nilfs2Writer();
    // Inputs are streamed rather than read in: the payload region is copied
    // through to the image, so it may be larger than memory.
    var sizes = new List<long>();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      if (input.InMemoryContent is { } bytes) {
        writer.AddFile(input.ArchiveName, bytes);
        sizes.Add(bytes.LongLength);
        continue;
      }
      var path = input.FullPath;
      var length = new FileInfo(path).Length;
      writer.AddStreamingFile(input.ArchiveName, length, () => File.OpenRead(path));
      sizes.Add(length);
    }

    // Block size: a pinned value is honoured verbatim; when unset, the optimiser
    // picks the legal size minimising tail-padding slack for the file-set. The
    // NILFS2 reader/modifier are block-size-agnostic (they key off the writer
    // directory magic and s_log_block_size), so every candidate round-trips.
    var label = options.GetOption("VolumeLabel", "");
    var blockSize = options.HasOption("BlockSize")
      ? options.GetOptionInt("BlockSize", 4096)
      : Compression.Core.Layout.LayoutOptimizerAdapter.SelectAllocationUnit(
          [1024, 2048, 4096, 8192, 16384, 32768, 65536], sizes);
    writer.Build(output, blockSize, string.IsNullOrEmpty(label) ? null : label);
  }

  // ── IArchiveModifiable ────────────────────────────────────────────────

  /// <summary>
  /// Appends a fresh log segment at the tail of the image carrying dirent +
  /// data blocks for each input, and bumps <c>s_last_cno</c> in the superblock.
  /// The 8-byte cno field is the only in-place edit; every other byte of the
  /// prior image stays byte-identical at its original offset — continuous
  /// snapshot semantic intact. Inputs whose name already exists are
  /// effectively replaced (the higher cno wins on read).
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    Nilfs2InPlaceModifier.Add(archive, inputs);
  }

  /// <summary>
  /// Appends a tombstone dirent for each named entry in a fresh log segment and
  /// bumps <c>s_last_cno</c>. The reader's cno-merge drops the entry from the
  /// listing; the original data blocks stay byte-identical at their original
  /// offsets and remain addressable as a snapshot of the pre-Remove state.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    Nilfs2InPlaceModifier.Remove(archive, entryNames);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Rewrites the volume as a single checkpoint holding the live state — the
  /// same reclamation the NILFS2 segment cleaner performs, which is what frees
  /// the space that superseded and tombstoned records still occupy. Older
  /// checkpoints are what a cleaner run reclaims, so they do not survive it;
  /// the live file set does, byte for byte.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);
    // Every consolidate mode lands on the same layout here: the writer emits a
    // fresh volume packed from the first data block, and has no way to place
    // files against the tail. Carving a hole is the one request it cannot meet.
    if (options.Mode is DefragMode.CarveHole)
      throw new NotSupportedException(
        "Nilfs2 defragmentation cannot carve a hole: the rebuild always start-packs the volume.");

    var tempPath = Path.GetTempFileName();
    try {
      using (var temp = File.Open(tempPath, FileMode.Open, FileAccess.ReadWrite)) {
        this.RebuildStreaming(archive, temp, new LayoutRebuildOptions { UnitSize = 0 });

        options.OnProgress?.Invoke(new DefragProgressEvent(
          Phase: "commit", Fraction: 1.0, CurrentReadOffset: archive.Length,
          CurrentWriteOffset: temp.Length, ImageSize: temp.Length, BlockMap: null));

        temp.Position = 0;
        archive.Position = 0;
        temp.CopyTo(archive);
        archive.SetLength(temp.Length);
        archive.Flush();
      }
    } finally {
      File.Delete(tempPath);
    }
  }

  // ── ILayoutOptimizable ────────────────────────────────────────────────
  //
  // NILFS2's block size is reader-agnostic (read back from s_log_block_size),
  // so any legal size round-trips. Because the writer packs payloads
  // contiguously rather than in per-file blocks, the slack the optimiser trims
  // here is the image's tail padding to a whole block, not per-file cluster
  // tails — the contract still fits, just with a smaller savings surface than a
  // cluster-allocating filesystem. PatchInPlace updates the 16-byte volume
  // label; a block-size change is a structural rebuild.

  private static readonly int[] BlockCandidates = [1024, 2048, 4096, 8192, 16384, 32768, 65536];

  /// <inheritdoc />
  public LayoutAnalysis AnalyzeLayout(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.CanSeek) image.Position = 0;
    var reader = new Nilfs2Reader(image);
    var current = 1024 << (int)reader.LogBlockSize;
    var fileSizes = reader.Entries
      .Where(e => !e.IsDirectory && !SyntheticEntries.Contains(e.Name))
      .Select(e => e.Size).ToList();

    var optimal = Compression.Core.Layout.LayoutOptimizerAdapter.SelectAllocationUnit(BlockCandidates, fileSizes);
    var currentSlack = Compression.Core.Layout.LayoutOptimizerAdapter.SlackAt(fileSizes, current);
    var optimalSlack = Compression.Core.Layout.LayoutOptimizerAdapter.SlackAt(fileSizes, optimal);
    return new LayoutAnalysis {
      ImageSize = image.CanSeek ? image.Length : 0,
      CurrentUnitSize = current,
      CurrentSlackBytes = currentSlack,
      OptimalUnitSize = optimal,
      OptimalSlackBytes = optimalSlack,
      InPlaceChanges = ["volume label"],
      RequiresRebuild = optimal != current ? ["block size"] : [],
      Notes = optimal == current
        ? ["Block size is already optimal for this file-set."]
        : [$"Rebuild at {optimal}-byte blocks trims padding slack."],
    };
  }

  /// <inheritdoc />
  public void PatchInPlace(Stream image, LayoutPatch patch) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(patch);
    if (patch.VolumeLabel is { } label) {
      // Volume label lives at superblock + 0xA8 (the spec offset the encoder
      // writes and the reader parses). Editing it invalidates the superblock
      // CRC, so reseal s_sum afterwards over the first s_bytes with the stored
      // crc seed (Linux crc32_le).
      var sb = new byte[Nilfs2Superblock.Size];
      image.Position = 1024;
      if (image.Read(sb, 0, sb.Length) == sb.Length) {
        Array.Clear(sb, 0xA8, 16);
        var bytes = System.Text.Encoding.ASCII.GetBytes(label);
        bytes.AsSpan(0, Math.Min(bytes.Length, 16)).CopyTo(sb.AsSpan(0xA8));
        var seed = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x0C));
        Nilfs2Superblock.FinalizeChecksum(sb, seed);
        image.Position = 1024;
        image.Write(sb, 0, sb.Length);
      }
    }
  }

  /// <inheritdoc />
  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);
    if (source.CanSeek) source.Position = 0;
    using var reader = new Nilfs2Reader(source);
    var w = new Nilfs2Writer();
    var fileSizes = new List<long>();
    // Each file is spilled to scratch so the rebuild streams it back rather than
    // holding the whole payload while the new image is assembled.
    var spill = new List<string>();
    try {
      foreach (var e in reader.Entries) {
        if (e.IsDirectory || SyntheticEntries.Contains(e.Name)) continue;
        var path = Path.GetTempFileName();
        spill.Add(path);
        using (var scratch = File.Create(path))
          reader.ExtractTo(e, scratch);
        var captured = path;
        w.AddStreamingFile(e.Name, e.Size, () => File.OpenRead(captured));
        fileSizes.Add(e.Size);
      }
      var blockSize = options.UnitSize > 0
        ? options.UnitSize
        : Compression.Core.Layout.LayoutOptimizerAdapter.SelectAllocationUnit(BlockCandidates, fileSizes);
      var start = target.Position;
      w.Build(target, blockSize);
      options.OnProgress?.Invoke(target.Position - start, target.Position - start);
    } finally {
      foreach (var path in spill)
        try { File.Delete(path); } catch { /* scratch file already gone */ }
    }
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <summary>
  /// Superblocks, the kernel log, the private directory and every appended
  /// segment's header are metadata; each live payload is the file that owns it.
  /// Superseded and tombstoned payloads are claimed by nothing, so a wipe
  /// reclaims exactly the bytes a segment cleaner would.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new Nilfs2Reader(image);
      foreach (var (offset, length) in reader.MetadataRegions)
        if (length > 0)
          result.Add(new DefragBlockInfo(offset, length, DefragBlockKind.MetadataReserved));
      var live = new HashSet<string>(StringComparer.Ordinal);
      foreach (var e in reader.Entries
                 .Where(x => x.Offset >= 0 && x.Size > 0 && !SyntheticEntries.Contains(x.Name))) {
        result.Add(new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, e.Name));
        live.Add(e.Name);
      }
      // The checkpoint's copy of an embedded file counts as that file's bytes
      // only while the file is live; once it is gone, so is its claim on them.
      foreach (var (offset, length, name) in reader.LogFileRegions)
        if (live.Contains(name))
          result.Add(new DefragBlockInfo(offset, length, DefragBlockKind.Used, name));
      if (result.Count == 0 && image.Length > 0)
        result.Add(new DefragBlockInfo(0, Math.Min(4096, image.Length), DefragBlockKind.MetadataReserved));
    } catch {
      // An image we cannot parse claims nothing, and a wipe of it would zero
      // every byte — so say it has no known extents and let the caller decide.
      return [];
    }
    return result;
  }

  /// <inheritdoc />
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;
    // Records are packed to the byte, so there are no cluster tips to trim —
    // only the slack a removal or a shorter replacement left behind.
    //
    // A file small enough to be embedded in the kernel checkpoint has a second
    // copy among the log's data blocks; the extent map claims those blocks only
    // for files that are still live, so a removed file loses both copies.
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }

}
