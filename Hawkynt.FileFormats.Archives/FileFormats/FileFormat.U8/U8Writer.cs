using System.Buffers.Binary;
using System.Text;

namespace FileFormat.U8;

/// <summary>
/// Creates a Nintendo U8 archive. Caller adds files by forward-slash-separated path
/// plus payload bytes; intermediate directories are inferred. <see cref="Finish"/>
/// builds the depth-first node table, packs the string table, and lays out file data.
/// </summary>
public sealed class U8Writer : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(string Path, byte[] Data)> _files = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="U8Writer"/>.
  /// </summary>
  public U8Writer(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanWrite)
      throw new ArgumentException("Stream must be writable.", nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds a file at <paramref name="path"/> (using <c>/</c> separators).
  /// </summary>
  public void AddEntry(string path, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);

    var normalized = path.Replace('\\', '/').TrimStart('/');
    if (normalized.Length == 0)
      throw new ArgumentException("Path must not be empty.", nameof(path));

    foreach (var segment in normalized.Split('/'))
      if (segment.Length == 0 || segment.Length > U8Constants.MaxNameLength)
        throw new ArgumentException($"Path segment is empty or too long (max {U8Constants.MaxNameLength}): '{path}'.", nameof(path));

    this._files.Add((normalized, data));
  }

  /// <summary>Finalizes and writes the archive.</summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    // Sort files lexicographically. This is conventional for U8 — many real archives
    // observe this ordering, and it keeps the output deterministic.
    var sortedFiles = this._files
      .OrderBy(f => f.Path, StringComparer.Ordinal)
      .ToList();

    // Build an in-memory tree, then linearize depth-first to produce the node array.
    var root = new TreeDir("");
    foreach (var (path, data) in sortedFiles) {
      var segments = path.Split('/');
      var dir = root;
      for (var i = 0; i < segments.Length - 1; ++i) {
        var name = segments[i];
        if (!dir.Subdirs.TryGetValue(name, out var child)) {
          child = new TreeDir(name);
          dir.Subdirs[name] = child;
        }
        dir = child;
      }
      dir.Files.Add(new TreeFile(segments[^1], data));
    }

    var nodes = new List<NodeRec>();
    Linearize(root, parentIndex: 0, nodes);

    // Build the string table. Names are NUL-terminated UTF-8, packed in node order.
    // The root keeps an empty name at offset 0 (standard U8 convention).
    using var stringTable = new MemoryStream();
    foreach (var node in nodes) {
      node.NameOffset = (uint)stringTable.Position;
      var bytes = Encoding.UTF8.GetBytes(node.Name);
      stringTable.Write(bytes);
      stringTable.WriteByte(0);
    }
    var stringTableBytes = stringTable.ToArray();

    // Layout: header (32) + nodes + string table, then 0x20-aligned data region.
    var firstNodeOffset = (uint)U8Constants.HeaderSize;
    var nodeTableSize = (uint)(nodes.Count * U8Constants.NodeSize + stringTableBytes.Length);
    var dataRegionStart = checked((long)firstNodeOffset + nodeTableSize);
    var dataOffset = AlignUp(dataRegionStart, U8Constants.DataAlignment);

    // Assign per-file data offsets in node order. Real U8 archives sometimes align each
    // file to 0x20 — we do the same so that consumers that mmap-align won't choke.
    var cursor = dataOffset;
    foreach (var node in nodes)
      if (!node.IsDirectory) {
        node.DataOffset = (uint)cursor;
        cursor += node.FileData!.Length;
        cursor = AlignUp(cursor, U8Constants.DataAlignment);
      }

    // Header
    Span<byte> header = stackalloc byte[U8Constants.HeaderSize];
    U8Constants.Magic.CopyTo(header[..4]);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..8], firstNodeOffset);
    BinaryPrimitives.WriteUInt32BigEndian(header[8..12], nodeTableSize);
    BinaryPrimitives.WriteUInt32BigEndian(header[12..16], (uint)dataOffset);
    // header[16..32] = reserved, already zero.
    this._stream.Position = 0;
    this._stream.Write(header);

    // Nodes
    Span<byte> nodeBuf = stackalloc byte[U8Constants.NodeSize];
    foreach (var node in nodes) {
      nodeBuf.Clear();
      nodeBuf[0] = node.IsDirectory ? U8Constants.TypeDirectory : U8Constants.TypeFile;
      WriteUInt24BigEndian(nodeBuf[1..4], node.NameOffset);
      BinaryPrimitives.WriteUInt32BigEndian(nodeBuf[4..8], node.DataOffset);
      BinaryPrimitives.WriteUInt32BigEndian(nodeBuf[8..12], node.Size);
      this._stream.Write(nodeBuf);
    }

    // String table
    if (stringTableBytes.Length > 0)
      this._stream.Write(stringTableBytes);

    // Pad to dataOffset
    while (this._stream.Position < dataOffset)
      this._stream.WriteByte(0);

    // File data — same alignment as we computed above
    foreach (var node in nodes)
      if (!node.IsDirectory) {
        while (this._stream.Position < node.DataOffset)
          this._stream.WriteByte(0);
        if (node.FileData!.Length > 0)
          this._stream.Write(node.FileData);
      }
  }

  private static void Linearize(TreeDir dir, int parentIndex, List<NodeRec> output) {
    var myIndex = output.Count;
    var dirRec = new NodeRec(dir.Name, IsDirectory: true) {
      DataOffset = (uint)parentIndex,
      // Size patched below once descendants are known.
    };
    output.Add(dirRec);

    // Files in this directory come first (sorted), then subdirectories (sorted) — depth-first.
    // The exact ordering doesn't matter for the format, but consistent ordering matters for
    // deterministic output.
    foreach (var file in dir.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
      output.Add(new NodeRec(file.Name, IsDirectory: false) {
        Size = (uint)file.Data.Length,
        FileData = file.Data,
      });

    foreach (var sub in dir.Subdirs.Values.OrderBy(d => d.Name, StringComparer.Ordinal))
      Linearize(sub, parentIndex: myIndex, output);

    // End-index is exclusive: it's the index of the first node OUTSIDE this directory.
    // For the root, this equals the total node count.
    dirRec.Size = (uint)output.Count;
  }

  private static long AlignUp(long value, int alignment)
    => (value + alignment - 1) & ~((long)alignment - 1);

  private static void WriteUInt24BigEndian(Span<byte> dest, uint value) {
    if (value > 0xFFFFFF)
      throw new InvalidDataException($"Name offset {value} exceeds 24-bit range.");
    dest[0] = (byte)(value >> 16);
    dest[1] = (byte)(value >> 8);
    dest[2] = (byte)value;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._finished)
      Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  private sealed class TreeDir(string name) {
    public string Name { get; } = name;
    public SortedDictionary<string, TreeDir> Subdirs { get; } = new(StringComparer.Ordinal);
    public List<TreeFile> Files { get; } = [];
  }

  private sealed record TreeFile(string Name, byte[] Data);

  private sealed class NodeRec(string name, bool IsDirectory) {
    public string Name { get; } = name;
    public bool IsDirectory { get; } = IsDirectory;
    public uint NameOffset { get; set; }
    public uint DataOffset { get; set; }
    public uint Size { get; set; }
    public byte[]? FileData { get; init; }
  }
}
