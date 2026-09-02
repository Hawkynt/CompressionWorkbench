#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.VxFs;

/// <summary>
/// Walks a VxFS volume all the way to the files, and says which blocks each one
/// owns and where the inode that claims them sits.
/// </summary>
/// <remarks>
/// <para>Nothing here is found by scanning. The superblock names an object
/// location table; the table names the block a raw inode array starts at and
/// the inode inside it describing the fileset-header file; that file's first two
/// blocks name the inode lists, structural and primary; the primary list is a
/// file whose bytes are inodes at a stride of 256, and inode 2 of it is the root
/// directory. Every file is an entry in that directory.</para>
///
/// <para>The last hop is what a layout pass needs: a file's blocks are named by
/// up to ten direct extents inside its own inode, at a byte offset this records.
/// Moving a file's bytes is rewriting those extents and nothing else.</para>
/// </remarks>
public sealed class VxFsVolume {

  /// <summary>A run of blocks a file owns.</summary>
  /// <param name="Block">The first block.</param>
  /// <param name="Count">How many.</param>
  public readonly record struct Extent(long Block, long Count);

  /// <summary>A file in the root directory.</summary>
  /// <param name="Name">Its name.</param>
  /// <param name="Inode">Its number in the primary inode list.</param>
  /// <param name="InodeOffset">Where that inode sits in the image.</param>
  /// <param name="Size">How many bytes it holds.</param>
  /// <param name="Extents">The blocks it owns, in order.</param>
  public sealed record VolumeFile(
    string Name, uint Inode, long InodeOffset, long Size, IReadOnlyList<Extent> Extents);

  private readonly byte[] _image;

  /// <summary>Whether the whole walk landed.</summary>
  public bool Valid { get; private set; }

  /// <summary>Why it did not, when it did not.</summary>
  public string Status { get; private set; } = "unparsed";

  /// <summary>Whether the volume was written by a big-endian host.</summary>
  public bool IsBigEndian { get; private set; }

  /// <summary>
  /// Gets or sets the block size.
  /// </summary>
public int BlockSize { get; private set; } = VxFsLayout.BlockSize;

  /// <summary>The files in the root directory, in the order it lists them.</summary>
  public IReadOnlyList<VolumeFile> Files => this._files;
  private readonly List<VolumeFile> _files = [];

  /// <summary>Where the root directory's own blocks are.</summary>
  public IReadOnlyList<Extent> RootDirectoryExtents { get; private set; } = [];

  /// <summary>The block the volume's data area starts at, per the superblock.</summary>
  public long FirstDataBlock { get; private set; }

  /// <summary>Blocks the metadata chain occupies and no file may be moved onto.</summary>
  public IReadOnlyList<Extent> ReservedExtents => this._reserved;
  private readonly List<Extent> _reserved = [];

  /// <summary>
  /// Gets the image length.
  /// </summary>
public long ImageLength => this._image.LongLength;

