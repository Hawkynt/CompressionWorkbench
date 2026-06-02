#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Hpfs;

/// <summary>
/// R/W descriptor for OS/2 HPFS (High Performance File System) volumes.
/// Supports: list, extract, create (WORM), modify (rebuild-based), defragment, extent map.
/// </summary>
public sealed class HpfsFormatDescriptor
    : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
      IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty {

  public string Id => "Hpfs";
  public string DisplayName => "HPFS";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;

  public string DefaultExtension => ".img";
  public IReadOnlyList<string> Extensions => [".img", ".hpfs"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // Superblock magic at LBA 16 (offset 8192). First 4 bytes are sufficient for detection.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xF9, 0x95, 0xE8, 0xF9], Offset: 8192, Confidence: 0.85),
  ];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "OS/2 High Performance File System — read/write with direct-allocation layout.";

  // ── IArchiveFormatOperations (List / Extract) ─────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new HpfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new HpfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  // ── IArchiveCreatable ─────────────────────────────────────────────────

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
    var r = new HpfsReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new HpfsWriter();
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }

  // ── IArchiveModifiable (rebuild-based) ────────────────────────────────

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => ModifyRebuilder.Add(archive, inputs, ReadFileEntries, BuildImage);

  public void Remove(Stream archive, string[] entryNames)
    => ModifyRebuilder.Remove(archive, entryNames, ReadFileEntries, BuildImage);

  // ── IArchiveDefragmentable ────────────────────────────────────────────

  public void Defragment(Stream archive)
    => Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options, ReadFileEntries, BuildImage);

  // ── IFilesystemExtentMap ──────────────────────────────────────────────

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    byte[] data;
    try {
      image.Position = 0;
      using var ms = new MemoryStream();
      image.CopyTo(ms);
      data = ms.ToArray();
    } catch {
      return [];
    }

    return EnumerateExtentsCore(data);
  }

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in the HPFS image: free sectors, gaps between files
  /// and the sector-tip slack between a file's logical size and the end of its
  /// last allocated 512-byte sector. The extent map clamps each file's data run
  /// to its logical byte length, so trailing slack inside the final sector
  /// presents as a free gap that the generic <see cref="UnusedSpaceWiper"/>
  /// zero-fills. The size lookup is keyed by the reader's full path, matching
  /// the extent FileName.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var reader = new HpfsReader(image);
        var sizeMap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries)
          if (!entry.IsDirectory && !entry.IsBtreeFile)
            sizeMap[entry.Name] = entry.Size;
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    var extents = EnumerateExtents(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  private static List<DefragBlockInfo> EnumerateExtentsCore(byte[] data) {
    var result = new List<DefragBlockInfo>();
    const int lbaSize = HpfsReader.LbaSize;

    if (data.Length < (HpfsReader.SuperblockLba + 1) * lbaSize) return result;

    // Boot sector
    result.Add(new DefragBlockInfo(0, lbaSize, DefragBlockKind.MetadataReserved, "Boot sector"));

    // Superblock at LBA 16
    var sbOff = HpfsReader.SuperblockLba * lbaSize;
    result.Add(new DefragBlockInfo(sbOff, lbaSize, DefragBlockKind.MetadataReserved, "Superblock"));

    // Spare block at LBA 17
    result.Add(new DefragBlockInfo(17 * lbaSize, lbaSize, DefragBlockKind.MetadataReserved, "SpareBlock"));

    // Root fnode
    try {
      var rootFnodeLba = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sbOff + 12, 4));
      var rootFnodeOff = (long)rootFnodeLba * lbaSize;
      if (rootFnodeOff + lbaSize <= data.Length) {
        result.Add(new DefragBlockInfo(rootFnodeOff, lbaSize, DefragBlockKind.MetadataReserved, "Root Fnode"));

        // Root dir block
        if (rootFnodeOff + 0xC4 + 12 <= data.Length) {
          var rootDirLba = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)rootFnodeOff + 0xC4 + 8, 4));
          var rootDirOff = (long)rootDirLba * lbaSize;
          if (rootDirOff + HpfsReader.DirBlockSize <= data.Length)
            result.Add(new DefragBlockInfo(rootDirOff, HpfsReader.DirBlockSize, DefragBlockKind.MetadataReserved, "Root DirBlock"));
        }
      }
    } catch {
      // Malformed — return what we have
    }

    // File extents from reader
    try {
      using var ms = new MemoryStream(data);
      using var r = new HpfsReader(ms);
      foreach (var entry in r.Entries) {
        if (entry.IsDirectory || entry.IsBtreeFile) continue;
        if (entry.FnodeLba > 0) {
          var fnodeOff = (long)entry.FnodeLba * lbaSize;
          if (fnodeOff + lbaSize <= data.Length)
            result.Add(new DefragBlockInfo(fnodeOff, lbaSize, DefragBlockKind.MetadataReserved, $"Fnode: {entry.Name}"));
        }
        if (entry.DataLba > 0 && entry.Size > 0) {
          var dataOff = (long)entry.DataLba * lbaSize;
          var dataLen = ((entry.Size + lbaSize - 1) / lbaSize) * lbaSize;
          if (dataOff + dataLen <= data.Length)
            result.Add(new DefragBlockInfo(dataOff, Math.Min(dataLen, entry.Size), DefragBlockKind.Used, entry.Name));
        }
      }
    } catch {
      // Best-effort
    }

    // Bitmap
    if (data.Length >= 25 * lbaSize)
      result.Add(new DefragBlockInfo(24 * lbaSize, lbaSize, DefragBlockKind.MetadataReserved, "Bitmap"));

    return result;
  }

  // ── Shared helpers ────────────────────────────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadFileEntries(Stream stream) {
    using var r = new HpfsReader(stream);
    return r.Entries
      .Where(e => !e.IsDirectory && !e.IsBtreeFile)
      .Select(e => (e.Name, r.Extract(e)))
      .ToList(); // materialise before reader is disposed
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new HpfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }
}
