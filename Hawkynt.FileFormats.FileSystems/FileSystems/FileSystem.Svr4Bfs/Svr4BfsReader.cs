using System.Buffers.Binary;

namespace FileSystem.Svr4Bfs;

/// <summary>One name in the BFS root directory.</summary>
/// <param name="Name">The directory-entry name (at most 14 bytes).</param>
/// <param name="Ino">The inode the name points at.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="LastModified">Inode modification time.</param>
/// <param name="Inode">The inode itself.</param>
/// <param name="DirentOffset">Byte offset of the directory entry inside the image.</param>
public sealed record Svr4BfsEntry(string Name, int Ino, long Size, DateTime LastModified, Svr4BfsInode Inode, long DirentOffset);

/// <summary>
/// Reads an SVR4 / UnixWare Boot File System image: superblock, inode table and the
/// single root directory. BFS has no subdirectories, so every entry is a root-level file.
/// </summary>
public sealed class Svr4BfsReader {
  private readonly Stream _stream;

  /// <summary>The parsed superblock.</summary>
  public Svr4BfsSuperblock Superblock { get; }

  /// <summary>Highest inode number the inode area holds.</summary>
  public int LastInode { get; }

  /// <summary>Every inode slot from the root (2) to <see cref="LastInode"/>, indexed by <c>ino - 2</c>.</summary>
  public IReadOnlyList<Svr4BfsInode> Inodes { get; }

  /// <summary>The root directory inode.</summary>
  public Svr4BfsInode Root => this.Inodes[0];

  /// <summary>Every file name the root directory lists, in directory order.</summary>
  public IReadOnlyList<Svr4BfsEntry> Entries { get; }

  /// <summary>Opens <paramref name="stream"/>; throws <see cref="InvalidDataException"/> when it is not BFS.</summary>
  public Svr4BfsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;

    var block = new byte[Svr4BfsLayout.BlockSize];
    stream.Position = 0;
    stream.ReadExactly(block);
    this.Superblock = Svr4BfsSuperblock.TryParse(block)
      ?? throw new InvalidDataException("Not an SVR4 BFS volume: superblock magic 0x1BADFACE or its geometry is missing.");
    if (this.Superblock.Start > stream.Length)
      throw new InvalidDataException("SVR4 BFS inode area extends past the end of the image.");

    this.LastInode = Math.Min(Svr4BfsLayout.LastInode(this.Superblock.Start), ushort.MaxValue);
    var count = this.LastInode - Svr4BfsLayout.RootIno + 1;
    var table = new byte[count * Svr4BfsLayout.InodeSize];
    stream.Position = Svr4BfsLayout.BlockSize;
    stream.ReadExactly(table);
    var inodes = new Svr4BfsInode[count];
    for (var i = 0; i < count; ++i)
      inodes[i] = Svr4BfsInode.Parse(table.AsSpan(i * Svr4BfsLayout.InodeSize, Svr4BfsLayout.InodeSize));
    this.Inodes = inodes;

    var root = inodes[0];
    if (root.Ino != Svr4BfsLayout.RootIno || root.VType != Svr4BfsLayout.VDir || !this.IsReadable(root))
      throw new InvalidDataException("SVR4 BFS root directory inode is missing or out of range.");
    this.Entries = this.ReadDirectory(root);
  }

  /// <summary>The inode with number <paramref name="ino"/>, or null when it is out of range or free.</summary>
  public Svr4BfsInode? GetInode(int ino) {
    if (ino < Svr4BfsLayout.RootIno || ino > this.LastInode) return null;
    var inode = this.Inodes[ino - Svr4BfsLayout.RootIno];
    return inode.Ino == ino ? inode : null;
  }

  /// <summary>
  /// True when the inode's blocks lie inside the data area and its size fits them —
  /// the condition under which its bytes can be returned without guessing.
  /// </summary>
  public bool IsReadable(Svr4BfsInode inode) {
    if (inode.HasNoBlocks) return true;
    var sb = this.Superblock;
    if (inode.StartBlock < sb.FirstDataBlock || inode.EndBlock < inode.StartBlock || inode.EndBlock > sb.LastBlock) return false;
    var size = inode.Size;
    var capacity = ((long)inode.EndBlock - inode.StartBlock + 1) * Svr4BfsLayout.BlockSize;
    return size >= 0 && size <= capacity && (long)inode.StartBlock * Svr4BfsLayout.BlockSize + size <= this._stream.Length;
  }

  /// <summary>Returns the bytes of <paramref name="entry"/>; refuses an inode whose extent is inconsistent.</summary>
  public byte[] Extract(Svr4BfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return this.ReadInode(entry.Inode);
  }

  private byte[] ReadInode(Svr4BfsInode inode) {
    if (!this.IsReadable(inode))
      throw new InvalidDataException($"SVR4 BFS inode {inode.Ino} names blocks outside the volume.");
    if (inode.HasNoBlocks) return [];
    var data = new byte[inode.Size];
    this._stream.Position = (long)inode.StartBlock * Svr4BfsLayout.BlockSize;
    this._stream.ReadExactly(data);
    return data;
  }

  private List<Svr4BfsEntry> ReadDirectory(Svr4BfsInode root) {
    var raw = this.ReadInode(root);
    var entries = new List<Svr4BfsEntry>();
    var dirBase = (long)root.StartBlock * Svr4BfsLayout.BlockSize;
    for (var off = 0; off + Svr4BfsLayout.DirentSize <= raw.Length; off += Svr4BfsLayout.DirentSize) {
      var slot = raw.AsSpan(off, Svr4BfsLayout.DirentSize);
      int ino = BinaryPrimitives.ReadUInt16LittleEndian(slot);
      if (ino == 0) continue;
      var name = Svr4BfsLayout.DecodeName(slot[2..]);
      if (name is "." or "..") continue;
      // A name pointing at a free or out-of-range inode is a dangling entry: it has no
      // bytes to give, so it is not listed rather than listed as an empty file.
      if (this.GetInode(ino) is not { } inode || inode.VType != Svr4BfsLayout.VReg || !this.IsReadable(inode)) continue;
      entries.Add(new(name, ino, inode.Size, DateTime.UnixEpoch.AddSeconds(inode.Mtime), inode, dirBase + off));
    }
    return entries;
  }
}
