using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Jffs1;

/// <summary>One inode to write: a directory (with children), a regular file or a symbolic link.</summary>
/// <param name="Name">The name in its parent directory (Latin-1, 1 to 255 bytes); ignored for the root.</param>
/// <param name="Mode">The Unix mode word, file type included (0x4000 directory, 0x8000 file, 0xA000 symlink).</param>
public sealed record Jffs1WriteNode(string Name, uint Mode) {
  /// <summary>Gets the owner.</summary>
  public ushort Uid { get; init; }
  /// <summary>Gets the group.</summary>
  public ushort Gid { get; init; }
  /// <summary>Gets the access time (UNIX seconds).</summary>
  public uint ATime { get; init; }
  /// <summary>Gets the modification time (UNIX seconds).</summary>
  public uint MTime { get; init; }
  /// <summary>Gets the status-change time (UNIX seconds).</summary>
  public uint CTime { get; init; }
  /// <summary>Gets the file contents or the link target.</summary>
  public byte[] Data { get; init; } = [];
  /// <summary>Gets a directory's entries, written in this order.</summary>
  public IReadOnlyList<Jffs1WriteNode> Children { get; init; } = [];

  internal bool IsDirectory => (this.Mode & 0xF000) == 0x4000;
}

/// <summary>
/// Writes JFFS version 1 images as <c>mkfs.jffs</c> lays them out (docs/JFFS1-ON-DISK.md): the root
/// inode 1, then every inode depth first, numbered in that order, each file's data in nodes of at
/// most 32 KiB, names and data padded to four bytes with 0xFF.
/// </summary>
public static class Jffs1Writer {
  /// <summary>The largest data node <c>mkfs.jffs</c> writes by default (half its default erase size).</summary>
  public const int DefaultChunkSize = 32768;

  /// <summary>Writes the image of the tree under <paramref name="root"/>.</summary>
  /// <param name="output">The stream to write to.</param>
  /// <param name="root">The root directory; its name is not stored, its owner is always root (0).</param>
  /// <param name="bigEndian">Whether to write a big-endian image (little-endian by default).</param>
  /// <param name="chunkSize">The largest data node.</param>
  /// <exception cref="ArgumentException">A name cannot be stored, a node is not a directory, file or link, or the root is not a directory.</exception>
  public static void Write(Stream output, Jffs1WriteNode root, bool bigEndian = false, int chunkSize = DefaultChunkSize) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(root);
    if (!root.IsDirectory)
      throw new ArgumentException("The JFFS root must be a directory.", nameof(root));
    if (chunkSize <= 0)
      throw new ArgumentOutOfRangeException(nameof(chunkSize));
    var writer = new NodeWriter(output, bigEndian);
    writer.Node(Jffs1Reader.RootIno, 0, 1, root with { Uid = 0, Gid = 0 }, [], 0, []);
    var next = Jffs1Reader.RootIno + 1;
    WriteChildren(writer, root, Jffs1Reader.RootIno, ref next, chunkSize, 0);
  }

  private static void WriteChildren(NodeWriter writer, Jffs1WriteNode directory, uint parent, ref uint next, int chunkSize, int depth) {
    if (depth > 256)
      throw new ArgumentException("JFFS directory nesting beyond 256 levels.");
    foreach (var child in directory.Children) {
      var name = Encoding.Latin1.GetBytes(child.Name);
      if (name.Length is 0 or > 255 || Encoding.Latin1.GetString(name) != child.Name || child.Name.Contains('/') || child.Name is "." or "..")
        throw new ArgumentException($"'{child.Name}' cannot be a JFFS name: it needs 1 to 255 Latin-1 characters and no '/'.");
      var type = child.Mode & 0xF000;
      if (type is not (0x4000 or 0x8000 or 0xA000))
        throw new ArgumentException($"'{child.Name}': only directories, regular files and symbolic links are written.");
      var ino = next++;
      if (type == 0x8000 && child.Data.Length > 0) {
        // the file's data in nodes of at most chunkSize bytes, each with the name and a new version
        uint version = 1;
        for (var offset = 0; offset < child.Data.Length; offset += chunkSize)
          writer.Node(ino, parent, version++, child, name, (uint)offset, child.Data.AsSpan(offset, Math.Min(chunkSize, child.Data.Length - offset)));
      } else
        writer.Node(ino, parent, 1, child, name, 0, type == 0xA000 ? child.Data : []);
      if (type == 0x4000)
        WriteChildren(writer, child, ino, ref next, chunkSize, depth + 1);
    }
  }

  private sealed class NodeWriter(Stream output, bool bigEndian) {
    private readonly byte[] _header = new byte[Jffs1Reader.HeaderSize];

    public void Node(uint ino, uint parent, uint version, Jffs1WriteNode node, ReadOnlySpan<byte> name, uint offset, ReadOnlySpan<byte> data) {
      var h = this._header.AsSpan();
      h.Clear();
      this.U32(h, 0, Jffs1Reader.Magic);
      this.U32(h, 4, ino);
      this.U32(h, 8, parent);
      this.U32(h, 12, version);
      this.U32(h, 16, node.Mode);
      this.U16(h, 20, node.Uid);
      this.U16(h, 22, node.Gid);
      this.U32(h, 24, node.ATime);
      this.U32(h, 28, node.MTime);
      this.U32(h, 32, node.CTime);
      this.U32(h, 36, offset);
      this.U32(h, 40, (uint)data.Length);
      // 44 rsize 0; 48 nsize; 49 nlink 1; 50 flags 0; 51 accurate (0 while the checksum is taken)
      h[48] = (byte)name.Length;
      h[49] = 1;
      this.U32(h, 52, Sum(data));
      this.U16(h, 56, (ushort)Sum(name));
      this.U16(h, 58, (ushort)Sum(h));
      h[51] = 0xFF;
      output.Write(h);
      WritePadded(name);
      WritePadded(data);
    }

    private void WritePadded(ReadOnlySpan<byte> bytes) {
      output.Write(bytes);
      for (var i = bytes.Length; (i & 3) != 0; ++i)
        output.WriteByte(0xFF);
    }

    private static uint Sum(ReadOnlySpan<byte> bytes) {
      uint sum = 0;
      foreach (var b in bytes)
        sum += b;
      return sum;
    }

    private void U32(Span<byte> h, int at, uint value) {
      if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(h[at..], value);
      else BinaryPrimitives.WriteUInt32LittleEndian(h[at..], value);
    }

    private void U16(Span<byte> h, int at, ushort value) {
      if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(h[at..], value);
      else BinaryPrimitives.WriteUInt16LittleEndian(h[at..], value);
    }
  }
}
