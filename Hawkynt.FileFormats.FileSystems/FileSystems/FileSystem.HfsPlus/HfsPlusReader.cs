using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.HfsPlus;

/// <summary>
/// Reads and extracts files from an HFS+ filesystem image.
/// Supports both HFS+ (signature "H+") and HFSX (signature "HX") volumes.
/// The volume header resides at byte offset 1024 within the image.
/// </summary>
public sealed class HfsPlusReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  /// <summary>
  /// Random-access view over the volume. Copying it into a byte[] capped the
  /// reader at the array limit, which HFS+'s 64-bit fork sizes do not.
  /// </summary>
  private readonly ImageAccessor _data;
  private bool _disposed;

  // Volume header fields.
  private readonly uint _blockSize;
  private readonly uint _totalBlocks;

  // Catalog file extent (first extent only for simplicity).
  private readonly uint _catalogStartBlock;
  private readonly uint _catalogBlockCount;
  private readonly uint _extentsStartBlock;
  private readonly uint _extentsBlockCount;
  private Dictionary<(uint, byte), List<(uint StartBlock, uint Count, uint FileStart)>>? _overflow;

  private const int VolumeHeaderOffset = 1024;
  private const int VolumeHeaderSize = 512;
  private const ushort HfsPlusSignature = 0x482B; // "H+"
  private const ushort HfsxSignature = 0x4858;    // "HX"

  // HFS+ epoch: 1904-01-01T00:00:00Z.
  private static readonly DateTime HfsEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  /// <summary>
  /// The volume's files and folders as a POSIX system shows them: names with ':' where the
  /// catalog stores '/', hard links carrying their shared data, and the metadata directories and
  /// journal files left out.
  /// </summary>
  public IReadOnlyList<HfsPlusEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="HfsPlusReader"/> and parses the HFS+ volume.
  /// </summary>
  /// <param name="stream">A stream containing the HFS+ image.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public HfsPlusReader(Stream stream, bool leaveOpen = false) {
    _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    _leaveOpen = leaveOpen;

    if (stream.CanSeek) stream.Position = 0;
    _data = new ImageAccessor(stream, leaveOpen: true);

    if (_data.Length < VolumeHeaderOffset + VolumeHeaderSize)
      throw new InvalidDataException("Stream too small for an HFS+ volume header.");

    var vh = _data.Read(VolumeHeaderOffset, VolumeHeaderSize).AsSpan();

    // Validate signature.
    var sig = BinaryPrimitives.ReadUInt16BigEndian(vh);
    if (sig != HfsPlusSignature && sig != HfsxSignature)
      throw new InvalidDataException($"Invalid HFS+ signature: 0x{sig:X4}");

    // Parse volume header fields.
    _blockSize = BinaryPrimitives.ReadUInt32BigEndian(vh[40..]);
    _totalBlocks = BinaryPrimitives.ReadUInt32BigEndian(vh[44..]);

    if (_blockSize == 0)
      throw new InvalidDataException("HFS+ block size is zero.");

    // Catalog file ForkData starts at offset 272 (TN1150 §3.2).
    // Layout: logicalSize(u64) + clumpSize(u32) + totalBlocks(u32) + extents[8].
    // First extent: startBlock at offset 272+16=288, blockCount at 272+20=292.
    _catalogStartBlock = BinaryPrimitives.ReadUInt32BigEndian(vh[288..]);
    _catalogBlockCount = BinaryPrimitives.ReadUInt32BigEndian(vh[292..]);

    // The extents overflow file's first extent (ForkData at 192, extents at +16).
    _extentsStartBlock = BinaryPrimitives.ReadUInt32BigEndian(vh[208..]);
    _extentsBlockCount = BinaryPrimitives.ReadUInt32BigEndian(vh[212..]);

    // TN1150: kHFSVolumeJournaledBit (bit 13 of attributes at +4) says journalInfoBlock (+12)
    // names the block holding the journal info block.
    var attributes = BinaryPrimitives.ReadUInt32BigEndian(vh[4..]);
    _journalInfoBlock = (attributes & VolumeJournaledMask) != 0 ? BinaryPrimitives.ReadUInt32BigEndian(vh[12..]) : 0;

    // Parse catalog B-tree.
    var nodes = new List<CatalogNode>();
    ParseCatalog(nodes);
    AttachDecmpfs(nodes, vh[352..]);
    (Entries, AllFiles) = Resolve(nodes);
  }

  /// <summary>
  /// Every file record in the catalog as stored: hard links unresolved and the metadata
  /// directories' files included. What a check of the whole volume's contents compares, and
  /// what the layout map names.
  /// </summary>
  internal IReadOnlyList<HfsPlusEntry> AllFiles { get; }

  private readonly uint _journalInfoBlock;
  private const uint VolumeJournaledMask = 1u << 13;

  // TN1150 "Hard Links": the metadata directory holding the indirect node files, a child of the
  // root whose name starts with four U+0000; a hard link is a file record of type 'hlnk', creator
  // 'hfs+', whose permissions.special (iNodeNum) names the indirect node file "iNode<n>".
  private const string MetadataDirectoryName = "\0\0\0\0HFS+ Private Data";
  // Mac OS X 10.5 keeps the targets of directory hard links in a second metadata directory
  // (libfshfs documentation; xnu bsd/hfs HFSPLUS_DIR_METADATA_FOLDER).
  private const string DirectoryMetadataDirectoryName = ".HFS+ Private Directory Data\r";
  private const uint HardLinkFileType = 0x686C6E6B; // 'hlnk'
  private const uint HfsPlusCreator = 0x6866732B;   // 'hfs+'
  private const string IndirectNodePrefix = "iNode";
  private const uint RootFolderCnid = 2;

  /// <summary>One catalog leaf record of a folder or a file, as stored.</summary>
  private sealed record CatalogNode(uint Parent, string Name, bool IsFolder, uint Cnid, DateTime? Modified) {
    public uint FileType { get; init; }
    public uint Creator { get; init; }
    public uint Special { get; init; }
    public long Size { get; init; }
    public uint FirstBlock { get; init; }
    public uint BlockCount { get; init; }
    public IReadOnlyList<(uint StartBlock, uint BlockCount)> Extents { get; init; } = [];
    public bool IsSymlink { get; init; }
    public string? LinkTarget { get; init; }
    public bool IsCompressed { get; init; }
    public long ResourceSize { get; init; }
    public IReadOnlyList<(uint StartBlock, uint BlockCount)> ResourceExtents { get; init; } = [];
    public byte[]? Decmpfs { get; init; }
  }

  private const string DecmpfsAttributeName = "com.apple.decmpfs";

  /// <summary>
  /// Gives every file flagged UF_COMPRESSED its "com.apple.decmpfs" extended attribute and the
  /// size that attribute's header records, which is the file's real length (its data fork is
  /// empty). The attributes file is a B-tree like the catalog (TN1150): key = keyLength u16,
  /// pad u16, fileID u32, startBlock u32, nameLength u16, UTF-16BE name; an inline record is
  /// recordType 0x10, reserved u32 x2, size u32, data; a fork record is 0x20, reserved u32,
  /// HFSPlusForkData.
  /// </summary>
  private void AttachDecmpfs(List<CatalogNode> nodes, ReadOnlySpan<byte> attributesFork) {
    var wanted = nodes.Where(static n => n.IsCompressed).Select(static n => n.Cnid).ToHashSet();
    if (wanted.Count == 0) return;
    var found = new Dictionary<uint, byte[]>();
    var forkExtents = new List<(uint StartBlock, uint BlockCount)>();
    for (var k = 0; k < 8; k++) {
      var count = BinaryPrimitives.ReadUInt32BigEndian(attributesFork[(20 + k * 8)..]);
      if (count == 0) break;
      forkExtents.Add((BinaryPrimitives.ReadUInt32BigEndian(attributesFork[(16 + k * 8)..]), count));
    }
    var forkBytes = forkExtents.Sum(static e => (long)e.BlockCount) * _blockSize;
    if (forkBytes < 512) return;
    var header = ReadExtents(forkExtents, 0, 512);
    if ((sbyte)header[8] != 1) return;
    var firstLeaf = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(14 + 10));
    var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(14 + 18));
    if (nodeSize < 512) return;
    var visited = new HashSet<uint>();
    for (var node = firstLeaf; node != 0 && visited.Add(node);) {
      if ((long)(node + 1) * nodeSize > forkBytes) break;
      var nd = ReadExtents(forkExtents, (long)node * nodeSize, nodeSize);
      if ((sbyte)nd[8] != -1) break;
      var records = BinaryPrimitives.ReadUInt16BigEndian(nd.AsSpan(10));
      for (var i = 0; i < records; i++) {
        var recOffset = BinaryPrimitives.ReadUInt16BigEndian(nd.AsSpan(nodeSize - 2 * (i + 1)));
        if (recOffset + 14 > nodeSize) continue;
        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(nd.AsSpan(recOffset));
        var fileId = BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(recOffset + 4));
        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(nd.AsSpan(recOffset + 12));
        if (!wanted.Contains(fileId) || nameLength != DecmpfsAttributeName.Length || recOffset + 14 + nameLength * 2 > nodeSize) continue;
        if (Encoding.BigEndianUnicode.GetString(nd, recOffset + 14, nameLength * 2) != DecmpfsAttributeName) continue;
        var data = recOffset + 2 + keyLength;
        if (data + 16 > nodeSize) continue;
        switch (BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(data))) {
          case 0x10: {
            var size = BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(data + 12));
            if (data + 16 + size <= nodeSize) found[fileId] = nd.AsSpan(data + 16, (int)size).ToArray();
            break;
          }
          case 0x20 when data + 8 + 80 <= nodeSize: {
            // An attribute fork's further extents would be 0x30 records, not overflow-file ones.
            var (size, extents) = ReadFork(nd.AsSpan(data + 8), 0, 0);
            if (size is > 0 and <= int.MaxValue) found[fileId] = ReadExtents(extents, 0, (int)size);
            break;
          }
        }
      }
      node = BinaryPrimitives.ReadUInt32BigEndian(nd);
    }

    for (var i = 0; i < nodes.Count; i++)
      if (nodes[i].IsCompressed && found.TryGetValue(nodes[i].Cnid, out var attribute) && HfsPlusDecmpfs.TryReadSize(attribute, out var length))
        nodes[i] = nodes[i] with { Decmpfs = attribute, Size = length };
  }

  /// <summary><paramref name="length"/> bytes from <paramref name="offset"/> into the fork the extents describe.</summary>
  private byte[] ReadExtents(IReadOnlyList<(uint StartBlock, uint BlockCount)> extents, long offset, int length) {
    var result = new byte[length];
    var done = 0;
    long forkAt = 0;
    foreach (var (start, count) in extents) {
      var runBytes = (long)count * _blockSize;
      if (done < length && offset + done < forkAt + runBytes) {
        var inRun = offset + done - forkAt;
        var take = (int)Math.Min(length - done, runBytes - inRun);
        var physical = (long)start * _blockSize + inRun;
        if (physical + take > _data.Length) break;
        _data.Read(physical, take).CopyTo(result, done);
        done += take;
      }
      forkAt += runBytes;
      if (done >= length) break;
    }
    if (done < length) throw new InvalidDataException("HFS+ fork ends before the bytes it was read for.");
    return result;
  }

  /// <summary>
  /// Builds the POSIX view of the catalog: paths from folder records whatever order they arrive
  /// in, hard links carrying their indirect node file's data, and the volume's own bookkeeping
  /// (metadata directories, journal files) left out — the files a hard link shares are reached
  /// through the link.
  /// </summary>
  private (List<HfsPlusEntry> Visible, List<HfsPlusEntry> AllFiles) Resolve(List<CatalogNode> nodes) {
    var folders = new Dictionary<uint, CatalogNode>();
    foreach (var node in nodes.Where(static n => n.IsFolder))
      folders[node.Cnid] = node;

    // The volume root folder (parent CNID 1) carries the VOLUME NAME as its catalog name, but
    // anchors paths at the empty root — its children resolve to bare paths ("docs/guide.txt").
    var paths = new Dictionary<uint, string> { [RootFolderCnid] = "" };
    foreach (var node in folders.Values.Where(static n => n.Parent == 1))
      paths[node.Cnid] = "";

    string PathOf(uint folder) {
      if (paths.TryGetValue(folder, out var known)) return known;
      // Walk up to a folder whose path is known; an unknown or cyclic parent anchors at the root.
      var chain = new List<CatalogNode>();
      var seen = new HashSet<uint>();
      var at = folder;
      while (!paths.ContainsKey(at) && folders.TryGetValue(at, out var f) && seen.Add(at)) {
        chain.Add(f);
        at = f.Parent;
      }
      var path = paths.GetValueOrDefault(at, "");
      for (var i = chain.Count - 1; i >= 0; --i) {
        var name = HfsPlusName.FromCatalog(chain[i].Name);
        path = path.Length > 0 ? path + "/" + name : name;
        paths[chain[i].Cnid] = path;
      }
      return paths.GetValueOrDefault(folder, "");
    }

    string FullPathOf(CatalogNode node) {
      var parent = PathOf(node.Parent);
      var name = HfsPlusName.FromCatalog(node.Name);
      return parent.Length > 0 ? parent + "/" + name : name;
    }

    uint? RootChild(string name) => folders.Values.FirstOrDefault(n => n.Parent == RootFolderCnid && n.Name == name)?.Cnid;
    var fileMetadata = RootChild(MetadataDirectoryName);
    var directoryMetadata = RootChild(DirectoryMetadataDirectoryName);
    // Directory hard links are not resolved, so a directory metadata folder that holds
    // anything stays visible: hiding it would hide the only path to those files.
    if (directoryMetadata is { } dm && nodes.Any(n => n.Parent == dm))
      directoryMetadata = null;

    bool IsHiddenFolder(uint cnid) => cnid == fileMetadata || cnid == directoryMetadata;
    bool IsInsideHidden(uint parent) {
      var seen = new HashSet<uint>();
      for (var at = parent; seen.Add(at) && folders.TryGetValue(at, out var f); at = f.Parent)
        if (IsHiddenFolder(at)) return true;
      return false;
    }

    var journalFiles = JournalFileIds(nodes);
    var indirectNodes = new Dictionary<string, CatalogNode>(StringComparer.Ordinal);
    if (fileMetadata is { } fm)
      foreach (var node in nodes.Where(n => !n.IsFolder && n.Parent == fm))
        indirectNodes.TryAdd(node.Name, node);

    var visible = new List<HfsPlusEntry>();
    var all = new List<HfsPlusEntry>();
    foreach (var node in nodes) {
      if (node.IsFolder && node.Parent == 1) continue;
      var fullPath = FullPathOf(node);
      var hidden = IsInsideHidden(node.Parent) || (node.IsFolder ? IsHiddenFolder(node.Cnid) : journalFiles.Contains(node.Cnid));
      if (node.IsFolder) {
        if (!hidden)
          visible.Add(new HfsPlusEntry {
            Name = HfsPlusName.FromCatalog(node.Name), FullPath = fullPath, IsDirectory = true,
            Cnid = node.Cnid, LastModified = node.Modified,
          });
        continue;
      }

      all.Add(ToEntry(node, node, fullPath));
      if (hidden) continue;
      var data = node.FileType == HardLinkFileType && node.Creator == HfsPlusCreator
                 && indirectNodes.TryGetValue(IndirectNodePrefix + node.Special.ToString(System.Globalization.CultureInfo.InvariantCulture), out var target)
        ? target
        : node;
      visible.Add(ToEntry(node, data, fullPath));
    }
    return (visible, all);
  }

  /// <summary>The entry named by <paramref name="name"/>'s record whose bytes are <paramref name="data"/>'s.</summary>
  private static HfsPlusEntry ToEntry(CatalogNode name, CatalogNode data, string fullPath) => new() {
    Name = HfsPlusName.FromCatalog(name.Name),
    FullPath = fullPath,
    Size = data.Size,
    IsSymlink = data.IsSymlink,
    LinkTarget = data.LinkTarget,
    Cnid = data.Cnid,
    LastModified = data.Modified,
    FirstBlock = data.FirstBlock,
    BlockCount = data.BlockCount,
    Extents = data.Extents,
    Decmpfs = data.Decmpfs,
    IsDataless = data.Decmpfs is { } attribute && HfsPlusDecmpfs.IsDataless(attribute),
    ResourceSize = data.ResourceSize,
    ResourceExtents = data.ResourceExtents,
  };

  /// <summary>
  /// The CNIDs of the journal's two files, which TN1150 puts in the root folder as
  /// ".journal_info_block" (at the volume header's journalInfoBlock) and ".journal" (at the
  /// offset that block records: flags u32, device_signature[8] u32, offset u64, size u64).
  /// A file merely named like them, on a volume without a journal, stays visible.
  /// </summary>
  private HashSet<uint> JournalFileIds(List<CatalogNode> nodes) {
    var ids = new HashSet<uint>();
    if (_journalInfoBlock == 0) return ids;
    var infoOffset = (long)_journalInfoBlock * _blockSize;
    long journalOffset = -1;
    if (infoOffset + 52 <= _data.Length)
      journalOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(_data.Read(infoOffset + 36, 8));
    foreach (var node in nodes.Where(static n => !n.IsFolder && n.Parent == RootFolderCnid)) {
      if (node.Name == ".journal_info_block" && node.FirstBlock == _journalInfoBlock) ids.Add(node.Cnid);
      else if (node.Name == ".journal" && journalOffset > 0 && (long)node.FirstBlock * _blockSize == journalOffset) ids.Add(node.Cnid);
    }
    return ids;
  }

  // ── Catalog B-tree parsing ──────────────────────────────────────────────

  private void ParseCatalog(List<CatalogNode> nodes) {
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

    var nodeSpan = _data.Read(nodeBase, (int)Math.Min(4096, _data.Length - nodeBase)).AsSpan();

    // Node descriptor fields.
    // var fLink = BinaryPrimitives.ReadUInt32BigEndian(nodeSpan);
    // kind at offset 8 (int8): 1 = header node
    var kind = (sbyte)nodeSpan[8];
    if (kind != 1) return; // Not a header node.

    // Header record at offset 14. BTHeaderRec layout per TN1150 §2.5.1:
    //   +0  treeDepth      (u16)
    //   +2  rootNode       (u32)
    //   +6  leafRecords    (u32)
    //   +10 firstLeafNode  (u32)
    //   +14 lastLeafNode   (u32)
    //   +18 nodeSize       (u16)
    var hdr = nodeSpan[14..];
    var firstLeafNode = BinaryPrimitives.ReadUInt32BigEndian(hdr[10..]);
    var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(hdr[18..]);

    if (nodeSize == 0) return;

    // Walk the whole leaf chain. Catalogs that outgrow a single leaf node grow
    // index nodes above several leaves, but every record still lives on a leaf
    // and the leaves are doubly linked (fLink/bLink). Starting at firstLeafNode
    // and following fLink therefore visits every record without descending the
    // index level. Paths are resolved once every record is in (see Resolve):
    // key order puts a folder's children after it only when its CNID is the
    // smaller, which a moved folder's need not be.
    var currentNode = firstLeafNode;
    var visited = new HashSet<uint>();

    while (currentNode != 0 && visited.Add(currentNode)) {
      var nodeOffset = catalogOffset + (long)currentNode * nodeSize;
      if (nodeOffset + nodeSize > _data.Length) break;

      var nd = _data.Read(nodeOffset, (int)Math.Min(nodeSize, _data.Length - nodeOffset)).AsSpan();
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
          var nameBytes = _data.Read(nodeOffset + recOffset + 8, nameByteLen).AsSpan();
          name = Encoding.BigEndianUnicode.GetString(nameBytes);
        }

        // Data record follows the key: aligned to 2-byte boundary.
        var dataOffset = recOffset + 2 + keyLength;
        if ((dataOffset & 1) != 0) dataOffset++; // Pad to even.
        if (dataOffset + 2 > nodeSize) continue;

        var recordType = BinaryPrimitives.ReadInt16BigEndian(nd[dataOffset..]);

        switch (recordType) {
          case 1: // Folder record.
            ParseFolderRecord(nd, dataOffset, parentCnid, name, nodes);
            break;
          case 2: // File record.
            ParseFileRecord(nd, dataOffset, parentCnid, name, nodes);
            break;
          // 3 = folder thread, 4 = file thread — skip.
        }
      }

      // Advance to next leaf node via fLink.
      currentNode = BinaryPrimitives.ReadUInt32BigEndian(nd);
    }
  }

  private static void ParseFolderRecord(ReadOnlySpan<byte> nd, int dataOffset, uint parentCnid,
      string name, List<CatalogNode> nodes) {
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

    nodes.Add(new CatalogNode(parentCnid, name, IsFolder: true, cnid, modDate));
  }

  // Finder fdType for a HFS+ symbolic link: 'slnk' (the link's target path lives
  // in its data fork, exactly like a small regular file). References: Apple TN1150
  // "HFS Plus Volume Format" (HFSPlusCatalogFile.userInfo/FInfo.fdType) and
  // Darwin xnu bsd/hfs (SYMLINKFILETYPE / SYMLINKCREATOR).
  private const uint SymlinkFileType = 0x736C6E6B; // 'slnk'

  private void ParseFileRecord(ReadOnlySpan<byte> nd, int dataOffset, uint parentCnid,
      string name, List<CatalogNode> nodes) {
    // TN1150 HFSPlusCatalogFile (248 bytes):
    //   offset 0:   recordType (int16 BE) = 2 (kHFSPlusFileRecord)
    //   offset 2:   flags (uint16 BE)
    //   offset 4:   reserved1 (uint32 BE)
    //   offset 8:   fileID (uint32 BE)
    //   offset 12:  createDate (uint32 BE)
    //   offset 16:  contentModDate (uint32 BE)
    //   offset 20:  attributeModDate (uint32 BE)
    //   offset 24:  accessDate (uint32 BE)
    //   offset 28:  backupDate (uint32 BE)
    //   offset 32:  permissions[16]
    //   offset 48:  userInfo[16] (FileInfo)
    //   offset 64:  finderInfo[16] (ExtendedFileInfo)
    //   offset 80:  textEncoding (uint32 BE)
    //   offset 84:  reserved2 (uint32 BE)
    //   offset 88:  dataFork HFSPlusForkData (80 bytes)
    //                 +0  logicalSize (uint64 BE)
    //                 +8  clumpSize (uint32 BE)
    //                 +12 totalBlocks (uint32 BE)
    //                 +16 extents[8] (8 * (u32 startBlock + u32 blockCount))
    //   offset 168: resourceFork HFSPlusForkData (80 bytes)
    if (dataOffset + 248 > nd.Length) return;

    var cnid = BinaryPrimitives.ReadUInt32BigEndian(nd[(dataOffset + 8)..]);
    var modDateRaw = BinaryPrimitives.ReadUInt32BigEndian(nd[(dataOffset + 16)..]);
    var modDate = modDateRaw > 0 ? HfsEpoch.AddSeconds(modDateRaw) : (DateTime?)null;

    // userInfo (FInfo) sits at record offset 48: fdType (u32 BE) then fdCreator.
    var fileType = BinaryPrimitives.ReadUInt32BigEndian(nd[(dataOffset + 48)..]);
    var creator = BinaryPrimitives.ReadUInt32BigEndian(nd[(dataOffset + 52)..]);
    // permissions (HFSPlusBSDInfo) at 32: ownerID, groupID, adminFlags, ownerFlags, fileMode,
    // then special (u32) at 44 — the iNodeNum of a hard link.
    var special = BinaryPrimitives.ReadUInt32BigEndian(nd[(dataOffset + 44)..]);
    var isSymlink = fileType == SymlinkFileType;

    const int dataForkOffset = 88;
    const int resourceForkOffset = 168;
    // extents[0] starts 16 bytes into the ForkData struct (after logicalSize+clumpSize+totalBlocks).
    var startBlock = BinaryPrimitives.ReadUInt32BigEndian(nd[(dataOffset + dataForkOffset + 16)..]);
    var blockCount = BinaryPrimitives.ReadUInt32BigEndian(nd[(dataOffset + dataForkOffset + 20)..]);
    var (logicalSize, extents) = ReadFork(nd[(dataOffset + dataForkOffset)..], cnid, DataForkType);

    // ownerFlags (permissions + 9) bit UF_COMPRESSED: the content is the decmpfs
    // attribute's, possibly with its compressed bytes in the resource fork.
    var compressed = (nd[dataOffset + 32 + 9] & CompressedOwnerFlag) != 0;
    var (resourceSize, resourceExtents) = compressed
      ? ReadFork(nd[(dataOffset + resourceForkOffset)..], cnid, ResourceForkType)
      : (0L, []);

    string? linkTarget = null;
    if (isSymlink)
      linkTarget = ReadForkText(startBlock, logicalSize);

    nodes.Add(new CatalogNode(parentCnid, name, IsFolder: false, cnid, modDate) {
      FileType = fileType,
      Creator = creator,
      Special = special,
      Size = logicalSize,
      IsSymlink = isSymlink,
      LinkTarget = linkTarget,
      FirstBlock = startBlock,
      BlockCount = blockCount,
      Extents = extents,
      IsCompressed = compressed,
      ResourceSize = resourceSize,
      ResourceExtents = resourceExtents,
    });
  }

  private const byte DataForkType = 0x00;
  private const byte ResourceForkType = 0xFF;
  private const byte CompressedOwnerFlag = 0x20; // UF_COMPRESSED

  /// <summary>
  /// One HFSPlusForkData (logicalSize u64, clumpSize u32, totalBlocks u32, eight extent
  /// descriptors) and the extents the overflow file adds when those eight fall short of
  /// totalBlocks.
  /// </summary>
  private (long LogicalSize, List<(uint StartBlock, uint BlockCount)> Extents) ReadFork(ReadOnlySpan<byte> fork, uint cnid, byte forkType) {
    var logicalSize = (long)BinaryPrimitives.ReadUInt64BigEndian(fork);
    var totalBlocks = BinaryPrimitives.ReadUInt32BigEndian(fork[12..]);
    var extents = new List<(uint StartBlock, uint BlockCount)>(8);
    uint covered = 0;
    for (var k = 0; k < 8; k++) {
      var start = BinaryPrimitives.ReadUInt32BigEndian(fork[(16 + k * 8)..]);
      var count = BinaryPrimitives.ReadUInt32BigEndian(fork[(20 + k * 8)..]);
      if (count == 0) break;
      extents.Add((start, count));
      covered += count;
    }
    if (covered < totalBlocks)
      foreach (var (start, count, _) in OverflowExtents(cnid, forkType).Where(e => e.FileStart >= covered).OrderBy(e => e.FileStart))
        extents.Add((start, count));
    return (logicalSize, extents);
  }

  /// <summary>
  /// The extents the extents overflow B-tree records for one fork of
  /// <paramref name="cnid" />, each with the fork block it starts at (TN1150:
  /// key = keyLength u16, forkType u8 (0x00 data, 0xFF resource), pad u8, fileID u32,
  /// startBlock u32; record = eight extent descriptors).
  /// </summary>
  private List<(uint StartBlock, uint Count, uint FileStart)> OverflowExtents(uint cnid, byte forkType) {
    _overflow ??= ReadOverflow();
    return _overflow.TryGetValue((cnid, forkType), out var list) ? list : [];
  }

  private Dictionary<(uint, byte), List<(uint StartBlock, uint Count, uint FileStart)>> ReadOverflow() {
    var result = new Dictionary<(uint, byte), List<(uint, uint, uint)>>();
    if (_extentsStartBlock == 0 || _extentsBlockCount == 0) return result;
    var fileOffset = (long)_extentsStartBlock * _blockSize;
    if (fileOffset + 512 > _data.Length) return result;
    var header = _data.Read(fileOffset, (int)Math.Min(512, _data.Length - fileOffset)).AsSpan();
    if ((sbyte)header[8] != 1) return result;
    var firstLeaf = BinaryPrimitives.ReadUInt32BigEndian(header[(14 + 10)..]);
    var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(header[(14 + 18)..]);
    if (nodeSize == 0) return result;
    var fileBytes = (long)_extentsBlockCount * _blockSize;
    var visited = new HashSet<uint>();
    for (var node = firstLeaf; node != 0 && visited.Add(node);) {
      if ((long)(node + 1) * nodeSize > fileBytes) break;
      var nodeOffset = fileOffset + (long)node * nodeSize;
      if (nodeOffset + nodeSize > _data.Length) break;
      var nd = _data.Read(nodeOffset, nodeSize).AsSpan();
      if ((sbyte)nd[8] != -1) break;
      var records = BinaryPrimitives.ReadUInt16BigEndian(nd[10..]);
      for (var i = 0; i < records; i++) {
        var recOffset = BinaryPrimitives.ReadUInt16BigEndian(nd[(nodeSize - 2 * (i + 1))..]);
        if (recOffset + 12 + 64 > nodeSize) continue;
        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(nd[recOffset..]);
        if (keyLength < 10) continue;
        var forkType = nd[recOffset + 2];
        var fileId = BinaryPrimitives.ReadUInt32BigEndian(nd[(recOffset + 4)..]);
        var forkStart = BinaryPrimitives.ReadUInt32BigEndian(nd[(recOffset + 8)..]);
        var data = recOffset + 2 + keyLength;
        if (!result.TryGetValue((fileId, forkType), out var list)) result[(fileId, forkType)] = list = [];
        for (var k = 0; k < 8 && data + k * 8 + 8 <= nodeSize; k++) {
          var start = BinaryPrimitives.ReadUInt32BigEndian(nd[(data + k * 8)..]);
          var count = BinaryPrimitives.ReadUInt32BigEndian(nd[(data + k * 8 + 4)..]);
          if (count == 0) break;
          list.Add((start, count, forkStart));
          forkStart += count;
        }
      }
      node = BinaryPrimitives.ReadUInt32BigEndian(nd);
    }
    return result;
  }

  // Reads a small data fork (a symlink target) as UTF-8 text from its first extent.
  private string? ReadForkText(uint startBlock, long logicalSize) {
    if (logicalSize <= 0 || logicalSize > 4096) return logicalSize == 0 ? "" : null;
    var offset = (long)startBlock * _blockSize;
    var length = (int)logicalSize;
    if (offset < 0 || offset + length > _data.Length)
      length = (int)Math.Max(0, _data.Length - offset);
    if (length <= 0) return "";
    return Encoding.UTF8.GetString(_data.Read(offset, length));
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
    if (entry.Decmpfs is { } attribute)
      return HfsPlusDecmpfs.Decode(attribute, () => entry.ResourceSize is > 0 and <= int.MaxValue
        ? ReadExtents(entry.ResourceExtents, 0, (int)entry.ResourceSize)
        : []);

    // A fork is up to eight extents in the catalog record plus any the overflow
    // file adds. Reading only the first returned zeros for the rest of every
    // fragmented file.
    var extents = entry.Extents.Count > 0 ? entry.Extents : [(entry.FirstBlock, entry.BlockCount)];
    var result = new byte[entry.Size];
    long done = 0;
    foreach (var (start, count) in extents) {
      if (done >= entry.Size) break;
      var offset = (long)start * _blockSize;
      var length = Math.Min(entry.Size - done, (long)count * _blockSize);
      if (offset >= _data.Length) break;
      length = Math.Min(length, _data.Length - offset);
      if (length <= 0) break;
      _data.Read(offset, (int)length).CopyTo(result, (int)done);
      done += length;
    }
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
