using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Hammer2;

/// <summary>
/// Walks a HAMMER2 (DragonFly BSD) filesystem image and extracts the regular
/// files living in its PFS roots. The walk mirrors the kernel's on-disk topology
/// (<c>sys/vfs/hammer2/hammer2_disk.h</c>):
///
/// <list type="bullet">
///   <item><description>pick the volume header (one of four 64 KB slots) with the
///   highest valid <c>mirror_tid</c>;</description></item>
///   <item><description>follow <c>sroot_blockset[0]</c> to the super-root inode,
///   then each PFS-MASTER inode it references;</description></item>
///   <item><description>walk each PFS root's blockset — descending through
///   <c>HAMMER2_BREF_TYPE_INDIRECT</c> blocks — to gather the
///   <c>HAMMER2_BREF_TYPE_DIRENT</c> entries (names + child inode numbers) and
///   the child <c>HAMMER2_BREF_TYPE_INODE</c> entries (keyed by inode number);
///   </description></item>
///   <item><description>read each regular file's bytes from the inode's embedded
///   direct data (<c>HAMMER2_OPFLAG_DIRECTDATA</c>) or its
///   <c>HAMMER2_BREF_TYPE_DATA</c> blocks.</description></item>
/// </list>
///
/// <para>Directories are recursed so nested files surface under a
/// <c>parent/child</c> path. Data stored with a compression method other than
/// <c>HAMMER2_COMP_NONE</c> (e.g. the kernel's default LZ4) is surfaced raw and
/// flagged via <see cref="HasCompressedData"/> — decompression is out of scope.
/// </para>
/// </summary>
public sealed class Hammer2Reader {
  private const ulong VolumeIdHbo = 0x48414d3205172011UL;
  private const ulong VolumeIdAbo = 0x11201705324d4148UL;
  private const int VolumeBytes = 65536;
  private const int NumVolhdrs = 4;
  private const int BlockrefBytes = 128;
  private const int SetCount = 4;
  private const int InodeBytes = 1024;

  private const byte BrefTypeInode = 1;
  private const byte BrefTypeIndirect = 2;
  private const byte BrefTypeData = 3;
  private const byte BrefTypeDirent = 4;

  private const byte ObjTypeDirectory = 1;
  private const byte ObjTypeRegfile = 2;
  private const byte OpflagDirectData = 0x01;

  private readonly byte[] _image;

  /// <summary>Creates a reader over the full HAMMER2 image bytes.</summary>
  public Hammer2Reader(byte[] image) {
    ArgumentNullException.ThrowIfNull(image);
    this._image = image;
  }

  /// <summary>True iff a valid HAMMER2 volume header was found.</summary>
  public bool Valid { get; private set; }

  /// <summary>True iff any extracted file's data used a compression method other
  /// than <c>HAMMER2_COMP_NONE</c> (surfaced raw).</summary>
  public bool HasCompressedData { get; private set; }

  /// <summary>
  /// Reads every regular file in every PFS root, keyed by its path
  /// (<c>name</c> at the root, <c>dir/name</c> for nested files). Never throws —
  /// returns whatever it could parse.
  /// </summary>
  public Dictionary<string, byte[]> ReadAllFiles() {
    var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    try {
      var vh = this.SelectVolumeHeader();
      if (vh < 0)
        return result;
      this.Valid = true;

      // sroot_blockset[0] -> super-root inode.
      var srootBref = this._image.AsSpan(vh + 0x200, BlockrefBytes);
      if (srootBref[0] != BrefTypeInode)
        return result;
      var srootInodeOff = DecodeOffset(ReadI64(srootBref, 32));
      var sroot = this.ReadBlock(srootInodeOff, InodeBytes);
      if (sroot == null)
        return result;

      // The super-root's blockset references the PFS-MASTER inodes.
      foreach (var pfsBref in this.EnumerateBlockset(sroot, 0x200, SetCount * BlockrefBytes)) {
        if (pfsBref.Type != BrefTypeInode)
          continue;
        var pfsInode = this.ReadBlock(DecodeOffset(pfsBref.DataOff), InodeBytes);
        if (pfsInode == null)
          continue;
        // Skip the always-present "LOCAL" PFS to avoid duplicate/empty surfaces;
        // include any labelled PFS that actually holds files.
        var label = ReadInodeName(pfsInode);
        if (string.Equals(label, "LOCAL", StringComparison.Ordinal))
          continue;
        this.WalkDirectory(pfsInode, prefix: "", result);
      }
    } catch {
      // Best-effort: return whatever was gathered.
    }
    return result;
  }

