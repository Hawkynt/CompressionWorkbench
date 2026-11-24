using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Chfs;

/// <summary>One vnode to write: a directory (with children), a regular file, a symbolic link or a device node.</summary>
/// <param name="Name">The name in its parent directory (1 to 255 UTF-8 bytes, no '/'); ignored for the root.</param>
/// <param name="Mode">The Unix mode word, file type included (0x4000 directory, 0x8000 file, 0xA000 symlink, …).</param>
public sealed record ChfsWriteNode(string Name, uint Mode) {
  /// <summary>Gets the value stored in the vnode's uid field.</summary>
  public uint Uid { get; init; }
  /// <summary>Gets the value stored in the vnode's gid field.</summary>
  public uint Gid { get; init; }
  /// <summary>Gets the access time (UNIX seconds).</summary>
  public uint ATime { get; init; }
  /// <summary>Gets the modification time (UNIX seconds).</summary>
  public uint MTime { get; init; }
  /// <summary>Gets the status-change time (UNIX seconds).</summary>
  public uint CTime { get; init; }
  /// <summary>Gets the size a directory's vnode records (<c>makefs</c> copies the source directory's size; FFS reports 512).</summary>
  public uint DirectorySize { get; init; } = ChfsWriter.DefaultDirectorySize;
  /// <summary>Gets the file contents, the link target or a device node's device number.</summary>
  public byte[] Data { get; init; } = [];
  /// <summary>Gets a directory's entries; they are written sorted by name, as <c>makefs</c> does.</summary>
  public IReadOnlyList<ChfsWriteNode> Children { get; init; } = [];

  internal bool IsDirectory => (this.Mode & 0xF000) == 0x4000;
}

/// <summary>The flash an image is laid out for.</summary>
/// <param name="PageSize">Flash page size: the eraseblock header sits one page in, and nodes start two pages in.</param>
/// <param name="EraseBlockSize">Eraseblock size.</param>
/// <param name="Nand">True for NAND eraseblock headers (with a serial), false for NOR.</param>
public readonly record struct ChfsGeometry(int PageSize, int EraseBlockSize, bool Nand) {
  /// <summary><c>makefs -t chfs</c>'s defaults, and the geometry of NetBSD's <c>nandemulator(4)</c>: NAND, 2 KiB pages, 128 KiB eraseblocks.</summary>
  public static ChfsGeometry Default => new(2048, 131072, true);
}

/// <summary>
/// Writes CHFS images as NetBSD's <c>makefs -t chfs</c> lays them out (docs/CHFS-ON-DISK.md):
/// every eraseblock opens with an erase-counter page and an eraseblock-header page, then each
/// directory's entries in name order get a vnode node, a directory-entry node and (for anything
/// but a directory) data nodes; subdirectories follow, depth first. A node that does not fit in
/// the rest of an eraseblock starts the next one; gaps are 0xFF (erased flash).
/// </summary>
/// <remarks>
/// Converted from NetBSD's BSD-licensed <c>usr.sbin/makefs/chfs/chfs_mkfs.c</c> and
/// <c>usr.sbin/makefs/chfs.c</c>; see <c>THIRD-PARTY-NOTICE.Chfs.txt</c>. Where <c>makefs</c>'s
/// images do not read back through NetBSD's kernel, the writer does what the kernel does
/// instead: data nodes cover 4 KiB, not a page, and are numbered rather than all version 0.
/// Two things <c>makefs</c> does by accident are not copied either: it reads a name past its
/// end (so a name can carry stray bytes), and it stores the owner's uid in the gid field and
/// the gid in the uid field. This writer stores names exactly and puts each field where the
/// kernel reads it.
/// </remarks>
public static class ChfsWriter {
  /// <summary>The directory size <c>makefs</c> records for a directory on FFS.</summary>
  public const uint DefaultDirectorySize = 512;

  /// <summary>
  /// The largest data node the writer emits by default: the kernel's 4 KiB block, which is how
  /// long the data nodes the kernel writes are. NetBSD 9.5 reads back files cut into smaller
  /// nodes wrongly (see docs/CHFS-ON-DISK.md); <c>makefs</c> cuts at the page size.
  /// </summary>
  public const int DefaultDataNodeSize = 4096;

  private const int EcHeaderSize = 12, NorHeaderSize = 8, NandHeaderSize = 16;
  private const int VnodeSize = 64, DataHeaderSize = 48, DirentHeaderSize = 52;

