#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;
using static FileSystem.UnixV7.UnixV7Layout;

namespace FileSystem.UnixV7;

/// <summary>One file or directory of a V7 volume.</summary>
public sealed record UnixV7Entry(string Name, long Size, bool IsDirectory, DateTime? LastModified, int Inode, ushort Mode);

/// <summary>
/// Reads Seventh Edition UNIX volumes. The superblock carries no magic number, so
/// a volume is recognised the way Linux' <c>v7</c> driver recognises it: the
/// free-list and inode caches within their bounds, an i-list inside the volume,
/// and inode 2 a directory whose size is a whole number of entries.
/// </summary>
public sealed class UnixV7Reader : IDisposable {
  private const int MaxDirectoryDepth = 128;

  private readonly ImageAccessor _img;
  private readonly List<UnixV7Entry> _entries = [];
  private readonly HashSet<int> _visited = [];
  private readonly int _inodeCount;
  private readonly int _volumeBlocks;

  /// <summary>Gets the files and directories, in directory order.</summary>
  public IReadOnlyList<UnixV7Entry> Entries => _entries;

  /// <summary>Size of the volume in blocks (<c>s_fsize</c>).</summary>
  public int VolumeBlocks => _volumeBlocks;

  /// <summary>First data block (<c>s_isize</c>).</summary>
  public int FirstDataBlock { get; }

  /// <summary>Free blocks the superblock reports (<c>s_tfree</c>).</summary>
  public int FreeBlocks { get; }

  public UnixV7Reader(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    _img = new ImageAccessor(stream, leaveOpen);
    var sb = _img.Read(SuperblockBlock * BlockSize, BlockSize);
    if (Check(sb, _img.Length, _img.Read(InodeOffset(RootInode), InodeSize)) is { } problem)
      throw new InvalidDataException("UNIX V7: " + problem);
    FirstDataBlock = BinaryPrimitives.ReadUInt16LittleEndian(sb);
    _volumeBlocks = PdpInt32(sb.AsSpan(SFsize));
    FreeBlocks = PdpInt32(sb.AsSpan(STfree));
    _inodeCount = (FirstDataBlock - 2) * InodesPerBlock;
    ReadDirectory(RootInode, "", 0);
  }

  /// <summary>
  /// Returns why <paramref name="superblock"/> and <paramref name="rootInode"/> do not
  /// describe a V7 volume of <paramref name="imageLength"/> bytes, or null when they do.
  /// </summary>
  internal static string? Check(ReadOnlySpan<byte> superblock, long imageLength, ReadOnlySpan<byte> rootInode) {
    if (superblock.Length < BlockSize || rootInode.Length < InodeSize) return "image too small for a superblock and an i-list";
    var isize = BinaryPrimitives.ReadUInt16LittleEndian(superblock);
    var fsize = PdpInt32(superblock[SFsize..]);
    var nfree = BinaryPrimitives.ReadInt16LittleEndian(superblock[SNfree..]);
    var ninode = BinaryPrimitives.ReadInt16LittleEndian(superblock[SNinode..]);
    if (nfree is < 0 or > NicFree) return $"s_nfree {nfree} outside 0..{NicFree}";
    if (ninode is < 0 or > NicInode) return $"s_ninode {ninode} outside 0..{NicInode}";
    if (fsize <= 0 || fsize > MaxVolumeBlocks) return $"s_fsize {fsize} implausible";
    if (isize < 3 || isize >= fsize) return $"s_isize {isize} not inside the volume";
    if ((long)isize * BlockSize > imageLength) return "the i-list runs past the end of the image";
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(rootInode);
    var size = PdpInt32(rootInode[DiSize..]);
    if ((mode & ~0x1FF) != IfDir) return "inode 2 is not a directory";
    if (size <= 0 || (size & (DirectEntrySize - 1)) != 0) return "the root directory size is not a whole number of entries";
    return null;
  }

