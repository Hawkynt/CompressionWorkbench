#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.ExFat;

public sealed class ExFatFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover {

  /// <summary>
  /// Walks the VBR + FAT + cluster heap and yields the actual on-disk
  /// layout — VBR/backup VBR + FAT region as MetadataReserved, allocation
  /// bitmap + up-case table as MetadataReserved, every file's cluster-chain
  /// run (or the contiguous range when <c>NoFatChain</c> is set) as Used,
  /// and the un-owned cluster gaps as Free.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => ExFatExtentMap.Enumerate(image);

  public string Id => "ExFat";
  public string DisplayName => "exFAT";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new ExFatBlockMover();
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    mover.Init(ms.ToArray());
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new ExFatBlockMover();
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    mover.Init(ms.ToArray());
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware exFAT defragmentor. Supports planner-driven in-place path
  /// and falls back to legacy rebuild path.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      try {
        DefragmentWithPlanner(archive, options);
        return;
      } catch {
        archive.Position = 0;
      }
    }
    DefragmentWithRebuild(archive, options);
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new ExFatBlockMover();
    mover.Init(archive); // reads only the 512-byte VBR

    // Stream the extent map directly off the archive — no whole-image load.
    var extents = ExFatExtentMap.Enumerate(archive).ToList();
    // Use the VBR-declared volume size (clusterHeapOffset + clusterCount * clusterSize)
    // rather than archive.Length so the planner doesn't target offsets past the end
    // of the cluster heap. When the exFAT image sits in a larger container
    // (partition window, sparse VHD), archive.Length includes trailing padding
    // bytes that are NOT part of the exFAT volume — placing a file there would
    // assign it a cluster number outside [2, clusterCount+1] and cause
    // UpdateAllocationAfterMove to write a FAT entry past fatLength, corrupting
    // the cluster heap contents.
    var volumeSize = Math.Min(mover.VolumeSize, archive.Length);
    options.OnProgress?.Invoke(new DefragProgressEvent("scanning", 0, 0, -1, volumeSize, extents, "Analysing layout"));

    var moves = DefragPlanner.Plan(extents, mover.FirstDataByte, volumeSize, mover.ClusterSize, options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, volumeSize, extents, "Already defragmented"));
      return;
    }

    // VBR doesn't change during defrag — no per-move re-init needed.
    DefragPlannerExecutor.Execute(archive, options, mover, moves, volumeSize, reinitAfterMove: null);

    options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, volumeSize, null, "Defragmentation complete"));
  }

  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    var sizeMB = (int)System.Math.Max(8, (archive.Length + 1024 * 1024 - 1) / (1024 * 1024));
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new ExFatReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new ExFatWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build(sizeMB);
      });
  }
  public string DefaultExtension => ".img";
  public IReadOnlyList<string> Extensions => [".img", ".exfat"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("EXFAT   "u8.ToArray(), Offset: 3, Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "exFAT filesystem image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ExFatReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new ExFatWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ExFatReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Adds (or replaces by name) files to an existing exFAT image. Uses
  /// <see cref="ExFatModifier"/> for true O(touched bytes) random-access I/O —
  /// only the FAT entries for new clusters, the allocation-bitmap byte(s) covering
  /// them, the root-directory cluster(s) holding the entry-set, the new file's
  /// data clusters, and the VBR PercentInUse byte are touched. The up-case table
  /// and all other files are never read.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FlatFiles(inputs)) {
      ExFatModifier.RemoveFile(archive, name, wipeData: true);
      ExFatModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes files from an existing exFAT image with full secure wipe (cluster
  /// bytes, FAT chain, allocation bitmap bits, directory entry set). Uses
  /// <see cref="ExFatModifier"/> for O(touched bytes) random-access I/O — no
  /// forensic recovery of the removed content is possible from the resulting bytes.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      ExFatModifier.RemoveFile(archive, name, wipeData: true);
  }
}