  /// <summary>Writes the image of the tree under <paramref name="root"/>.</summary>
  /// <param name="output">The stream to write to.</param>
  /// <param name="root">The root directory (vnode 2); like <c>makefs</c>, no vnode is written for it.</param>
  /// <param name="geometry">The flash layout; <see cref="ChfsGeometry.Default"/> when omitted.</param>
  /// <param name="eraseCounts">Erase counts to keep, by eraseblock index; blocks without one (or with 0) get 1, as <c>makefs</c> writes.</param>
  /// <param name="dataNodeSize">The largest data node; <see cref="DefaultDataNodeSize"/> when omitted (<c>makefs</c> uses the page size).</param>
  /// <exception cref="ArgumentException">A name cannot be stored, a node does not fit an eraseblock, or the root is not a directory.</exception>
  public static void Write(Stream output, ChfsWriteNode root, ChfsGeometry? geometry = null, IReadOnlyList<uint>? eraseCounts = null, int dataNodeSize = DefaultDataNodeSize) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(root);
    var g = geometry ?? ChfsGeometry.Default;
    if (!root.IsDirectory)
      throw new ArgumentException("The CHFS root must be a directory.", nameof(root));
    if (g.PageSize < NandHeaderSize || g.PageSize % 4 != 0)
      throw new ArgumentOutOfRangeException(nameof(geometry), $"Page size {g.PageSize} must be a multiple of 4 and at least {NandHeaderSize}.");
    // the largest node is a full data node; it has to fit behind the two header pages
    if (dataNodeSize <= 0)
      throw new ArgumentOutOfRangeException(nameof(dataNodeSize));
    if (g.EraseBlockSize % g.PageSize != 0 || g.EraseBlockSize < 2 * g.PageSize + DataHeaderSize + dataNodeSize + 3)
      throw new ArgumentOutOfRangeException(nameof(geometry), $"Eraseblock size {g.EraseBlockSize} must be a multiple of the page size with room for a full data node after two header pages.");