  private void ReadDirectory(int ino, string basePath, int depth) {
    if (depth > MaxDirectoryDepth || !_visited.Add(ino)) return;
    byte[] data;
    try {
      data = ReadData(ino);
    } catch (InvalidDataException) {
      return; // a directory with blocks outside the image lists as empty
    }
    for (var pos = 0; pos + DirectEntrySize <= data.Length; pos += DirectEntrySize) {
      var child = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
      if (child == 0 || child > _inodeCount) continue;
      var raw = data.AsSpan(pos + 2, MaxNameLength);
      var nul = raw.IndexOf((byte)0);
      var name = Encoding.Latin1.GetString(nul < 0 ? raw : raw[..nul]);
      if (name.Length == 0 || name is "." or ".." || name.Contains('/')) continue;

      var inode = _img.Read(InodeOffset(child), InodeSize);
      var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
      var type = mode & IfMt;
      // Character/block devices and multiplexed files carry no data stream.
      if (type is not (IfDir or IfReg)) continue;
      var size = (uint)PdpInt32(inode.AsSpan(DiSize));
      var mtime = PdpInt32(inode.AsSpan(DiMtime));
      var path = basePath.Length == 0 ? name : basePath + "/" + name;
      var isDir = type == IfDir;
      _entries.Add(new UnixV7Entry(path, isDir ? 0 : size, isDir,
        mtime > 0 ? DateTimeOffset.FromUnixTimeSeconds(mtime).UtcDateTime : null, child, mode));
      if (isDir) ReadDirectory(child, path, depth + 1);
    }
  }

  /// <summary>Returns the contents of <paramref name="entry"/>.</summary>
  public byte[] Extract(UnixV7Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.IsDirectory ? [] : ReadData(entry.Inode);
  }

  private byte[] ReadData(int ino) {
    var inode = _img.Read(InodeOffset(ino), InodeSize);
    var size = (uint)PdpInt32(inode.AsSpan(DiSize));
    if (size > int.MaxValue) throw new InvalidDataException($"UNIX V7: inode {ino} declares {size} bytes.");
    var result = new byte[size];
    var blocks = (int)((size + BlockSize - 1) / BlockSize);
    var addr = inode.AsSpan(DiAddr);
    for (var i = 0; i < blocks; ++i) {
      var block = Resolve(addr, i);
      var count = (int)Math.Min(BlockSize, size - (long)i * BlockSize);
      if (block == 0) continue; // a hole reads as zeros
      CopyBlock(block, result.AsSpan(i * BlockSize, count));
    }
    return result;
  }

  // Maps logical block i to a physical block through the 10 direct and three indirect addresses.
  private int Resolve(ReadOnlySpan<byte> addr, int i) {
    const int n = AddressesPerBlock;
    if (i < DirectAddresses) return Address3(addr[(i * 3)..]);
    i -= DirectAddresses;
    if (i < n) return Walk(Address3(addr[30..]), i);
    i -= n;
    if (i < n * n) return Walk(Walk(Address3(addr[33..]), i / n), i % n);
    i -= n * n;
    return Walk(Walk(Walk(Address3(addr[36..]), i / (n * n)), i / n % n), i % n);
  }

  // One step down an indirect tree; a zero pointer is a hole all the way down.
  private int Walk(int block, int slot) => block == 0 ? 0 : Pointer(block, slot);

  private int Pointer(int block, int slot) {
    if (block < 0 || block >= _volumeBlocks || ((long)block + 1) * BlockSize > _img.Length)
      throw new InvalidDataException($"UNIX V7: indirect block {block} lies outside the volume.");
    return PdpInt32(_img.Read((long)block * BlockSize + slot * 4, 4));
  }

  private void CopyBlock(int block, Span<byte> destination) {
    if (block < 0 || block >= _volumeBlocks || (long)block * BlockSize + destination.Length > _img.Length)
      throw new InvalidDataException($"UNIX V7: data block {block} lies outside the volume.");
    _img.Read((long)block * BlockSize, destination);
  }

  public void Dispose() => _img.Dispose();
}
