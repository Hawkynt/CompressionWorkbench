using System.Buffers.Binary;

namespace FileFormat.CompactPro;

/// <summary>
/// Writes single-volume Compact Pro archives: RLE-coded forks (no LZH), folders, data and resource forks,
/// Finder type/creator and dates, with the directory and per-file CRCs real readers check.
/// </summary>
public sealed class CompactProWriter {
  private sealed record Member(string[] Path, byte[] Data, byte[] Resource, uint Type, uint Creator, DateTime Modified);

  private readonly List<Member> _files = [];
  private readonly HashSet<string> _folders = new(StringComparer.Ordinal);

  public void AddFile(string path, byte[] data, byte[]? resource = null, uint type = 0x3F3F3F3F, uint creator = 0x3F3F3F3F, DateTime? modified = null) {
    var parts = Split(path);
    for (var i = 1; i < parts.Length; ++i)
      this._folders.Add(string.Join('/', parts[..i]));
    this._files.Add(new Member(parts, data, resource ?? [], type, creator, modified ?? DateTime.UtcNow));
  }

  public void AddFolder(string path) {
    var parts = Split(path);
    for (var i = 1; i <= parts.Length; ++i)
      this._folders.Add(string.Join('/', parts[..i]));
  }

  private static string[] Split(string path) {
    var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
      throw new ArgumentException("Empty Compact Pro path.", nameof(path));
    foreach (var p in parts)
      if (CompactProReader.MacRoman.GetByteCount(p) > 63)
        throw new ArgumentException($"Compact Pro names are limited to 63 bytes: '{p}'.", nameof(path));
    return parts;
  }

  private static uint MacTime(DateTime t) {
    var seconds = (t.ToUniversalTime() - CompactProReader.MacEpoch).TotalSeconds;
    return (uint)Math.Clamp(seconds, 0, uint.MaxValue);
  }

  private sealed class Node {
    public readonly SortedDictionary<string, Node> Folders = new(StringComparer.Ordinal);
    public readonly List<(Member File, uint Offset, uint RsrcPacked, uint DataPacked)> Files = [];
    public int Count => this.Files.Count + this.Folders.Values.Sum(f => 1 + f.Count);
  }

  public byte[] ToArray() {
    using var ms = new MemoryStream();
    this.WriteTo(ms);
    return ms.ToArray();
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    using var body = new MemoryStream();
    body.Write(new byte[8]); // header, patched below
    var root = new Node();
    Node FolderOf(string[] parts, int depth) {
      var node = root;
      for (var i = 0; i < depth; ++i) {
        if (!node.Folders.TryGetValue(parts[i], out var next))
          node.Folders[parts[i]] = next = new Node();
        node = next;
      }
      return node;
    }
    foreach (var folder in this._folders.OrderBy(f => f, StringComparer.Ordinal)) {
      var parts = folder.Split('/');
      FolderOf(parts, parts.Length);
    }
    foreach (var file in this._files) {
      var offset = checked((uint)body.Position);
      var rsrc = file.Resource.Length == 0 ? [] : CompactProCodec.EncodeRle(file.Resource);
      var data = file.Data.Length == 0 ? [] : CompactProCodec.EncodeRle(file.Data);
      body.Write(rsrc);
      body.Write(data);
      FolderOf(file.Path, file.Path.Length - 1).Files.Add((file, offset, (uint)rsrc.Length, (uint)data.Length));
    }

    var dirOffset = checked((uint)body.Position);
    using var dir = new MemoryStream();
    var b2 = new byte[2];
    var b4 = new byte[4];
    BinaryPrimitives.WriteUInt16BigEndian(b2, checked((ushort)root.Count));
    dir.Write(b2);
    dir.WriteByte(0); // no comment

    void WriteNode(Node node) {
      foreach (var (file, offset, rsrcPacked, dataPacked) in node.Files) {
        var name = CompactProReader.MacRoman.GetBytes(file.Path[^1]);
        dir.WriteByte((byte)name.Length);
        dir.Write(name);
        dir.WriteByte(1); // volume
        void U32(uint v) { BinaryPrimitives.WriteUInt32BigEndian(b4, v); dir.Write(b4); }
        U32(offset);
        U32(file.Type);
        U32(file.Creator);
        U32(MacTime(file.Modified));
        U32(MacTime(file.Modified));
        BinaryPrimitives.WriteUInt16BigEndian(b2, 0); dir.Write(b2); // Finder flags
        var all = new byte[file.Resource.Length + file.Data.Length];
        file.Resource.CopyTo(all, 0);
        file.Data.CopyTo(all, file.Resource.Length);
        U32(CompactProReader.Crc(all));
        BinaryPrimitives.WriteUInt16BigEndian(b2, 0); dir.Write(b2); // flags: RLE only
        U32((uint)file.Resource.Length);
        U32((uint)file.Data.Length);
        U32(rsrcPacked);
        U32(dataPacked);
      }
      foreach (var (name, child) in node.Folders) {
        var bytes = CompactProReader.MacRoman.GetBytes(name);
        dir.WriteByte((byte)(bytes.Length | 0x80));
        dir.Write(bytes);
        BinaryPrimitives.WriteUInt16BigEndian(b2, checked((ushort)child.Count));
        dir.Write(b2);
        WriteNode(child);
      }
    }
    WriteNode(root);

    var dirBytes = dir.ToArray();
    BinaryPrimitives.WriteUInt32BigEndian(b4, CompactProReader.Crc(dirBytes));
    body.Write(b4);
    body.Write(dirBytes);

    var image = body.ToArray();
    image[0] = 1;   // marker
    image[1] = 1;   // volume
    // bytes 2..3: the "xmagic" field; readers ignore it
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4), dirOffset);
    output.Write(image);
  }
}
