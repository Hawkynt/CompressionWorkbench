using System.Buffers.Binary;
using System.Text;

namespace FileFormat.HfsPlus;

/// <summary>
/// Reads and extracts files from an HFS+ filesystem image.
/// Supports both HFS+ (signature "H+") and HFSX (signature "HX") volumes.
/// The volume header resides at byte offset 1024 within the image.
/// </summary>
public sealed class HfsPlusReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly byte[] _data;
  private bool _disposed;

  // Volume header fields.
  private readonly uint _blockSize;
  private readonly uint _totalBlocks;

  // Catalog file extent (first extent only for simplicity).
  private readonly uint _catalogStartBlock;
  private readonly uint _catalogBlockCount;

  private const int VolumeHeaderOffset = 1024;
  private const int VolumeHeaderSize = 512;
  private const ushort HfsPlusSignature = 0x482B; // "H+"
  private const ushort HfsxSignature = 0x4858;    // "HX"

  // HFS+ epoch: 1904-01-01T00:00:00Z.
  private static readonly DateTime HfsEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  /// <summary>Gets all file and directory entries found in the volume.</summary>
  public IReadOnlyList<HfsPlusEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="HfsPlusReader"/> and parses the HFS+ volume.
  /// </summary>
  /// <param name="stream">A stream containing the HFS+ image.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public HfsPlusReader(Stream stream, bool leaveOpen = false) {
    _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    _leaveOpen = leaveOpen;

    // Read entire image into memory.
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();

    if (_data.Length < VolumeHeaderOffset + VolumeHeaderSize)
      throw new InvalidDataException("Stream too small for an HFS+ volume header.");

    var vh = _data.AsSpan(VolumeHeaderOffset);

    // Validate signature.
    var sig = BinaryPrimitives.ReadUInt16BigEndian(vh);
    if (sig != HfsPlusSignature && sig != HfsxSignature)
      throw new InvalidDataException($"Invalid HFS+ signature: 0x{sig:X4}");

    // Parse volume header fields.
    _blockSize = BinaryPrimitives.ReadUInt32BigEndian(vh[40..]);
    _totalBlocks = BinaryPrimitives.ReadUInt32BigEndian(vh[44..]);

    if (_blockSize == 0)
      throw new InvalidDataException("HFS+ block size is zero.");

    // Catalog file: first extent descriptor at offset 272.
    // Extent descriptors: startBlock (uint32 BE) + blockCount (uint32 BE).
    _catalogStartBlock = BinaryPrimitives.ReadUInt32BigEndian(vh[272..]);
    _catalogBlockCount = BinaryPrimitives.ReadUInt32BigEndian(vh[276..]);

    // Parse catalog B-tree.
    var entries = new List<HfsPlusEntry>();
    ParseCatalog(entries);
    Entries = entries;
  }

  // ── Catalog B-tree parsing ──────────────────────────────────────────────

  private void ParseCatalog(List<HfsPlusEntry> entries) {
    if (_catalogBlockCount == 0 || _catalogStartBlock == 0)
      return;

    var catalogOffset = (long)_catalogStartBlock * _blockSize;
    var catalogSize = (long)_catalogBlockCount * _blockSize;
    if (catalogOffset + catalogSize > _data.Length)
      catalogSize = _data.Length - catalogOffset;
    if (catalogSize <= 0)
      return;

    // B-tree header node is at node 0 (start of catalog file).
    // Node descriptor: 14 bytes.
    // Header record starts at offset 14 within node 0.
    var nodeBase = catalogOffset;
    if (nodeBase + 14 + 30 > _data.Length) return;

    var nodeSpan = _data.AsSpan((int)nodeBase);

    // Node descriptor fields.
    // var fLink = BinaryPrimitives.ReadUInt32BigEndian(nodeSpan);
    // kind at offset 8 (int8): 1 = header node
    var kind = (sbyte)nodeSpan[8];
    if (kind != 1) return; // Not a header node.

    // Header record at offset 14.
    var hdr = nodeSpan[14..];
    // treeDepth: uint16 BE at 0
    // rootNode: uint32 BE at 2
    // leafRecords: uint32 BE at 6
    var firstLeafNode = BinaryPrimitives.ReadUInt32BigEndian(hdr[18..]);
    // lastLeafNode: uint32 BE at 22
    var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(hdr[26..]);

    if (nodeSize == 0) return;

    // Build a CNID-to-path map for directory resolution.
    // Root folder CNID = 2.
    var dirPaths = new Dictionary<uint, string> { [2] = "" };

    // Walk all leaf nodes.
    var currentNode = firstLeafNode;
    var visited = new HashSet<uint>();

    while (currentNode != 0 && visited.Add(currentNode)) {
      var nodeOffset = catalogOffset + (long)currentNode * nodeSize;
      if (nodeOffset + nodeSize > _data.Length) break;

      var nd = _data.AsSpan((int)nodeOffset);
      var ndKind = (sbyte)nd[8];
      if (ndKind != -1) {
        // Not a leaf node; stop.
        break;
      }

      var numRecords = BinaryPrimitives.ReadUInt16BigEndian(nd[10..]);

      // Record offsets are stored at the end of the node, in reverse order (uint16 BE each).
      // Offset[0] is at nodeSize - 2, Offset[1] at nodeSize - 4, etc.
      for (var i = 0; i < numRecords; i++) {
        var offsetPos = (int)nodeSize - 2 * (i + 1);
        if (offsetPos < 12) break;
        var recOffset = BinaryPrimitives.ReadUInt16BigEndian(nd[offsetPos..]);
        if (recOffset + 6 > nodeSize) continue;

        var rec = nd[recOffset..];

        // Catalog key: keyLength (uint16 BE), parentCNID (uint32 BE), name length (uint16 BE), UTF-16BE chars.
        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(rec);
        if (keyLength < 6) continue;
        var parentCnid = BinaryPrimitives.ReadUInt32BigEndian(rec[2..]);
        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(rec[6..]);

        // Name starts at offset 8 within the key, each char is 2 bytes (UTF-16BE).
        var nameByteLen = nameLength * 2;
        if (8 + nameByteLen > recOffset + 2 + keyLength + 100) {
          // Sanity check: name too long.
          nameLength = 0;
        }

        var name = "";
        if (nameLength > 0 && recOffset + 8 + nameByteLen <= nodeSize) {
          var nameBytes = _data.AsSpan((int)nodeOffset + recOffset + 8, nameByteLen);
          name = Encoding.BigEndianUnicode.GetString(nameBytes);
        }

        // Data record follows the key: aligned to 2-byte boundary.
        var dataOffset = recOffset + 2 + keyLength;
        if ((dataOffset & 1) != 0) dataOffset++; // Pad to even.
        if (dataOffset + 2 > nodeSize) continue;

        var recordType = BinaryPrimitives.ReadInt16BigEndian(nd[dataOffset..]);

        switch (recordType) {
          case 1: // Folder record.
            ParseFolderRecord(nd, dataOffset, parentCnid, name, dirPaths, entries);
            break;
          case 2: // File record.
            ParseFileRecord(nd, dataOffset, parentCnid, name, dirPaths, entries);
            break;
          // 3 = folder thread, 4 = file thread — skip.
        }
      }

      // Advance to next leaf node via fLink.
      currentNode = BinaryPrimitives.ReadUInt32BigEndian(nd);
    }
  }

  private static void ParseFolderRecord(ReadOnlySpan<byte> nd, int dataOffset, uint parentCnid,
      string name, Dictionary<uint, string> dirPaths, List<HfsPlusEntry> entries) {
    // Folder record layout:
    // offset 0: recordType (int16 BE) = 1
    // offset 2: flags (uint16 BE)
    // offset 4: valence (uint32 BE)
    // offset 8: CNID (uint32 BE)
    // offset 12: createDate (uint32 BE)
    // offset 16: contentModDate (uint32 BE)
    if (dataOffset + 20 > nd.Length) return;

    var cnid = BinaryPrimitives.ReadUInt32BigEndian(nd[(dataOffset + 8)..]);
    var modDateRaw = BinaryPrimitives.ReadUInt32BigEndian(nd[(dataOffset + 16)..]);
    var modDate = modDateRaw > 0 ? HfsEpoch.AddSeconds(modDateRaw) : (DateTime?)null;

    var parentPath = dirPaths.GetValueOrDefault(parentCnid, "");
    var fullPath = parentPath.Length > 0 ? parentPath + "/" + name : name;

    dirPaths[cnid] = fullPath;

    // Skip root folder itself (CNID 2 with empty name or parent CNID 1).
    if (parentCnid == 1) return;

    entries.Add(new HfsPlusEntry {
      Name = name,
      FullPath = fullPath,
      Size = 0,
      IsDirectory = true,
      Cnid = cnid,
      LastModified = modDate,
    });
  }

  private static void ParseFileRecord(ReadOnlySpan<byte> nd, int dataOffset, uint parentCnid,
      string name, Dictionary<uint, string> dirPaths, List<HfsPlusEntry> entries) {
    // File record layout:
    // offset 0: recordType (int16 BE) = 2
    // offset 2: flags (uint16 BE)
    // offset 4: reserved (uint32 BE)
    // offset 8: CNID (uint32 BE)
    // offset 12: createDate (uint32 BE)
    // offset 16: contentModDate (uint32 BE)
    // ...
    // offset 70: data fork logical size (uint64 BE)
    // offset 78: data fork extent[0].startBlock (uint32 BE)
    // offset 82: data fork extent[0].blockCount (uint32 BE)
    if (dataOffset + 86 > nd.Length) return;

    var cnid = BinaryPrimitives.ReadUInt32BigEndian(nd[(dataOffset + 8)..]);
    var modDateRaw = BinaryPrimitives.ReadUInt32BigEndian(nd[(dataOffset + 16)..]);
    var modDate = modDateRaw > 0 ? HfsEpoch.AddSeconds(modDateRaw) : (DateTime?)null;
    var logicalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(nd[(dataOffset + 70)..]);
    var startBlock = BinaryPrimitives.ReadUInt32BigEndian(nd[(dataOffset + 78)..]);
    var blockCount = BinaryPrimitives.ReadUInt32BigEndian(nd[(dataOffset + 82)..]);

    var parentPath = dirPaths.GetValueOrDefault(parentCnid, "");
    var fullPath = parentPath.Length > 0 ? parentPath + "/" + name : name;

    entries.Add(new HfsPlusEntry {
      Name = name,
      FullPath = fullPath,
      Size = logicalSize,
      IsDirectory = false,
      Cnid = cnid,
      LastModified = modDate,
      FirstBlock = startBlock,
      BlockCount = blockCount,
    });
  }

  // ── File extraction ─────────────────────────────────────────────────────

  /// <summary>
  /// Extracts the data fork content of the specified file entry.
  /// </summary>
  /// <param name="entry">The file entry to extract.</param>
  /// <returns>The file data as a byte array.</returns>
  public byte[] Extract(HfsPlusEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.Size == 0) return [];

    var offset = (long)entry.FirstBlock * _blockSize;
    var length = (int)Math.Min(entry.Size, (long)entry.BlockCount * _blockSize);
    length = (int)Math.Min(length, entry.Size);

    if (offset + length > _data.Length)
      length = (int)Math.Max(0, _data.Length - offset);
    if (length <= 0) return [];

    var result = new byte[entry.Size];
    var toCopy = (int)Math.Min(length, entry.Size);
    Buffer.BlockCopy(_data, (int)offset, result, 0, toCopy);
    return result;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!_disposed) {
      _disposed = true;
      if (!_leaveOpen)
        _stream.Dispose();
    }
  }
}