  // ---- directory walk ---------------------------------------------------------
  private void WalkDirectory(byte[] dirInode, string prefix, Dictionary<string, byte[]> result) {
    // Gather the directory's children: a map of inum -> inode, plus the dirents
    // (name + inum) that give them human names.
    var inodesByInum = new Dictionary<ulong, byte[]>();
    var dirents = new List<(string Name, ulong Inum, byte Type)>();

    foreach (var bref in this.EnumerateBlockset(dirInode, 0x200, SetCount * BlockrefBytes)) {
      switch (bref.Type) {
        case BrefTypeInode: {
          var child = this.ReadBlock(DecodeOffset(bref.DataOff), InodeBytes);
          if (child != null) {
            var inum = ReadU64(child, 0x58);
            inodesByInum[inum] = child;
          }
          break;
        }
        case BrefTypeDirent: {
          var (name, inum, type) = ReadDirent(bref);
          if (name.Length > 0)
            dirents.Add((name, inum, type));
          break;
        }
      }
    }

    foreach (var (name, inum, type) in dirents) {
      if (!inodesByInum.TryGetValue(inum, out var inode))
        continue;
      var path = prefix.Length == 0 ? name : prefix + "/" + name;
      var objType = inode[0x50];
      if (objType == ObjTypeDirectory)
        this.WalkDirectory(inode, path, result);
      else if (objType == ObjTypeRegfile)
        result[path] = this.ReadFileData(inode);
    }
  }

  // ---- file data --------------------------------------------------------------
  private byte[] ReadFileData(byte[] inode) {
    var size = ReadI64(inode, 0x60);
    if (size <= 0)
      return [];

    var opFlags = inode[0x51];
    if ((opFlags & OpflagDirectData) != 0) {
      // Embedded direct data in the inode union @0x200.
      var n = (int)Math.Min(size, InodeBytes - 0x200);
      return inode.AsSpan(0x200, n).ToArray();
    }

    // Otherwise the blockset holds DATA (or INDIRECT->DATA) blockrefs, each
    // covering a logical-offset range. Reassemble by key (logical offset).
    var data = new byte[size];
    foreach (var bref in this.EnumerateBlockset(inode, 0x200, SetCount * BlockrefBytes)) {
      if (bref.Type != BrefTypeData)
        continue;
      if ((bref.CompAlgo & 15) != 0)
        this.HasCompressedData = true;
      var blockSize = 1L << RadixOf(bref.DataOff);
      var block = this.ReadBlock(DecodeOffset(bref.DataOff), (int)blockSize);
      if (block == null)
        continue;
      var logical = (long)bref.Key;
      if (logical >= size)
        continue;
      var copy = (int)Math.Min(blockSize, size - logical);
      block.AsSpan(0, copy).CopyTo(data.AsSpan((int)logical, copy));
    }
    return data;
  }

  // ---- blockset / indirect enumeration ---------------------------------------
  // A leaf blockref carries its full 128 raw bytes so dirent fields (which
  // overlay the bref) can be decoded by the caller.
  private readonly record struct Bref(byte Type, int CompAlgo, ulong Key, long DataOff, byte[] Raw);

