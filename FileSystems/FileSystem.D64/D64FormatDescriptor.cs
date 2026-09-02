#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.D64;

/// <summary>
/// References:
/// <list type="bullet">
///   <item><description><c>http://unusedino.de/ec64/technical/formats/d64.html</c> — Peter Schepers' D64 format specification (BAM, directory, track/sector layout)</description></item>
///   <item><description>"Inside Commodore DOS" (Richard Immers &amp; Gerald Neufeld, Datamost, 1984) — the canonical 1541 DOS internals book</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Commodore_1541</c> — Wikipedia overview of the drive whose disks D64 images</description></item>
/// </list>
/// </summary>
public sealed class D64FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
  IArchiveWriteConstraints, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable,
  IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable,
  IRandomAccessBlockDeviceProvider, IFilesystemDriverProvider {

  /// <summary>
  /// Tunable knobs for D64 creation. The Commodore 1541 stores a 16-char
  /// PETSCII disk name plus a 2-char disk ID in the BAM (track 18 sector 0);
  /// both are user-visible from the C64 directory listing. Disk geometry is
  /// fixed at the single-sided 1541 size (174 848 bytes).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 16),
    new FormatOptionDescriptor(
      Key: "DiskId",
      DisplayName: "Disk ID",
      Kind: FormatOptionKind.String,
      Default: "00",
      Description: "Two-character disk ID at BAM offset 0xA2. Shown in the C64 directory " +
        "header (\"0 \"DISKNAME\" ID 2A\"). Padded with '0' if shorter, truncated if longer."),
  ];

  /// <summary>
  /// Walks the directory chain on track 18 and yields the actual on-disk
  /// byte layout — track 18 (BAM + directory) as <see cref="DefragBlockKind.MetadataReserved"/>,
  /// every per-file sector chain as one or more contiguous-run extents, and
  /// the un-attributed sectors as <see cref="DefragBlockKind.Free"/>. Used by
  /// the defragment window's block-map preview.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => D64ExtentMap.Enumerate(image);

  private const int D64SectorSize = 256;
  private const int D64DirTrack = 18;
  private const int D64DirStartSector = 1;
  private static readonly int[] D64SectorsPerTrack = [
    0,
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    19, 19, 19, 19, 19, 19, 19,
    18, 18, 18, 18, 18, 18,
    17, 17, 17, 17, 17,
  ];

  private static int D64SectorOffset(int track, int sector) {
    if (track < 1 || track >= D64SectorsPerTrack.Length) return -1;
    if (sector < 0 || sector >= D64SectorsPerTrack[track]) return -1;
    var offset = 0;
    for (var t = 1; t < track; t++) offset += D64SectorsPerTrack[t] * D64SectorSize;
    return offset + sector * D64SectorSize;
  }

  /// <summary>
  /// Zeros all unused space in a D64 image: unallocated sectors and the
  /// cluster-tip slack at the tail of each file's <em>last</em> sector. A D64
  /// file is a linked chain of 256-byte sectors — each carries a 2-byte
  /// next-track/next-sector link followed by up to 254 data bytes. The final
  /// sector's link is <c>(0, used+1)</c>, so the bytes after the last used
  /// data byte up to the sector boundary are slack. Those slack bytes are
  /// zero-filled when <paramref name="wipeClusterTips"/> is set, while the
  /// 2-byte link headers, live file data, and the track-18 BAM/directory are
  /// preserved.
  ///
  /// <para>Because file content is interleaved with per-sector link bytes and
  /// chains may be fragmented, the simple "offset + size" cluster-tip model of
  /// the generic wiper does not apply; tip wiping is done here by walking each
  /// chain to its final sector. Free-space wiping is delegated to the generic
  /// wiper using the extent map.</para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);

    using var ms = new MemoryStream();
    image.Position = 0;
    image.CopyTo(ms);
    var data = ms.GetBuffer();
    var length = (int)ms.Length;
    var totalWiped = 0L;

    if (wipeClusterTips && length >= 174848) {
      var t = D64DirTrack;
      var s = D64DirStartSector;
      var visitedDir = new HashSet<(int, int)>();
      while (t != 0 && visitedDir.Add((t, s))) {
        var dirOff = D64SectorOffset(t, s);
        if (dirOff < 0 || dirOff + D64SectorSize > length) break;
        var nextTrack = data[dirOff];
        var nextSector = data[dirOff + 1];
        for (var i = 0; i < 8; i++) {
          var entryOff = dirOff + i * 32;
          var fileType = data[entryOff + 2];
          if ((fileType & 0x07) == 0) continue;
          totalWiped += WipeChainTail(data, length, data[entryOff + 3], data[entryOff + 4]);
        }
        t = nextTrack;
        s = nextSector;
      }
    }

    var msStream = new MemoryStream(data, 0, length, writable: true);
    msStream.Position = 0;
    var extents = D64ExtentMap.Enumerate(msStream);
    msStream.Position = 0;
    totalWiped += UnusedSpaceWiper.Wipe(msStream, extents, length, wipeClusterTips: false, fileSizeLookup: null);
    msStream.Flush();

    image.Position = 0;
    image.Write(data, 0, length);
    image.SetLength(length);
    image.Flush();
    return totalWiped;
  }

  /// <summary>
  /// Walks one file's sector chain to its final sector and zeros the slack
  /// bytes between the last used data byte and the 256-byte sector boundary,
  /// preserving the 2-byte link header. Returns bytes actually changed.
  /// </summary>
  private static long WipeChainTail(byte[] data, int length, int startTrack, int startSector) {
    var t = startTrack;
    var s = startSector;
    var visited = new HashSet<(int, int)>();
    while (t != 0 && visited.Add((t, s))) {
      var off = D64SectorOffset(t, s);
      if (off < 0 || off + D64SectorSize > length) return 0;
      var nextTrack = data[off];
      var nextSector = data[off + 1];
      if (nextTrack == 0) {
        var bytesUsed = nextSector > 1 ? nextSector - 1 : 254;
        var tipStart = off + 2 + bytesUsed;
        var changed = 0L;
        for (var p = tipStart; p < off + D64SectorSize; p++) {
          if (data[p] != 0) { data[p] = 0; changed++; }
        }
        return changed;
      }
      t = nextTrack;
      s = nextSector;
    }
    return 0;
  }

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => 174848;  // standard 1541 single-sided D64 image size
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "Commodore 1541 D64 disk; any file up to 174 848 bytes total (664 data sectors × 254 bytes).";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    reason = null;
    return true;
  }

  /// <summary>
  /// Gets the canonical sizes.
  /// </summary>
  public IReadOnlyList<long> CanonicalSizes => [174848];
  /// <summary>
  /// Performs the shrink operation.
  /// </summary>
  public void Shrink(Stream input, Stream output) =>
    ArchiveShrinker.ShrinkViaRebuild(input, output, this, this, this.CanonicalSizes);

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "D64";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "D64";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing D64 image.
  /// Uses <see cref="D64Modifier"/> for true O(touched bytes) random-access
  /// I/O — only the BAM (1 sector) + directory chain (≤19 sectors) + the
  /// new file's data sectors (⌈len/254⌉ sectors) are read or written. The
  /// 174 848-byte image isn't touched outside that.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      var truncatedName = name.Length > 16 ? name[..16] : name;
      D64Modifier.RemoveFile(archive, truncatedName, wipeData: true);
      D64Modifier.AddFile(archive, truncatedName, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing D64 image. Uses
  /// <see cref="D64Modifier"/> for O(touched bytes) random-access I/O —
  /// walks the file chain, marks each sector free in the BAM, secure-wipes
  /// data sectors, and clears the directory entry's file-type byte.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames) {
      var truncatedName = name.Length > 16 ? name[..16] : name;
      D64Modifier.RemoveFile(archive, truncatedName, wipeData: true);
    }
  }

  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".d64";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".d64"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
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
  public string Description => "Commodore 64 1541 disk image with mount-grade sector and CBM DOS namespace access";

  /// <summary>
  /// Opens the image as a random-access block device.
  /// </summary>
  public IRandomAccessBlockDevice OpenBlockDevice(Stream image, bool writable, bool leaveOpen = true)
    => new D64BlockDevice(image, writable, leaveOpen);

  /// <summary>
  /// Probes the image and reports the filesystem driver profile.
  /// </summary>
  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var limitations = new List<string> {
      "CBM DOS 2.6 is a flat root namespace; subdirectories, hard links, symlinks and transactions are unavailable.",
      "Node ids are stable for the lifetime of one mounted session but are not persistent across remounts.",
      "Writable mounting supports ordinary closed SEQ/PRG/USR files; REL side-sector semantics are fail-closed.",
    };
    if (!image.CanRead || !image.CanSeek) {
      limitations.Add("Mounting requires a readable, seekable image stream.");
      return BuildProfile(false, false, limitations);
    }

    var saved = image.Position;
    try {
      if (image.Length < D64BlockDevice.DataLength) {
        limitations.Add($"Image is {image.Length} bytes; a 35-track D64 needs at least {D64BlockDevice.DataLength} bytes.");
        return BuildProfile(false, false, limitations);
      }
      image.Position = 0;
      var data = new byte[D64BlockDevice.DataLength];
      image.ReadExactly(data);
      var validation = D64MountValidator.Validate(data);
      limitations.AddRange(validation.Limitations);
      return BuildProfile(validation.CanRead, validation.CanWrite && image.CanWrite, limitations);
    } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException) {
      limitations.Add(ex.Message);
      return BuildProfile(false, false, limitations);
    } finally {
      image.Position = saved;
    }
  }

  /// <summary>
  /// Opens a filesystem session over the image.
  /// </summary>
  public IFilesystemSession OpenFilesystem(Stream image, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var profile = ProbeFilesystem(image);
    if (!profile.CanMount)
      throw new InvalidDataException("D64 cannot be mounted: " + string.Join(" ", profile.Limitations));
    if (!options.ReadOnly && !profile.CanMountWritable)
      throw new NotSupportedException("D64 is not safe for writable mounting: " + string.Join(" ", profile.Limitations));
    var device = new D64BlockDevice(image, writable: !options.ReadOnly, leaveOpen: options.LeaveOpen);
    return new D64FilesystemSession(device, profile, options.ReadOnly, ownsDevice: true);
  }

  private static FilesystemDriverProfile BuildProfile(bool canMount, bool canWrite, IReadOnlyList<string> limitations) {
    var capabilities = FilesystemDriverCapabilities.None;
    if (canMount)
      capabilities = FilesystemDriverCapabilities.EnumerateDirectories |
        FilesystemDriverCapabilities.ReadData |
        FilesystemDriverCapabilities.RandomAccess |
        FilesystemDriverCapabilities.StableNodeIds |
        FilesystemDriverCapabilities.Flush;
    if (canWrite)
      capabilities |= FilesystemDriverCapabilities.WriteData |
        FilesystemDriverCapabilities.Truncate |
        FilesystemDriverCapabilities.CreateFile |
        FilesystemDriverCapabilities.DeleteFile |
        FilesystemDriverCapabilities.Rename;
    return new FilesystemDriverProfile(
      "D64",
      "CBM DOS 2.6 / 1541",
      capabilities,
      canWrite ? FilesystemMutationModel.Direct : FilesystemMutationModel.None,
      canMount,
      canWrite,
      limitations.Distinct().ToArray());
  }

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new D64Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new D64Reader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single filesystem entry as a bounded read-only stream. The
  /// reader produces the decoded file bytes by walking the entry's extent
  /// or block chain; the matched bytes are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's logical length so cluster/extent slack past the entry's
  /// end is physically unreachable through this view.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new D64Reader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new D64Writer();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name.Length > 16 ? name[..16] : name, data);

    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (string.IsNullOrEmpty(label)) label = "DISK";
    var diskId = options?.GetOption("DiskId", "00") ?? "00";
    output.Write(w.Build(label, diskId));
  }

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new D64BlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new D64BlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware D64 defragmentor. Tries the planner-driven in-place path first
  /// (using the planner + <see cref="D64BlockMover"/>), falling back to the
  /// rebuild path on error or for <see cref="DefragMode.CarveHole"/>.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      try {
        DefragmentWithPlanner(archive, options);
        return;
      } catch (Exception planFailure) {
        options.OnProgress?.Invoke(new DefragProgressEvent(
          "fallback", 0, -1, -1, archive.Length, null,
          $"In-place planning declined ({planFailure.GetType().Name}: " +
          $"{FirstLine(planFailure.Message)}); rebuilding instead"));
        archive.Position = 0;
      }
    }
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new D64Reader(stream);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new D64Writer();
        foreach (var (n, d) in files)
          w.AddFile(n.Length > 16 ? n[..16] : n, d);
        return w.Build();
      });
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var imageSize = archive.Length;
    using var snap = new MemoryStream();
    archive.CopyTo(snap);
    var imageData = snap.ToArray();
    var extents = D64ExtentMap.Enumerate(new MemoryStream(imageData)).ToList();
    var mover = new D64BlockMover();
    var moves = DefragPlanner.Plan(extents, 0, imageSize, 256, options.Profile, options.Mode,
      holeSize: options.HoleSize, holeAt: options.HoleAt);
    if (moves.Count == 0) return;
    DefragPlannerExecutor.Execute(archive, options, mover, moves, imageSize);
  }

  /// <summary>The first line of a message, for a one-line progress note.</summary>
  private static string FirstLine(string message) {
    var end = message.IndexOf('\n');
    return end < 0 ? message : message[..end].TrimEnd('\r');
  }
}
