#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Vmfs;

/// <summary>One file, directory or symlink of a VMFS volume.</summary>
public sealed record VmfsEntry(string Name, long Size, bool IsDirectory, bool IsSymlink, DateTime? LastModified, uint DescriptorId, uint Type);

/// <summary>
/// Reads single-extent VMFS3 volumes, following <c>docs/VMFS-ON-DISK.md</c>: volume info
/// and the logical address space, the file-system info, the bootstrap of the
/// file-descriptor bitmap, inodes, the FB/SB/PB block addressing and directory records.
/// VMFS5/6 and multi-extent volumes are refused: nothing public proves them.
/// </summary>
public sealed class VmfsReader : IDisposable {
  internal const long VolumeInfoOffset = 0x100000;
  internal const uint VolumeInfoMagic = 0xC001D00D;
  private const long LogicalBase = VolumeInfoOffset + 0x1000000;
  private const long FsInfoOffset = 0x200000;
  private const uint FsInfoMagic = 0x2FABF15E;
  private const uint InodeMagic = 0x10C00001;
  private const int InodeSize = 0x800;
  private const int BlockArrayOffset = 1024;
  private const int BlockArrayLength = 256;
  private const int DirentSize = 0x8C;
  private const int BitmapEntrySize = 0x400;
  private const long HeartbeatEnd = 0x300000 + 2048L * 0x200;
  private const uint TypeDirectory = 2, TypeFile = 3, TypeSymlink = 4, TypeSystem = 5, TypeRdm = 6;
  private const int MaxDepth = 64;

  private static readonly byte[] VmfsPartitionGuid = new Guid("AA31E02A-400F-11DB-9590-000C2911D1B8").ToByteArray();

  private readonly ImageAccessor _img;
  private readonly long _partition;
  private readonly List<VmfsEntry> _entries = [];
  private readonly HashSet<uint> _visited = [];
  private readonly BitmapFile _fdc;
  private readonly BitmapFile? _pbc;
  private readonly BitmapFile? _sbc;

  public long BlockSize { get; }
  public string Label { get; }
  public uint VolumeVersion { get; }
  public IReadOnlyList<VmfsEntry> Entries => _entries;

  private sealed record Inode(uint Id, uint Type, long Size, long BlockSize, uint Zla, uint[] Blocks, uint MTime, byte[] Raw);

  private sealed record BitmapFile(Inode File, uint ItemsPerEntry, uint EntriesPerArea, uint HeaderSize, uint ItemSize, uint AreaSize);

  public VmfsReader(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    _img = new ImageAccessor(stream, leaveOpen);
    _partition = FindPartition(_img);
    var vol = Read(_partition + VolumeInfoOffset, 0x400);
    if (BinaryPrimitives.ReadUInt32LittleEndian(vol) != VolumeInfoMagic)
      throw new InvalidDataException("VMFS: no volume info magic at 1 MiB into the partition.");
    VolumeVersion = BinaryPrimitives.ReadUInt32LittleEndian(vol.AsSpan(4));
    if (VolumeVersion != 3)
      throw new NotSupportedException($"VMFS: volume version {VolumeVersion} is not read; only VMFS3 is proven against a real volume.");
    var extents = BinaryPrimitives.ReadUInt32LittleEndian(vol.AsSpan(0x200 + 0x90));
    var firstSegment = BinaryPrimitives.ReadUInt32LittleEndian(vol.AsSpan(0x200 + 0x78));
    if (extents != 1 || firstSegment != 0)
      throw new NotSupportedException($"VMFS: a volume spread over {extents} extents is not read.");

    var fs = Logical(FsInfoOffset, 512);
    if (BinaryPrimitives.ReadUInt32LittleEndian(fs) != FsInfoMagic)
      throw new InvalidDataException("VMFS: no file-system info magic.");
    BlockSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(fs.AsSpan(161));
    if (BlockSize < 0x10000 || BlockSize > 0x10000000 || (BlockSize & (BlockSize - 1)) != 0)
      throw new InvalidDataException($"VMFS: implausible block size {BlockSize}.");
    var raw = fs.AsSpan(29, 128);
    var nul = raw.IndexOf((byte)0);
    Label = Encoding.UTF8.GetString(nul < 0 ? raw : raw[..nul]);

    var fdcBase = (uint)Math.Max(1, HeartbeatEnd / BlockSize);
    var bootstrapInode = new Inode(0, TypeSystem, BlockSize, BlockSize, 1, [FileBlock(fdcBase), .. new uint[BlockArrayLength - 1]], 0, []);
    var bootstrap = OpenBitmap(bootstrapInode);
    var root = ReadInode(bootstrap, FileDescriptor(0, 0));
    if (root.Type != TypeDirectory) throw new InvalidDataException("VMFS: FD(0,0) is not the root directory.");

    var rootRecords = Records(root);
    BitmapFile? Meta(string name) =>
      rootRecords.FirstOrDefault(r => r.Name == name) is { } r && r.Type == TypeSystem ? OpenBitmap(ReadInode(bootstrap, r.Id)) : null;
    _fdc = Meta(".fdc.sf") ?? throw new InvalidDataException("VMFS: the root directory names no .fdc.sf.");
    _pbc = Meta(".pbc.sf");
    _sbc = Meta(".sbc.sf");
    Walk(root, "", 0);
  }