  // Yields every leaf blockref reachable from a blockset, transparently
  // descending through HAMMER2_BREF_TYPE_INDIRECT blocks. Recursion is bounded
  // to avoid pathological self-referential images.
  private IEnumerable<Bref> EnumerateBlockset(byte[] buffer, int offset, int length, int depth = 0) {
    if (depth > 16)
      yield break;
    var n = length / BlockrefBytes;
    for (var i = 0; i < n; ++i) {
      var pos = offset + i * BlockrefBytes;
      var type = buffer[pos];
      if (type == 0)
        continue;
      var compAlgo = buffer[pos + 1] & 15;
      var key = ReadU64(buffer, pos + 8);
      var dataOff = ReadI64(buffer, pos + 32);

      if (type != BrefTypeIndirect) {
        yield return new Bref(type, compAlgo, key, dataOff, buffer.AsSpan(pos, BlockrefBytes).ToArray());
        continue;
      }

      var blockSize = 1 << RadixOf(dataOff);
      var block = this.ReadBlock(DecodeOffset(dataOff), blockSize);
      if (block == null)
        continue;
      foreach (var b in this.EnumerateBlockset(block, 0, block.Length, depth + 1))
        yield return b;
    }
  }

  // ---- dirent decode ----------------------------------------------------------
  // A HAMMER2_BREF_TYPE_DIRENT overlays the bref: inum @+0x30, namlen @+0x38,
  // type @+0x3A, name inline @+0x40 when it fits in 64 bytes (data_off == 0),
  // otherwise the name lives in the referenced data block.
  private (string Name, ulong Inum, byte Type) ReadDirent(Bref bref) {
    var raw = bref.Raw;
    var inum = ReadU64(raw, 0x30);
    var namLen = ReadU16(raw, 0x38);
    var type = raw[0x3A];
    if (namLen == 0)
      return ("", 0, 0);

    byte[] nameBytes;
    if (bref.DataOff != 0 && namLen > 64) {
      // Long name stored in a referenced data block.
      var blockSize = Math.Max(1 << RadixOf(bref.DataOff), namLen);
      var block = this.ReadBlock(DecodeOffset(bref.DataOff), blockSize);
      if (block == null)
        return ("", 0, 0);
      nameBytes = block.AsSpan(0, namLen).ToArray();
    } else {
      nameBytes = raw.AsSpan(0x40, Math.Min((int)namLen, 64)).ToArray();
    }
    return (Encoding.ASCII.GetString(nameBytes), inum, type);
  }

  // ---- raw block / inode reads ------------------------------------------------
  private byte[]? ReadBlock(long deviceOffset, int size) {
    if (deviceOffset < 0 || deviceOffset + size > this._image.LongLength)
      return null;
    return this._image.AsSpan((int)deviceOffset, size).ToArray();
  }

  private int SelectVolumeHeader() {
    var best = -1;
    ulong bestTid = 0;
    for (var slot = 0; slot < NumVolhdrs; ++slot) {
      var off = slot * VolumeBytes;
      if (off + VolumeBytes > this._image.LongLength)
        break;
      var magic = ReadU64(this._image, off);
      if (magic != VolumeIdHbo && magic != VolumeIdAbo)
        continue;
      var mirrorTid = ReadU64(this._image, off + 0x78);
      if (best < 0 || mirrorTid >= bestTid) {
        best = off;
        bestTid = mirrorTid;
      }
    }
    return best;
  }

  private static string ReadInodeName(byte[] inode) {
    var nameLen = ReadU16(inode, 0x80);
    if (nameLen == 0 || 0x100 + nameLen > inode.Length)
      return "";
    return Encoding.ASCII.GetString(inode, 0x100, Math.Min((int)nameLen, InodeBytes - 0x100));
  }

  // ---- primitives -------------------------------------------------------------
  private static long DecodeOffset(long dataOff) => dataOff & ~0x3FL;
  private static int RadixOf(long dataOff) => (int)(dataOff & 0x3F);

  private static ulong ReadU64(byte[] d, int off) => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(off, 8));
  private static ulong ReadU64(ReadOnlySpan<byte> d, int off) => BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(off, 8));
  private static long ReadI64(byte[] d, int off) => BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(off, 8));
  private static long ReadI64(ReadOnlySpan<byte> d, int off) => BinaryPrimitives.ReadInt64LittleEndian(d.Slice(off, 8));
  private static ushort ReadU16(byte[] d, int off) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(off, 2));
}