  /// <summary>
  /// Initializes a new instance of <see cref="VxFsVolume"/>.
  /// </summary>
public VxFsVolume(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    using var ms = new MemoryStream();
    image.Position = 0;
    image.CopyTo(ms);
    this._image = ms.ToArray();

    try {
      var sb = VxFsLayout.SuperblockOffset;
      if (this._image.Length < sb + VxFsLayout.SuperblockBytes) {
        this.Status = "too small for a superblock";
        return;
      }

      if (BinaryPrimitives.ReadUInt32LittleEndian(this._image.AsSpan(sb)) == VxFsLayout.SuperMagic)
        this.IsBigEndian = false;
      else if (BinaryPrimitives.ReadUInt32BigEndian(this._image.AsSpan(sb)) == VxFsLayout.SuperMagic)
        this.IsBigEndian = true;
      else {
        this.Status = "no superblock magic";
        return;
      }

      var blockSize = (int)this.U32(sb + VxFsLayout.SbBsize);
      if (blockSize is < 256 or > 65536 || (blockSize & (blockSize - 1)) != 0) {
        this.Status = $"implausible block size {blockSize}";
        return;
      }
      this.BlockSize = blockSize;
      this.FirstDataBlock = this.U32(sb + VxFsLayout.SbBstart);

      var oltBlock = this.U32(sb + VxFsLayout.SbOltext);
      if (!this.ReadOlt(oltBlock, out var fsHeadInode, out var structuralBlock)) return;

      // The raw inode array the driver reads before it can read any list as a
      // file. Inode n of it sits at a stride of 256 from its first block.
      var structuralBase = structuralBlock * this.BlockSize;
      var fsHeadExtents = this.ReadExtents(structuralBase + (long)fsHeadInode * VxFsLayout.InodeSize);
      if (fsHeadExtents.Count == 0) { this.Status = "the fileset-header inode names no blocks"; return; }

      // Its first two blocks are the structural and primary fileset headers.
      var structuralFsh = this.FileOffset(fsHeadExtents, 0);
      var primaryFsh = this.FileOffset(fsHeadExtents, this.BlockSize);
      if (structuralFsh < 0 || primaryFsh < 0) { this.Status = "fileset headers out of range"; return; }

      var structuralIlistInode = this.U32(structuralFsh + 48);
      var primaryIlistInode = this.U32(primaryFsh + 48);

      var structuralIlistExtents =
        this.ReadExtents(structuralBase + (long)structuralIlistInode * VxFsLayout.InodeSize);
      if (structuralIlistExtents.Count == 0) { this.Status = "the structural list names no blocks"; return; }

      var primaryIlistInodeAt =
        this.FileOffset(structuralIlistExtents, (long)primaryIlistInode * VxFsLayout.InodeSize);
      if (primaryIlistInodeAt < 0) { this.Status = "the primary list inode is out of range"; return; }

      var primaryIlist = this.ReadExtents(primaryIlistInodeAt);
      if (primaryIlist.Count == 0) { this.Status = "the primary list names no blocks"; return; }

      // Everything up to here is structure a file must stay off.
      this._reserved.Add(new Extent(0, structuralBlock));
      foreach (var e in fsHeadExtents) this._reserved.Add(e);
      foreach (var e in structuralIlistExtents) this._reserved.Add(e);
      foreach (var e in primaryIlist) this._reserved.Add(e);
      this._reserved.Add(new Extent(oltBlock, 1));

      var rootAt = this.FileOffset(primaryIlist, (long)VxFsLayout.RootInode * VxFsLayout.InodeSize);
      if (rootAt < 0) { this.Status = "the root inode is out of range"; return; }
      if ((this.U32(rootAt + VxFsLayout.InMode) & VxFsLayout.TypeMask) != VxFsLayout.ModeDir) {
        this.Status = "inode 2 is not a directory";
        return;
      }

      var rootExtents = this.ReadExtents(rootAt);
      this.RootDirectoryExtents = rootExtents;
      var rootSize = this.I64(rootAt + VxFsLayout.InSize);

      foreach (var (name, inode) in this.ReadDirectory(rootExtents, rootSize)) {
        var at = this.FileOffset(primaryIlist, (long)inode * VxFsLayout.InodeSize);
        if (at < 0) continue;
        if ((this.U32(at + VxFsLayout.InMode) & VxFsLayout.TypeMask) != VxFsLayout.ModeReg) continue;

        this._files.Add(new VolumeFile(
          name, inode, at, this.I64(at + VxFsLayout.InSize), this.ReadExtents(at)));
      }

      this.Valid = true;
      this.Status = "ok";
    } catch (Exception e) {
      this.Status = $"walk failed: {e.GetType().Name}";
    }
  }

  /// <summary>Returns a file's bytes.</summary>
  public byte[] Read(VolumeFile file) {
    ArgumentNullException.ThrowIfNull(file);

    var data = new byte[file.Size];
    var written = 0L;
    foreach (var extent in file.Extents) {
      for (var i = 0L; i < extent.Count && written < file.Size; ++i) {
        var from = (extent.Block + i) * this.BlockSize;
        var take = (int)Math.Min(this.BlockSize, file.Size - written);
        if (from < 0 || from + take > this._image.LongLength) return data;
        Array.Copy(this._image, from, data, written, take);
        written += take;
      }
    }

    return data;
  }

  /// <summary>
  /// Reads the table naming the fileset-header inode and the block the raw
  /// inode array starts at.
  /// </summary>
  private bool ReadOlt(long oltBlock, out uint fsHeadInode, out long structuralBlock) {
    fsHeadInode = 0;
    structuralBlock = 0;

    var at = oltBlock * this.BlockSize;
    if (at < 0 || at + this.BlockSize > this._image.LongLength) {
      this.Status = "the object location table is out of range";
      return false;
    }

    if (this.U32(at) != VxFsLayout.OltMagic) {
      this.Status = "no object location table magic";
      return false;
    }

    var cursor = at + this.U32(at + 4);
    var end = at + this.BlockSize;
    while (cursor + 8 <= end) {
      var type = this.U32(cursor);
      var size = this.U32(cursor + 4);
      if (size == 0) break;

      if (type == VxFsLayout.OltFsHead && fsHeadInode == 0) fsHeadInode = this.U32(cursor + 8);
      else if (type == VxFsLayout.OltIlist && structuralBlock == 0) structuralBlock = this.U32(cursor + 8);
      cursor += size;
    }

    if (fsHeadInode == 0 || structuralBlock == 0) {
      this.Status = "the object location table names no fileset header or inode list";
      return false;
    }

    return true;
  }