  // ── addressing ─────────────────────────────────────────────────────

  private static long FindPartition(ImageAccessor img) {
    if (img.Length < 1024) throw new InvalidDataException("VMFS: image too small.");
    var mbr = img.Read(0, 512);
    if (BinaryPrimitives.ReadUInt16LittleEndian(mbr.AsSpan(510)) != 0xAA55) return 0;
    if (mbr[446 + 4] == 0xEE && img.Length >= 34 * 512) {
      var gpt = img.Read(512, 512);
      if (Encoding.ASCII.GetString(gpt, 0, 8) == "EFI PART") {
        var entriesLba = (long)BinaryPrimitives.ReadUInt64LittleEndian(gpt.AsSpan(72));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(gpt.AsSpan(80));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(gpt.AsSpan(84));
        for (var i = 0; i < Math.Min(count, 128u) && size >= 128; ++i) {
          var e = img.Read(entriesLba * 512 + i * size, 128);
          if (e.AsSpan(0, 16).SequenceEqual(VmfsPartitionGuid))
            return (long)BinaryPrimitives.ReadUInt64LittleEndian(e.AsSpan(32)) * 512;
        }
      }
    }
    for (var off = 446; off < 510; off += 16)
      if (mbr[off + 4] == 0xFB) return BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(off + 8)) * 512L;
    return 0;
  }

  private byte[] Read(long offset, int length) {
    if (offset < 0 || offset + length > _img.Length)
      throw new InvalidDataException($"VMFS: {length} bytes at {offset} lie outside the image.");
    return _img.Read(offset, length);
  }

  private byte[] Logical(long offset, int length) => Read(_partition + LogicalBase + offset, length);

  private static uint FileBlock(uint item) => (item << 6) | 1;
  private static uint FileDescriptor(uint entry, uint item) => (item << 22) | (entry << 6) | 4;

  // ── metadata ───────────────────────────────────────────────────────

  private BitmapFile OpenBitmap(Inode file) {
    var h = ReadFile(file, 0, 0x1C);
    uint U(int o) => BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(o));
    var b = new BitmapFile(file, U(0), U(4), U(8), U(0xC), U(0x10));
    if (b.ItemsPerEntry == 0 || b.EntriesPerArea == 0 || b.ItemSize == 0 || b.ItemSize > 0x100000)
      throw new InvalidDataException("VMFS: implausible bitmap file header.");
    return b;
  }

  private byte[] Item(BitmapFile b, uint entry, uint item) {
    var addr = (long)entry * b.ItemsPerEntry + item;
    var perArea = (long)b.EntriesPerArea * b.ItemsPerEntry;
    var area = addr / perArea;
    var pos = b.HeaderSize + area * b.AreaSize + (long)b.EntriesPerArea * BitmapEntrySize + addr % perArea * b.ItemSize;
    return ReadFile(b.File, pos, (int)b.ItemSize);
  }

  private Inode ReadInode(BitmapFile fdc, uint id) {
    if ((id & 7) != 4) throw new InvalidDataException($"VMFS: block ID 0x{id:X8} is not a file descriptor.");
    var raw = Item(fdc, (id >> 6) & 0xFFFF, id >> 22);
    if (raw.Length < InodeSize || BinaryPrimitives.ReadUInt32LittleEndian(raw) != InodeMagic)
      throw new InvalidDataException($"VMFS: file descriptor 0x{id:X8} has no inode magic.");
    var blocks = new uint[BlockArrayLength];
    for (var i = 0; i < BlockArrayLength; ++i) blocks[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(BlockArrayOffset + i * 4));
    return new Inode(
      BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(512)),
      BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(524)),
      (long)BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(532)),
      (long)BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(540)),
      BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(580)),
      blocks,
      BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(556)),
      raw);
  }

  // ── file contents ──────────────────────────────────────────────────

  private uint BlockFor(Inode inode, long pos) {
    if (inode.BlockSize <= 0) throw new InvalidDataException("VMFS: inode with no block size.");
    var index = pos / inode.BlockSize;
    switch (inode.Zla) {
      case 1 or 2:
        if (index >= BlockArrayLength) throw new InvalidDataException("VMFS: file offset past the inode's block array.");
        return inode.Blocks[index];
      case 3: {
        var pbc = _pbc ?? throw new InvalidDataException("VMFS: pointer-block file needed but absent.");
        var perPointerBlock = pbc.ItemSize / 4;
        var pbIndex = index / perPointerBlock;
        if (pbIndex >= BlockArrayLength) throw new InvalidDataException("VMFS: file offset past the pointer blocks.");
        var pb = inode.Blocks[pbIndex];
        if (pb == 0) return 0;
        if ((pb & 7) != 3) throw new InvalidDataException($"VMFS: 0x{pb:X8} is not a pointer block.");
        var table = Item(pbc, (pb >> 6) & 0x3FFFFF, pb >> 28);
        return BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan((int)(index % perPointerBlock) * 4));
      }
      default:
        throw new NotSupportedException($"VMFS: block addressing mode (ZLA) {inode.Zla} is not read.");
    }
  }

  private byte[] ReadFile(Inode inode, long pos, int length) {
    var result = new byte[length];
    using var ms = new MemoryStream(result);
    CopyFile(inode, pos, length, ms);
    return result;
  }

  private void CopyFile(Inode inode, long pos, long length, Stream destination) {
    while (length > 0) {
      var id = BlockFor(inode, pos);
      var type = id & 7;
      if (type == 1 && ((id >> 3) & 7 & 4) != 0) type = 0; // to be zeroed
      long n;
      switch (type) {
        case 0:
          n = Math.Min(length, inode.BlockSize - pos % inode.BlockSize);
          WriteZeros(destination, n);
          break;
        case 1: {
          var within = pos % BlockSize;
          n = Math.Min(length, BlockSize - within);
          var at = _partition + LogicalBase + (long)(id >> 6) * BlockSize + within;
          if (at < 0 || at + n > _img.Length) throw new InvalidDataException($"VMFS: file block 0x{id:X8} lies outside the image.");
          _img.CopyTo(at, destination, n);
          break;
        }
        case 2: {
          var sbc = _sbc ?? throw new InvalidDataException("VMFS: sub-block file needed but absent.");
          var item = ((id >> 28) & 0xF) | (((id >> 3) & 3) << 4);
          var data = Item(sbc, (id >> 6) & 0x3FFFFF, item);
          var within = (int)(pos % sbc.ItemSize);
          n = Math.Min(length, sbc.ItemSize - within);
          destination.Write(data, within, (int)n);
          break;
        }
        default:
          throw new InvalidDataException($"VMFS: block 0x{id:X8} of unexpected type {type}.");
      }
      pos += n;
      length -= n;
    }
  }

  private static void WriteZeros(Stream destination, long count) {
    var zeros = new byte[(int)Math.Min(count, 1 << 20)];
    while (count > 0) {
      var n = (int)Math.Min(count, zeros.Length);
      destination.Write(zeros, 0, n);
      count -= n;
    }
  }

  // ── directories ────────────────────────────────────────────────────

  private sealed record Record(uint Type, uint Id, string Name);

  private List<Record> Records(Inode dir) {
    var data = ReadFile(dir, 0, (int)Math.Min(dir.Size, 64L * 1024 * 1024));
    var list = new List<Record>();
    for (var p = 0; p + DirentSize <= data.Length; p += DirentSize) {
      var type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p));
      var id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 4));
      var raw = data.AsSpan(p + 12, 128);
      var nul = raw.IndexOf((byte)0);
      var name = Encoding.UTF8.GetString(nul < 0 ? raw : raw[..nul]);
      if (type == 0 || name.Length == 0) continue;
      list.Add(new Record(type, id, name));
    }
    return list;
  }

  private void Walk(Inode dir, string basePath, int depth) {
    if (depth > MaxDepth || !_visited.Add(dir.Id)) return;
    List<Record> records;
    try { records = Records(dir); } catch (InvalidDataException) { return; }
    foreach (var r in records) {
      if (r.Name is "." or ".." || r.Name.Contains('/')) continue;
      Inode inode;
      try { inode = ReadInode(_fdc, r.Id); } catch (InvalidDataException) { continue; }
      var path = basePath.Length == 0 ? r.Name : basePath + "/" + r.Name;
      var isDir = inode.Type == TypeDirectory;
      _entries.Add(new VmfsEntry(path, isDir ? 0 : inode.Size, isDir, inode.Type == TypeSymlink,
        inode.MTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(inode.MTime).UtcDateTime : null, r.Id, inode.Type));
      if (isDir) Walk(inode, path, depth + 1);
    }
  }

  /// <summary>Writes the contents of <paramref name="entry"/> (a symlink's target) to <paramref name="destination"/>.</summary>
  public void ExtractTo(VmfsEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory) return;
    var inode = ReadInode(_fdc, entry.DescriptorId);
    if (inode.Type == TypeRdm) throw new NotSupportedException($"VMFS: '{entry.Name}' is a raw device mapping; its data lives on another LUN.");
    CopyFile(inode, 0, inode.Size, destination);
  }

  public byte[] Extract(VmfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size > Array.MaxLength) throw new IOException($"VMFS: '{entry.Name}' is too large for an array; use ExtractTo.");
    using var ms = new MemoryStream();
    ExtractTo(entry, ms);
    return ms.ToArray();
  }

  public void Dispose() => _img.Dispose();
}
