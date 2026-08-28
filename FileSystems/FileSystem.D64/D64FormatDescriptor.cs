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

  public long? MaxTotalArchiveSize => 174848;
  public string AcceptedInputsDescription =>
    "Commodore 1541 D64 disk; any file up to 174 848 bytes total (664 data sectors × 254 bytes).";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    reason = null;
    return true;
  }

  public IReadOnlyList<long> CanonicalSizes => [174848];
  public void Shrink(Stream input, Stream output) =>
    ArchiveShrinker.ShrinkViaRebuild(input, output, this, this, this.CanonicalSizes);

  public string Id => "D64";
  public string DisplayName => "D64";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      var truncatedName = name.Length > 16 ? name[..16] : name;
      D64Modifier.RemoveFile(archive, truncatedName, wipeData: true);
      D64Modifier.AddFile(archive, truncatedName, data);
    }
  }

  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames) {
      var truncatedName = name.Length > 16 ? name[..16] : name;
      D64Modifier.RemoveFile(archive, truncatedName, wipeData: true);
    }
  }

  public string DefaultExtension => ".d64";
  public IReadOnlyList<string> Extensions => [".d64"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Commodore 64 1541 disk image with mount-grade sector and CBM DOS namespace access";

  public IRandomAccessBlockDevice OpenBlockDevice(Stream image, bool writable, bool leaveOpen = true)
    => new D64BlockDevice(image, writable, leaveOpen);

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

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new D64Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new D64Reader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

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

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new D64Writer();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name.Length > 16 ? name[..16] : name, data);

    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (string.IsNullOrEmpty(label)) label = "DISK";
    var diskId = options?.GetOption("DiskId", "00") ?? "00";
    output.Write(w.Build(label, diskId));
  }

  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false)
    => new D64BlockMover().MoveExtent(image, srcOffset, dstOffset, length, zeroSource);

  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length)
    => new D64BlockMover().UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

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

  private static string FirstLine(string message) {
    var end = message.IndexOf('\n');
    return end < 0 ? message : message[..end].TrimEnd('\r');
  }
}