  /// <summary>Reads the direct extents out of an inode.</summary>
  /// <remarks>
  /// Only the direct kind is read. An inode may instead hold its data inline or
  /// point at an extent tree, and a volume using either is one this declines
  /// rather than half-understands.
  /// </remarks>
  private List<Extent> ReadExtents(long inodeAt) {
    var extents = new List<Extent>();
    if (inodeAt < 0 || inodeAt + VxFsLayout.InodeSize > this._image.LongLength) return extents;
    if (this._image[inodeAt + VxFsLayout.InOrgtype] != VxFsLayout.OrgExt4) return extents;

    for (var i = 0; i < VxFsLayout.DirectExtents; ++i) {
      var slot = inodeAt + VxFsLayout.Ext4Direct + i * 8;
      var block = this.U32(slot);
      var count = this.U32(slot + 4);
      if (count == 0) continue;
      extents.Add(new Extent(block, count));
    }

    return extents;
  }

  /// <summary>Turns an offset inside a file into an offset inside the image.</summary>
  private long FileOffset(IReadOnlyList<Extent> extents, long fileOffset) {
    var remaining = fileOffset;
    foreach (var extent in extents) {
      var span = extent.Count * this.BlockSize;
      if (remaining < span) {
        var at = extent.Block * this.BlockSize + remaining;
        return at >= 0 && at + VxFsLayout.InodeSize <= this._image.LongLength ? at : -1;
      }

      remaining -= span;
    }

    return -1;
  }

  /// <summary>
  /// Walks the root directory's blocks and yields what each entry names.
  /// </summary>
  /// <remarks>
  /// Every block opens with a header the walk skips, and an entry whose record
  /// length is zero means the rest of that block is empty — the walk moves to
  /// the next one. <c>.</c> and <c>..</c> are not on disk; the driver invents
  /// both, so a volume that stored them would list each twice.
  /// </remarks>
  private IEnumerable<(string Name, uint Inode)> ReadDirectory(IReadOnlyList<Extent> extents, long size) {
    var limit = VxFsLayout.DirRound(size);
    for (var pos = 0L; pos < limit;) {
      var blockStart = pos - pos % this.BlockSize;
      var at = this.FileOffset(extents, blockStart);
      if (at < 0) yield break;

      var hashChains = this.U16(at + 2);
      var cursor = VxFsLayout.DirBlockHeaderBytes + hashChains * 2;

      while (blockStart + cursor < limit && cursor + VxFsLayout.DirNameOffset <= this.BlockSize) {
        var entry = at + cursor;
        if (entry + VxFsLayout.DirNameOffset > this._image.LongLength) yield break;

        var recordLength = this.U16(entry + 4);
        if (recordLength == 0) break;
        if (cursor + recordLength > this.BlockSize) break;

        var inode = this.U32(entry);
        var nameLength = this.U16(entry + 6);
        if (inode != 0 && nameLength > 0 && VxFsLayout.DirNameOffset + nameLength <= recordLength)
          yield return (
            Encoding.ASCII.GetString(this._image, (int)(entry + VxFsLayout.DirNameOffset), nameLength),
            inode);

        cursor += recordLength;
      }

      pos = blockStart + this.BlockSize;
    }
  }

  private uint U32(long at) => this.IsBigEndian
    ? BinaryPrimitives.ReadUInt32BigEndian(this._image.AsSpan((int)at))
    : BinaryPrimitives.ReadUInt32LittleEndian(this._image.AsSpan((int)at));

  private ushort U16(long at) => this.IsBigEndian
    ? BinaryPrimitives.ReadUInt16BigEndian(this._image.AsSpan((int)at))
    : BinaryPrimitives.ReadUInt16LittleEndian(this._image.AsSpan((int)at));

  private long I64(long at) => this.IsBigEndian
    ? BinaryPrimitives.ReadInt64BigEndian(this._image.AsSpan((int)at))
    : BinaryPrimitives.ReadInt64LittleEndian(this._image.AsSpan((int)at));
}
