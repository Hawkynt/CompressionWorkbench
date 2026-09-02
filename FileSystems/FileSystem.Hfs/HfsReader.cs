#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Hfs;

/// <summary>
/// Reads the catalog of a Classic Macintosh HFS volume and extracts the files it holds.
/// </summary>
public sealed class HfsReader : IDisposable {
  private const ushort HfsMagic = 0x4244;
  private const int MdbOffset = 1024;

  // HFS catalog record types.
  private const byte RecFolder = 1;
  private const byte RecFile = 2;
  private const byte RecFolderThread = 3;
  private const byte RecFileThread = 4;

  private readonly byte[] _data;
  private readonly List<HfsEntry> _entries = [];

  private uint _blockSize;
  private int _firstBlockOffset; // byte offset of first allocation block (drAlBlSt × 512)
  private int _catalogStartBlock;
  private int _catalogBlockCount;

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<HfsEntry> Entries => this._entries;

  /// <summary>
  /// Initializes a new instance of <see cref="HfsReader"/>.
  /// </summary>
  public HfsReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < MdbOffset + 162)
      throw new InvalidDataException("HFS: image too small.");

    var sig = BinaryPrimitives.ReadUInt16BigEndian(this._data.AsSpan(MdbOffset));
    if (sig != HfsMagic)
      throw new InvalidDataException("HFS: invalid MDB signature.");

    this._blockSize = BinaryPrimitives.ReadUInt32BigEndian(this._data.AsSpan(MdbOffset + 20));
    if (this._blockSize == 0) this._blockSize = 512;
    var drAlBlSt = BinaryPrimitives.ReadUInt16BigEndian(this._data.AsSpan(MdbOffset + 28));
    this._firstBlockOffset = drAlBlSt * 512;

    // Catalog file extents at MDB + 150 (drCTExtRec[0]).
    this._catalogStartBlock = BinaryPrimitives.ReadUInt16BigEndian(this._data.AsSpan(MdbOffset + 150));
    this._catalogBlockCount = BinaryPrimitives.ReadUInt16BigEndian(this._data.AsSpan(MdbOffset + 152));

    if (this._catalogBlockCount == 0) return;

    var catalogOffset = this.BlockToOffset(this._catalogStartBlock);
    if (catalogOffset < 0 || catalogOffset + 512 > this._data.Length) return;

    this.ReadCatalogTree(catalogOffset);
  }

  private int BlockToOffset(int block) => this._firstBlockOffset + (int)(block * this._blockSize);

  // The volume root directory dirID per Inside Macintosh. The root-dir catalog
  // record is keyed under CNID 1 (RootParent); the root itself is dirID 2.
  private const uint CnidRootParent = 1;
  private const uint CnidRootDir = 2;

  private void ReadCatalogTree(int catalogBase) {
    if (catalogBase + 32 > this._data.Length) return;

    // Header node: node 0. Verify kind == 1.
    var headerKind = (sbyte)this._data[catalogBase + 8];
    if (headerKind != 1) return;

    // BTHdrRec at offset 14: bthDepth(2) + bthRoot(4) + bthNRecs(4) + bthFNode(4) + bthLNode(4) + bthNodeSize(2) + ...
    var hdr = this._data.AsSpan(catalogBase + 14);
    var firstLeaf = BinaryPrimitives.ReadUInt32BigEndian(hdr[10..]);
    var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(hdr[18..]);
    if (nodeSize == 0) nodeSize = 512;

    // First pass: collect every folder and file record with its parent dirID so
    // we can reconstruct full nested paths from the (parentDirID, CName) keying.
    var folderNamesByDirId = new Dictionary<uint, (uint Parent, string Name)>();
    var pendingFolders = new List<(uint DirId, uint Parent, string Name)>();
    var pendingFiles = new List<(uint Parent, string Name, uint Size, ushort Start, ushort Blocks)>();

    var node = (int)firstLeaf;
    var visited = new HashSet<int>();
    while (node != 0 && visited.Add(node)) {
      var nodeOffset = catalogBase + node * nodeSize;
      if (nodeOffset + nodeSize > this._data.Length) break;

      var nodeKind = (sbyte)this._data[nodeOffset + 8];
      if (nodeKind != -1) break; // not a leaf

      var numRecords = BinaryPrimitives.ReadUInt16BigEndian(this._data.AsSpan(nodeOffset + 10));

      for (var r = 0; r < numRecords; r++) {
        var recOffsetPos = nodeOffset + nodeSize - 2 * (r + 1);
        if (recOffsetPos < nodeOffset) break;
        var recOffset = BinaryPrimitives.ReadUInt16BigEndian(this._data.AsSpan(recOffsetPos));
        var recPos = nodeOffset + recOffset;
        if (recPos + 8 > this._data.Length) continue;

        // Parse catalog key: keyLen(1) + resrv1(1) + parentID(4) + nameLen(1) + name
        var keyLen = this._data[recPos];
        if (keyLen < 6) continue;
        var parentDirId = BinaryPrimitives.ReadUInt32BigEndian(this._data.AsSpan(recPos + 2));
        var nameLen = this._data[recPos + 6];
        if (recPos + 7 + nameLen > this._data.Length) continue;
        var name = nameLen > 0
          ? Encoding.Latin1.GetString(this._data, recPos + 7, nameLen)
          : "";

        // Record data starts after key, aligned up to even boundary.
        var dataPos = recPos + 1 + keyLen;
        if ((dataPos & 1) != 0) dataPos++;
        if (dataPos + 2 > this._data.Length) continue;

        var recType = this._data[dataPos];

        switch (recType) {
          case RecFile when !string.IsNullOrEmpty(name): {
            // File record per Inside Macintosh: data-fork extent at offset 74 into record data.
            //   filLgLen at offset 26, filStBlk at offset 24.
            // But we prefer the first extent descriptor (filExtRec[0]) at offset 74.
            if (dataPos + 78 > this._data.Length) continue;
            var filLgLen = BinaryPrimitives.ReadUInt32BigEndian(this._data.AsSpan(dataPos + 26));
            var extStart = BinaryPrimitives.ReadUInt16BigEndian(this._data.AsSpan(dataPos + 74));
            var extBlocks = BinaryPrimitives.ReadUInt16BigEndian(this._data.AsSpan(dataPos + 76));
            pendingFiles.Add((parentDirId, name, filLgLen, extStart, extBlocks));
            break;
          }
          case RecFolder when !string.IsNullOrEmpty(name): {
            // Folder record: dirDirID at offset 6 into the record data. The root
            // directory itself (keyed under RootParent) is recorded so it can
            // anchor the path walk, but is not surfaced as a directory entry.
            if (dataPos + 10 > this._data.Length) continue;
            var dirId = BinaryPrimitives.ReadUInt32BigEndian(this._data.AsSpan(dataPos + 6));
            folderNamesByDirId[dirId] = (parentDirId, name);
            if (parentDirId != CnidRootParent)
              pendingFolders.Add((dirId, parentDirId, name));
            break;
          }
          case RecFolderThread:
          case RecFileThread:
            // Threads are internal cross-references; ignore.
            break;
        }
      }

      // Follow fLink to next leaf.
      node = (int)BinaryPrimitives.ReadUInt32BigEndian(this._data.AsSpan(nodeOffset));
    }

    // Second pass: resolve each record's full nested path from its parent chain.
    foreach (var (dirId, parent, name) in pendingFolders) {
      _ = dirId;
      this._entries.Add(new HfsEntry {
        Name = this.ResolvePath(folderNamesByDirId, parent, name),
        IsDirectory = true,
      });
    }
    foreach (var (parent, name, size, start, blocks) in pendingFiles) {
      this._entries.Add(new HfsEntry {
        Name = this.ResolvePath(folderNamesByDirId, parent, name),
        Size = size,
        IsDirectory = false,
        StartBlock = start,
        BlockCount = blocks,
      });
    }
  }

  /// <summary>
  /// Builds the full slash-separated path of an entry from its parent dirID
  /// chain, stopping at the volume root (dirID 2). The volume name itself is not
  /// part of any entry path.
  /// </summary>
  private string ResolvePath(
    Dictionary<uint, (uint Parent, string Name)> folderNamesByDirId,
    uint parentDirId, string leafName) {
    var parts = new List<string> { leafName };
    var current = parentDirId;
    var guard = 0;
    while (current != CnidRootDir && current != CnidRootParent && guard++ < 256) {
      if (!folderNamesByDirId.TryGetValue(current, out var folder)) break;
      parts.Add(folder.Name);
      current = folder.Parent;
    }
    parts.Reverse();
    return string.Join('/', parts);
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(HfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.BlockCount == 0) return [];
    var offset = this.BlockToOffset(entry.StartBlock);
    if (offset < 0 || offset >= this._data.Length) return [];
    var len = (int)Math.Min(entry.Size, (long)entry.BlockCount * this._blockSize);
    if (offset + len > this._data.Length) len = this._data.Length - offset;
    return this._data.AsSpan(offset, len).ToArray();
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
