#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ods1;

/// <summary>
/// Read+R/W descriptor for DEC VAX/VMS ODS-1 (Files-11 Level 1) volumes.
/// Signature "DECFILE11A" at file offset 0x3F0 (= LBN 1 + 0x1F0).
/// Reader covers single-extent retrieval pointers; writer emits a fresh
/// Files-11 L1 disk image (home block + index file + bitmap + user-file
/// headers + contiguous extents); modifier mutates existing images in-place
/// via <see cref="Ods1Modifier"/> (Add allocates a free header slot + a
/// contiguous BITMAP run, Remove zeros the header slot + frees its BITMAP
/// bits + zero-fills its data extent; both recompute the home-block additive
/// checksums). Self-round-trip gated; no Linux fsck for ODS-1 exists.
///
/// References:
/// <list type="bullet">
///   <item><description>DEC "Files-11 On-Disk Structure Specification" — the canonical ODS-1/ODS-2 spec (archived at Bitsavers)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Files-11</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class Ods1FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {

  /// <summary>
  /// Sole tunable the Files-11 L1 writer honours: the 12-character home-block
  /// volume name (hm1$t_volname). The rest of the Stage-1 geometry is fixed.
  /// An empty label falls back to the writer default ("CWBVOL").
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 12),
  ];

  public string Id => "Ods1";
  public string DisplayName => "ODS-1 (VAX/VMS Files-11 L1)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".ods1";
  public IReadOnlyList<string> Extensions => [".ods1", ".vms"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "DECFILE11A" at file offset 0x200 + 0x1F0 = 0x3F0
    new([(byte)'D', (byte)'E', (byte)'C', (byte)'F', (byte)'I', (byte)'L', (byte)'E', (byte)'1', (byte)'1', (byte)'A'], Offset: 0x3F0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "DEC ODS-1 (RSX-11/VAX-VMS Files-11 Level 1) volume — read + R/W create + in-place " +
    "Add/Remove (Stage 1: single-extent retrieval pointers, ASCII filenames, ≤ 9.3 chars, " +
    "64-slot INDEXF window, home-block additive checksums recomputed on every mutation).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Ods1Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new Ods1Reader(stream);
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
  /// Builds a fresh ODS-1 disk image. Inputs are stored in the root with
  /// 9.3 ASCII filenames (longer names are truncated by
  /// <see cref="Ods1Writer.SplitName"/>); directory inputs are skipped
  /// (ODS-1 Stage-1 has no subdirectory support).
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = new List<(string Name, Compression.Core.DiskImage.FilePayload Payload)>();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var info = input;
      // Only the length is needed to lay the volume out; reading a large input
      // into a byte[] would cap it at what an array can hold.
      files.Add((Path.GetFileName(info.ArchiveName), info.InMemoryContent is { } bytes
        ? Compression.Core.DiskImage.FilePayload.FromBytes(bytes)
        : Compression.Core.DiskImage.FilePayload.FromStream(
            new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath))));
    }
    var volumeName = options?.GetOption("VolumeLabel", "") ?? "";
    if (string.IsNullOrEmpty(volumeName)) volumeName = "CWBVOL";
    Ods1Writer.WriteTo(output, files, volumeName);
  }

  /// <summary>
  /// Adds files to an existing ODS-1 image via <see cref="Ods1Modifier.AddFile"/>.
  /// Each input gets a free header slot in the 64-slot INDEXF window plus a
  /// contiguous BITMAP run for its data extent. Directory inputs are skipped
  /// (Stage-1 has no subdirectory support). Throws
  /// <see cref="NotSupportedException"/> when INDEXF or BITMAP is exhausted.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    // The in-place modifier reads the volume into an array to walk its
    // structures, which a volume past two gigabytes does not fit in. Above that
    // the edit is applied by unpacking and relaying the volume out instead.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, this);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var leaf = Path.GetFileName(input.ArchiveName);
      // Replace-by-name: drop any prior entry first so an update overwrites in place
      // rather than leaving a duplicate directory record.
      Ods1Modifier.RemoveFile(archive, leaf);
      Ods1Modifier.AddFile(archive, leaf, input.ReadContent());
    }
  }

  /// <summary>
  /// Removes the named entries from an existing ODS-1 image via
  /// <see cref="Ods1Modifier.RemoveFile"/>. Each removal frees the file's
  /// BITMAP bits, zero-fills its data extent (no forensic recovery), and
  /// zero-fills its file-header slot. Unknown names are silently skipped.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      Ods1Modifier.RemoveFile(archive, name);
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new Ods1Reader(archive);
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

  // ── IFilesystemExtentMap + IWipeEmpty ─────────────────────────────────

  /// <summary>
  /// Reports the volume's layout: the boot and home blocks, the allocation bitmap
  /// and the index-file window as metadata, then each file's retrieval pointers.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    List<DefragBlockInfo> result = [];
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new Ods1Reader(image);

      var firstData = reader.Length;
      List<DefragBlockInfo> files = [];
      foreach (var e in reader.Entries) {
        if (e.IsDirectory || e.Size <= 0) continue;
        long written = 0;
        foreach (var (lbn, blocks) in e.Extents ?? [(e.StartLbn, e.BlockCount)]) {
          var offset = (long)lbn * Ods1Reader.LbnSize;
          var take = Math.Min((long)blocks * Ods1Reader.LbnSize, e.Size - written);
          if (take <= 0) break;
          if (offset < firstData) firstData = offset;
          files.Add(new DefragBlockInfo(offset, take, DefragBlockKind.Used, e.Name));
          written += take;
        }
      }

      var metadataEnd = files.Count > 0 ? firstData : Math.Min(reader.Length, 68L * Ods1Reader.LbnSize);
      result.Add(new DefragBlockInfo(0, metadataEnd, DefragBlockKind.MetadataReserved,
        "Boot block, home block, BITMAP.SYS and the index-file window"));
      result.AddRange(files);
    } catch {
      return [];
    }
    return result;
  }

  /// <summary>Zeros every byte no live file occupies, including the block padding past each file.</summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;
    _ = wipeDeletedEntries;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }


  // ── IArchiveDefragmentable ─────────────────────────────────────────────

  /// <inheritdoc />
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Moves only the files that are out of place, rewriting each one's retrieval
  /// pointer as its blocks arrive. The pass is kept only if every payload still
  /// reads back: it can refuse partway — a header it cannot find leaves bytes
  /// moved with nothing naming them — and the volume is restored when it does.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    DefragContentGuard.RunOrRebuild(archive,
      readContents: stream => {
        using var reader = new Ods1Reader(stream);
        return reader.Entries.Where(e => !e.IsDirectory).Select(reader.Extract).ToList();
      },
      inPlace: () => this.DefragmentWithPlanner(archive, options),
      rebuild: () => { /* the volume is put back as it was */ });
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new Ods1BlockMover();
    mover.Init(archive);

    var extents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    // A file described by several retrieval pointers needs its whole map area
    // restated, which this pass cannot do.
    var runsPerOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var extent in extents) {
      if (extent.Kind != DefragBlockKind.Used || extent.FileName is not { } owner) continue;
      runsPerOwner.TryGetValue(owner, out var count);
      runsPerOwner[owner] = count + 1;
    }
    var fragmented = runsPerOwner.Count(kv => kv.Value > 1);
    if (fragmented > 0)
      throw new NotSupportedException(
        $"ODS-1: {fragmented} file(s) span more than one retrieval pointer.");

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.FirstDataByte, archive.Length, mover.BlockSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement);
    if (moves.Count == 0) return;

    Compression.Core.Layout.DefragPlannerExecutor.Execute(archive, options, mover, moves,
      archive.Length, reinitAfterMove: null);

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

}
