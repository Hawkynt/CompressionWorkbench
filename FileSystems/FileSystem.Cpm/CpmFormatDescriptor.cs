#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Cpm;

/// <summary>
/// Read+write descriptor for CP/M 2.2 disk images using the 8" SSSD reference
/// geometry (256 256 bytes, 2 reserved tracks, 1024-byte blocks, 64 directory
/// entries). Kaypro/Osborne/Amstrad and other manufacturer-specific geometries
/// are not emitted by the writer; the reader still parses any image that
/// matches this layout.
/// </summary>
public sealed class CpmFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty {

  /// <summary>
  /// Walks the 64-entry CP/M directory and yields the actual on-disk byte
  /// layout — the 2 reserved tracks (BIOS) + the 2 KB directory area as
  /// <see cref="DefragBlockKind.MetadataReserved"/>, every per-file
  /// allocation-block list as one or more contiguous-run extents (coalesced
  /// across extents), and unreferenced data blocks as
  /// <see cref="DefragBlockKind.Free"/>.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => CpmExtentMap.Enumerate(image);

  public string Id => "Cpm";
  public string DisplayName => "CP/M 2.2 (8\" SSSD)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".cpm";
  public IReadOnlyList<string> Extensions => [".cpm", ".dsk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // CP/M disks have no magic — only geometry — so we advertise no magic-byte
  // signature. Detection falls back to extension-based matching.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "CP/M 2.2 disk image (8\" SSSD canonical geometry) — 77 tracks × 26 sectors × 128 B, " +
    "1024-byte allocation blocks, 64-entry directory, 8.3 filenames.";

  // Write constraints.
  public long? MaxTotalArchiveSize => CpmLayout.UsableBlocks * (long)CpmLayout.BlockSize;
  public long? MinTotalArchiveSize => 0;
  public string AcceptedInputsDescription =>
    $"Up to {CpmLayout.DirectoryEntries} directory entries, {CpmLayout.UsableBlocks} × 1024-byte blocks of data; 8.3 filenames.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) { reason = "CP/M volumes have a single flat directory — no subdirectories."; return false; }
    var file = Path.GetFileName(input.ArchiveName);
    var dot = file.LastIndexOf('.');
    var name = dot < 0 ? file : file[..dot];
    var ext = dot < 0 ? "" : file[(dot + 1)..];
    if (name.Length > 8) { reason = "Filename stem exceeds 8 characters."; return false; }
    if (ext.Length > 3) { reason = "Extension exceeds 3 characters."; return false; }
    reason = null;
    return true;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var v = ReadVolume(stream);
    return v.Files.Select((f, i) => new ArchiveEntryInfo(
      i, f.FullName, f.Data.LongLength, f.Data.LongLength, "stored",
      false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var v = ReadVolume(stream);
    foreach (var f in v.Files) {
      if (files != null && files.Length > 0 && !MatchesFilter(f.FullName, files)) continue;
      WriteFile(outputDir, f.FullName, f.Data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var files = inputs
      .Where(i => !i.IsDirectory)
      .Select(i => (i.ArchiveName, File.ReadAllBytes(i.FullPath), (byte)0))
      .ToList();
    var image = CpmWriter.Build(files);
    output.Write(image);
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing CP/M image.
  /// Uses <see cref="CpmModifier"/> for true O(touched bytes) random-access I/O —
  /// only the 2 KB directory + the affected file's data blocks are read or written.
  /// Replacement semantics: pre-existing entries with the same (name, ext) under
  /// user code 0 are removed (and their data wiped) before the new file is written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var data = File.ReadAllBytes(input.FullPath);
      CpmModifier.RemoveFile(archive, input.ArchiveName, userCode: 0, wipeData: true);
      CpmModifier.AddFile(archive, input.ArchiveName, data, userCode: 0);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing CP/M image. Uses
  /// <see cref="CpmModifier"/> for O(touched bytes) random-access I/O —
  /// matching directory entries are flipped to 0xE5 and data blocks are zeroed.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      CpmModifier.RemoveFile(archive, name, userCode: 0, wipeData: true);
  }

  /// <summary>
  /// Zeros all unused space in a CP/M image: unreferenced 1024-byte allocation
  /// blocks and the cluster-tip slack at the tail of each file's last block.
  /// CP/M allocates whole blocks but tracks length to 128-byte record
  /// granularity, so the bytes between a file's real length and its last
  /// allocated block boundary are slack and get zero-filled when
  /// <paramref name="wipeClusterTips"/> is set. Live file data, the BIOS
  /// reserved tracks, and the 2 KB directory are preserved.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    // The extent map names Used runs by the CP/M "name.ext" full name, which is
    // exactly CpmFile.FullName — so cluster-tip detection lines up.
    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        var volume = ReadVolume(image);
        var sizeMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in volume.Files)
          sizeMap[file.FullName] = file.Data.LongLength;
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    var extents = CpmExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new CpmBlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new CpmBlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware CP/M defragmentor. Tries planner-driven in-place path first,
  /// falls back to rebuild path on error.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      // Save a snapshot so the planner path can't corrupt the image if it fails.
      archive.Position = 0;
      using var snapshot = new MemoryStream();
      archive.CopyTo(snapshot);
      try {
        archive.Position = 0;
        DefragmentWithPlanner(archive, options);
        return;
      } catch {
        // Restore the original image before falling back to rebuild.
        archive.Position = 0;
        snapshot.Position = 0;
        snapshot.CopyTo(archive);
        archive.SetLength(snapshot.Length);
        archive.Position = 0;
      }
    }

    DefragmentWithRebuild(archive, options);
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var imageSize = archive.Length;

    var mover = new CpmBlockMover();
    var extents = CpmExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "scanning", Fraction: 0, CurrentReadOffset: 0, CurrentWriteOffset: -1,
      ImageSize: imageSize, BlockMap: extents, Status: "Analysing layout"));

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.DataOrigin, imageSize, mover.BlockSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);

    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        Phase: "complete", Fraction: 1, CurrentReadOffset: -1, CurrentWriteOffset: -1,
        ImageSize: imageSize, BlockMap: extents, Status: "Already defragmented"));
      return;
    }

    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize);

    var postExtents = CpmExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      Phase: "complete", Fraction: 1, CurrentReadOffset: -1, CurrentWriteOffset: -1,
      ImageSize: imageSize, BlockMap: postExtents, Status: "Defragmentation complete"));
  }

  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var v = ReadVolume(stream);
        return v.Files.Select(f => (f.FullName, f.Data));
      },
      buildImage: files => CpmWriter.Build(
        files.Select(f => (f.Name, f.Data, (byte)0)).ToList()));
  }

  private static CpmReader.Volume ReadVolume(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return CpmReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
  }
}