    var w = new Image(g, eraseCounts, dataNodeSize);
    w.EraseBlockHeader();
    var next = ChfsReader.RootVno;
    WriteDirectory(w, root, ChfsReader.RootVno, ref next, 0);
    w.PadBlock();
    w.CopyTo(output);
  }

  private static void WriteDirectory(Image w, ChfsWriteNode directory, ulong vno, ref ulong next, int depth) {
    if (depth > 256)
      throw new ArgumentException("CHFS directory nesting beyond 256 levels.");
    var children = directory.Children.Select(c => (Node: c, Name: Name(c))).ToList();
    children.Sort((a, b) => a.Name.AsSpan().SequenceCompareTo(b.Name));
    for (var i = 1; i < children.Count; ++i)
      if (children[i].Name.AsSpan().SequenceEqual(children[i - 1].Name))
        throw new ArgumentException($"'{children[i].Node.Name}' appears twice in one directory.");

    var numbers = new ulong[children.Count];
    for (var i = 0; i < children.Count; ++i) {
      var (child, name) = children[i];
      numbers[i] = ++next;
      w.Vnode(numbers[i], child);
      w.Dirent(numbers[i], vno, child.Mode, name);
      if (!child.IsDirectory)
        for (int offset = 0, version = 1; offset < child.Data.Length; offset += w.DataNodeSize)
          w.Data(numbers[i], (ulong)version++, (ulong)offset, child.Data.AsSpan(offset, Math.Min(w.DataNodeSize, child.Data.Length - offset)));
    }
    for (var i = 0; i < children.Count; ++i)
      if (children[i].Node.IsDirectory)
        WriteDirectory(w, children[i].Node, numbers[i], ref next, depth + 1);
  }

  private static byte[] Name(ChfsWriteNode node) {
    var name = Encoding.UTF8.GetBytes(node.Name);
    if (name.Length is 0 or > 255 || node.Name.Contains('/') || node.Name.Contains('\0') || node.Name is "." or "..")
      throw new ArgumentException($"'{node.Name}' cannot be a CHFS name: it needs 1 to 255 UTF-8 bytes and no '/' or NUL.");
    return name;
  }

  /// <summary><c>IFTOCHT</c>: the directory-entry type for a mode's file type.</summary>
  private static byte EntryType(uint mode) => (mode & 0xF000) switch {
    0x1000 => 7, 0x2000 => 4, 0x4000 => 2, 0x6000 => 3, 0x8000 => 1, 0xA000 => 5, 0xC000 => 6,
    _ => throw new ArgumentException($"Mode {mode:X} has no CHFS file type."),
  };

  private sealed class Image(ChfsGeometry geometry, IReadOnlyList<uint>? eraseCounts, int dataNodeSize) {
    private readonly MemoryStream _image = new();
    private uint _lid;
    private ulong _serial;
    private ulong _version;

    public int DataNodeSize => dataNodeSize;
    private long InBlock => this._image.Length % geometry.EraseBlockSize;

    public void EraseBlockHeader() {
      var page = new byte[geometry.PageSize];
      page.AsSpan().Fill(0xFF);
      BinaryPrimitives.WriteUInt32LittleEndian(page, ChfsReader.EraseBlockMagic);
      // makefs starts every block at erase count 1; a rewrite keeps the count the block had
      var count = eraseCounts != null && this._lid < eraseCounts.Count && eraseCounts[(int)this._lid] > 0 ? eraseCounts[(int)this._lid] : 1u;
      BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), count);
      BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), Crc32.Compute(page.AsSpan(8, 4)));
      this._image.Write(page);

      page.AsSpan().Fill(0xFF);
      BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), this._lid++);
      var size = NorHeaderSize;
      if (geometry.Nand) {
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(8), ++this._serial);
        size = NandHeaderSize;
      }
      BinaryPrimitives.WriteUInt32LittleEndian(page, Crc32.Compute(page.AsSpan(4, size - 4)));
      this._image.Write(page);
    }

    public void PadBlock() {
      while (this.InBlock != 0)
        this._image.WriteByte(0xFF);
    }

    /// <summary>Starts a new eraseblock when <paramref name="required"/> bytes do not fit in this one.</summary>
    private void Reserve(int required) {
      if (this.InBlock + required <= geometry.EraseBlockSize)
        return;
      this.PadBlock();
      this.EraseBlockHeader();
    }

    private void PadWord() {
      while (this._image.Length % 4 != 0)
        this._image.WriteByte(0xFF);
    }

    private static void Header(Span<byte> node, ushort type, int length) {
      BinaryPrimitives.WriteUInt16LittleEndian(node, ChfsReader.NodeMagic);
      BinaryPrimitives.WriteUInt16LittleEndian(node[2..], type);
      BinaryPrimitives.WriteUInt32LittleEndian(node[4..], (uint)length);
      BinaryPrimitives.WriteUInt32LittleEndian(node[8..], Crc32.Compute(node[..8]));
    }

    public void Vnode(ulong vno, ChfsWriteNode n) {
      Span<byte> node = stackalloc byte[VnodeSize];
      node.Clear();
      Header(node, ChfsReader.TypeVnode, VnodeSize);
      BinaryPrimitives.WriteUInt64LittleEndian(node[12..], vno);
      BinaryPrimitives.WriteUInt64LittleEndian(node[20..], this._version++);
      BinaryPrimitives.WriteUInt32LittleEndian(node[28..], n.Uid);
      BinaryPrimitives.WriteUInt32LittleEndian(node[32..], n.Gid);
      BinaryPrimitives.WriteUInt32LittleEndian(node[36..], n.Mode);
      BinaryPrimitives.WriteUInt32LittleEndian(node[40..], n.IsDirectory ? n.DirectorySize : (uint)n.Data.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(node[44..], n.ATime);
      BinaryPrimitives.WriteUInt32LittleEndian(node[48..], n.MTime);
      BinaryPrimitives.WriteUInt32LittleEndian(node[52..], n.CTime);
      // node[56..60] is the unused dsize field, left zero
      BinaryPrimitives.WriteUInt32LittleEndian(node[60..], Crc32.Compute(node[..60]));
      this.Reserve(VnodeSize);
      this._image.Write(node);
      this.PadWord();
    }

    public void Dirent(ulong vno, ulong parent, uint mode, byte[] name) {
      Span<byte> node = stackalloc byte[DirentHeaderSize];
      node.Clear();
      Header(node, ChfsReader.TypeDirent, (DirentHeaderSize + name.Length + 3) & ~3);
      BinaryPrimitives.WriteUInt64LittleEndian(node[12..], vno);
      BinaryPrimitives.WriteUInt64LittleEndian(node[20..], parent);
      BinaryPrimitives.WriteUInt64LittleEndian(node[28..], this._version++);
      // node[36..40] mctime stays zero, as makefs leaves it
      node[40] = (byte)name.Length;
      node[41] = EntryType(mode);
      BinaryPrimitives.WriteUInt32LittleEndian(node[44..], Crc32.Compute(name));
      BinaryPrimitives.WriteUInt32LittleEndian(node[48..], Crc32.Compute(node[..48]));
      this.Reserve(DirentHeaderSize + name.Length);
      this._image.Write(node);
      this._image.Write(name);
      this.PadWord();
    }

    public void Data(ulong vno, ulong version, ulong offset, ReadOnlySpan<byte> data) {
      Span<byte> node = stackalloc byte[DataHeaderSize];
      node.Clear();
      this.Reserve(DataHeaderSize + data.Length);
      Header(node, ChfsReader.TypeData, (DataHeaderSize + data.Length + 3) & ~3);
      BinaryPrimitives.WriteUInt64LittleEndian(node[12..], vno);
      // makefs writes every data node as version 0; the kernel numbers each vnode's nodes, so
      // each file's data nodes are numbered 1, 2, 3 ... here, which leaves the vnode and entry
      // versions as makefs has them
      BinaryPrimitives.WriteUInt64LittleEndian(node[20..], version);
      BinaryPrimitives.WriteUInt64LittleEndian(node[28..], offset);
      BinaryPrimitives.WriteUInt32LittleEndian(node[36..], (uint)data.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(node[40..], Crc32.Compute(data));
      BinaryPrimitives.WriteUInt32LittleEndian(node[44..], Crc32.Compute(node[..44]));
      this._image.Write(node);
      this._image.Write(data);
      this.PadWord();
    }

    public void CopyTo(Stream output) {
      this._image.Position = 0;
      this._image.CopyTo(output);
    }
  }
}
