using System.Buffers.Binary;
using System.Text;

namespace FileFormat.U8;

/// <summary>
/// Reads a Nintendo U8 archive (Wii / Wii U / 3DS / Switch). All multi-byte integers
/// are big-endian. The directory tree is encoded depth-first using parent indices and
/// exclusive end-index markers — see <see cref="WalkTree"/>.
/// </summary>
public sealed class U8Reader : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets the offset of the first node, as declared in the header.</summary>
  public uint FirstNodeOffset { get; }

  /// <summary>Gets the combined size of node table + string table, as declared in the header.</summary>
  public uint NodeTableSize { get; }

  /// <summary>Gets the offset where file data starts, as declared in the header.</summary>
  public uint DataOffset { get; }

  /// <summary>Gets the entries in the archive, with full <c>/</c>-separated paths.</summary>
  public IReadOnlyList<U8Entry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="U8Reader"/> from a stream.
  /// </summary>
  public U8Reader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < U8Constants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid U8 archive.");

    Span<byte> header = stackalloc byte[U8Constants.HeaderSize];
    this._stream.Position = 0;
    ReadExact(header);

    if (!header[..4].SequenceEqual(U8Constants.Magic))
      throw new InvalidDataException("Invalid U8 magic.");

    this.FirstNodeOffset = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
    this.NodeTableSize = BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);
    this.DataOffset = BinaryPrimitives.ReadUInt32BigEndian(header[12..16]);

    if (this.FirstNodeOffset < U8Constants.HeaderSize)
      throw new InvalidDataException($"U8 first-node offset {this.FirstNodeOffset} overlaps the header.");
    if ((long)this.FirstNodeOffset + U8Constants.NodeSize > stream.Length)
      throw new InvalidDataException("U8 first-node offset is past end-of-stream.");

    // Read the root node first to learn the total node count (encoded in its Size field).
    this._stream.Position = this.FirstNodeOffset;
    Span<byte> rootBuf = stackalloc byte[U8Constants.NodeSize];
    ReadExact(rootBuf);

    if (rootBuf[0] != U8Constants.TypeDirectory)
      throw new InvalidDataException("U8 root node is not a directory.");

    var nodeCount = (int)BinaryPrimitives.ReadUInt32BigEndian(rootBuf[8..12]);
    if (nodeCount < 1)
      throw new InvalidDataException($"U8 declares non-positive node count {nodeCount}.");

    var nodeTableBytes = (long)nodeCount * U8Constants.NodeSize;
    if (this.FirstNodeOffset + nodeTableBytes > stream.Length)
      throw new InvalidDataException($"U8 node table ({nodeCount} entries) extends past end-of-stream.");

    var rawNodes = new RawNode[nodeCount];
    rawNodes[0] = new RawNode(
      rootBuf[0],
      ReadUInt24BigEndian(rootBuf[1..4]),
      BinaryPrimitives.ReadUInt32BigEndian(rootBuf[4..8]),
      BinaryPrimitives.ReadUInt32BigEndian(rootBuf[8..12])
    );

    Span<byte> nodeBuf = stackalloc byte[U8Constants.NodeSize];
    for (var i = 1; i < nodeCount; ++i) {
      ReadExact(nodeBuf);
      rawNodes[i] = new RawNode(
        nodeBuf[0],
        ReadUInt24BigEndian(nodeBuf[1..4]),
        BinaryPrimitives.ReadUInt32BigEndian(nodeBuf[4..8]),
        BinaryPrimitives.ReadUInt32BigEndian(nodeBuf[8..12])
      );
    }

    // String table sits immediately after the last node and runs to (FirstNodeOffset + NodeTableSize).
    var stringTableStart = (long)this.FirstNodeOffset + nodeTableBytes;
    var stringTableEnd = (long)this.FirstNodeOffset + this.NodeTableSize;
    if (stringTableEnd < stringTableStart || stringTableEnd > stream.Length)
      throw new InvalidDataException("U8 string table bounds are invalid.");

    var stringTableLen = checked((int)(stringTableEnd - stringTableStart));
    var stringTable = new byte[stringTableLen];
    if (stringTableLen > 0) {
      this._stream.Position = stringTableStart;
      ReadExact(stringTable);
    }

    this.Entries = WalkTree(rawNodes, stringTable);
  }

  /// <summary>Extracts the raw bytes for a file entry.</summary>
  public byte[] Extract(U8Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      throw new InvalidOperationException("Cannot extract data from a directory entry.");
    if (entry.Size == 0)
      return [];

    this._stream.Position = entry.Offset;
    var data = new byte[entry.Size];
    ReadExact(data);
    return data;
  }

  // The U8 tree is encoded as a flat depth-first array. For a directory at index `i`, the
  // children are nodes (i+1 .. Size_i - 1). We walk recursively, accumulating directory
  // names into a path stack to produce full /-separated paths.
  private static List<U8Entry> WalkTree(RawNode[] nodes, byte[] stringTable) {
    var entries = new List<U8Entry>(nodes.Length);
    var pathStack = new List<string>();
    Walk(0, (int)nodes[0].Size, nodes, stringTable, pathStack, entries, isRoot: true);
    return entries;
  }

  private static void Walk(int startIdx, int endIdx, RawNode[] nodes, byte[] stringTable, List<string> pathStack, List<U8Entry> output, bool isRoot) {
    if (endIdx > nodes.Length)
      throw new InvalidDataException($"U8 directory end-index {endIdx} exceeds node count {nodes.Length}.");

    var dir = nodes[startIdx];
    var dirName = isRoot ? "" : ReadCString(stringTable, (int)dir.NameOffset);

    // Root carries the empty name and is not emitted as an entry — emitting it would
    // produce a trailing-slash root path that downstream consumers don't expect.
    if (!isRoot) {
      pathStack.Add(dirName);
      output.Add(new U8Entry {
        Name = string.Join('/', pathStack),
        Offset = 0,
        Size = 0,
        IsDirectory = true,
      });
    }

    var i = startIdx + 1;
    while (i < endIdx) {
      var node = nodes[i];
      if (node.Type == U8Constants.TypeDirectory) {
        var childEnd = (int)node.Size;
        if (childEnd <= i || childEnd > endIdx)
          throw new InvalidDataException($"U8 child directory at {i} has invalid end-index {childEnd}.");
        Walk(i, childEnd, nodes, stringTable, pathStack, output, isRoot: false);
        i = childEnd;
      } else if (node.Type == U8Constants.TypeFile) {
        var fileName = ReadCString(stringTable, (int)node.NameOffset);
        var fullPath = pathStack.Count == 0 ? fileName : string.Join('/', pathStack) + "/" + fileName;
        output.Add(new U8Entry {
          Name = fullPath,
          Offset = node.DataOffset,
          Size = node.Size,
          IsDirectory = false,
        });
        ++i;
      } else
        throw new InvalidDataException($"U8 node at {i} has unknown type 0x{node.Type:X2}.");
    }

    if (!isRoot)
      pathStack.RemoveAt(pathStack.Count - 1);
  }

  // Three-byte big-endian: high byte first, low byte last.
  private static uint ReadUInt24BigEndian(ReadOnlySpan<byte> span)
    => ((uint)span[0] << 16) | ((uint)span[1] << 8) | span[2];

  private static string ReadCString(byte[] buffer, int offset) {
    if (offset < 0 || offset >= buffer.Length)
      return "";
    var end = offset;
    while (end < buffer.Length && buffer[end] != 0)
      ++end;
    return Encoding.UTF8.GetString(buffer, offset, end - offset);
  }

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of U8 stream.");
      totalRead += read;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  private readonly record struct RawNode(byte Type, uint NameOffset, uint DataOffset, uint Size);
}
