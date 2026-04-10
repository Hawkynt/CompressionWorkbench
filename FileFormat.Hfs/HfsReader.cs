#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Hfs;

public sealed class HfsReader : IDisposable {
  private const ushort HfsMagic = 0x4244;
  private const int MdbOffset = 1024;
  private const int NodeSize = 512;

  private readonly byte[] _data;
  private readonly List<HfsEntry> _entries = [];

  private uint _blockSize;
  private int _firstBlockOffset; // byte offset of first allocation block
  private int _catalogStartBlock;
  private int _catalogBlockCount;

  public IReadOnlyList<HfsEntry> Entries => _entries;

  public HfsReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < MdbOffset + 162)
      throw new InvalidDataException("HFS: image too small.");

    var sig = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(MdbOffset));
    if (sig != HfsMagic)
      throw new InvalidDataException("HFS: invalid MDB signature.");

    _blockSize = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(MdbOffset + 20));
    if (_blockSize == 0) _blockSize = 512;
    var firstAllocBlock = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(MdbOffset + 28));
    _firstBlockOffset = firstAllocBlock * 512;

    // Catalog file extent record at MDB + 78
    _catalogStartBlock = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(MdbOffset + 78));
    _catalogBlockCount = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(MdbOffset + 80));

    if (_catalogBlockCount == 0) return;

    // Read catalog B-tree
    var catalogOffset = BlockToOffset(_catalogStartBlock);
    if (catalogOffset < 0 || catalogOffset + NodeSize > _data.Length) return;

    // Header node is node 0
    ReadCatalogTree(catalogOffset);
  }

  private int BlockToOffset(int block) => _firstBlockOffset + (int)(block * _blockSize);

  private void ReadCatalogTree(int catalogBase) {
    // Header node at offset 0
    if (catalogBase + NodeSize > _data.Length) return;

    // Parse header node to find first leaf
    var kind = (sbyte)_data[catalogBase + 8];
    if (kind != 2) return; // not header node

    // Header record at offset 14: treeDepth(2) + rootNode(4) + leafRecords(4) + firstLeaf(4) + lastLeaf(4)
    var firstLeaf = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(catalogBase + 14 + 14));

    // Traverse leaf nodes
    var node = (int)firstLeaf;
    var visited = new HashSet<int>();
    while (node != 0 && visited.Add(node)) {
      var nodeOffset = catalogBase + node * NodeSize;
      if (nodeOffset + NodeSize > _data.Length) break;

      var nodeKind = (sbyte)_data[nodeOffset + 8];
      if (nodeKind != -1 && nodeKind != 0) break; // not a leaf

      var numRecords = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(nodeOffset + 10));

      for (int r = 0; r < numRecords; r++) {
        // Record offset is stored at end of node, uint16 BE
        var recOffsetPos = nodeOffset + NodeSize - 2 * (r + 1);
        if (recOffsetPos < nodeOffset) break;
        var recOffset = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(recOffsetPos));
        var recPos = nodeOffset + recOffset;

        if (recPos + 8 > _data.Length) continue;

        // Parse catalog key
        var keyLen = _data[recPos];
        if (keyLen < 6) continue;
        var parentDirId = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(recPos + 2));
        var nameLen = _data[recPos + 6];
        if (recPos + 7 + nameLen > _data.Length) continue;
        var name = Encoding.Latin1.GetString(_data, recPos + 7, nameLen);

        // Record data starts after key (aligned to even boundary)
        var dataPos = recPos + 1 + keyLen;
        if ((dataPos & 1) != 0) dataPos++;
        if (dataPos + 2 > _data.Length) continue;

        var recType = (sbyte)_data[dataPos];

        if (recType == 2 && !string.IsNullOrEmpty(name)) {
          // File record
          // Data fork first extent at dataPos + 16: startBlock(2) + numBlocks(2)
          if (dataPos + 20 > _data.Length) continue;
          var fileDataStart = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(dataPos + 16));
          var fileDataBlocks = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(dataPos + 18));
          // Logical EOF at dataPos + 22 (uint32 BE)
          long logicalSize = 0;
          if (dataPos + 26 <= _data.Length)
            logicalSize = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(dataPos + 22));

          _entries.Add(new HfsEntry {
            Name = name,
            Size = logicalSize,
            IsDirectory = false,
            StartBlock = fileDataStart,
            BlockCount = fileDataBlocks,
          });
        } else if (recType == 1 && !string.IsNullOrEmpty(name)) {
          // Directory record
          _entries.Add(new HfsEntry {
            Name = name,
            IsDirectory = true,
          });
        }
      }

      // Follow fLink to next leaf
      node = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(nodeOffset));
    }
  }

  public byte[] Extract(HfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.BlockCount == 0) return [];
    var offset = BlockToOffset(entry.StartBlock);
    if (offset < 0 || offset >= _data.Length) return [];
    var len = (int)Math.Min(entry.Size, (long)entry.BlockCount * _blockSize);
    if (offset + len > _data.Length) len = _data.Length - offset;
    return _data.AsSpan(offset, len).ToArray();
  }

  public void Dispose() { }
}
