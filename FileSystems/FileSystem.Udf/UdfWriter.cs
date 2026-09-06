#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.Udf.UdfDescriptors;

namespace FileSystem.Udf;

/// <summary>
/// Writes a UDF 2.01 volume image (ECMA-167 plus the OSTA UDF profile). Builds a
/// real directory tree from slash-separated file paths using short allocation
/// descriptors, and records every descriptor the standard's own tools look for:
/// both volume descriptor sequences, both anchors, the unallocated space and
/// implementation use descriptors, and a closed logical volume integrity
/// descriptor.
///
/// Layout (2048-byte logical blocks):
/// <code>
/// Block     0-15:  System area
/// Block       16:  VRS BEA01
/// Block       17:  VRS NSR03
/// Block       18:  VRS TEA01
/// Block    32-47:  Main volume descriptor sequence
/// Block    48-63:  Reserve volume descriptor sequence
/// Block    64-65:  Logical volume integrity sequence (LVID + terminator)
/// Block      256:  Anchor volume descriptor pointer
/// Block      257:  Partition start; File Set Descriptor at LBN 0
/// Block      258:  Root directory File Entry at LBN 1
/// Block     259+:  Per-node File Entries, directory FID data, file data
/// Last block:      Second anchor volume descriptor pointer
/// </code>
///
/// A directory's data is a dense sequence of File Identifier Descriptors (FID,
/// tag 257) — dense because ECMA-167 lets a FID span a logical block boundary
/// and Linux's udf driver reads directory bytes as one uninterrupted run, so
/// padding a FID onto the next block makes the directory unreadable. The first
/// FID of every directory is the parent entry (Parent flag 0x08, zero-length
/// identifier). Every directory and file is a File Entry (FE, tag 261);
/// directories carry file type 4, regular files file type 5.
/// </summary>
public sealed class UdfWriter {
  private const int Sector = 2048;

  private const int VrsFirstSector = 16;
  private const int MainVdsSector = 32;
  private const int VdsSectors = 16;
  private const int ReserveVdsSector = MainVdsSector + VdsSectors;
  private const int LvidSector = ReserveVdsSector + VdsSectors;
  private const int LvidSectors = 2;
  private const int AnchorSector = 256;
  private const int PartitionStartSector = 257;

  /// <summary>File Set Descriptor block, relative to the partition start.</summary>
  private const int FsdLbn = 0;

  /// <summary>Root directory File Entry block, relative to the partition start.</summary>
  private const int RootFeLbn = 1;

  // Descriptor body sizes per ECMA-167 §3/10. The body starts 16 bytes after the
  // tag and DescriptorCRCLength covers exactly these many bytes.
  private const int VolumeDescriptorBodySize = 496;   // 512-byte descriptor minus its tag
  private const int LvdBodySize = 446 - 16;           // through the single Type-1 partition map
  private const int UsdBodySize = 24 - 16;            // no allocation descriptors follow
  private const int LvidBodySize = (88 - 16) + LvidImplementationUseSize;
  private const int LvidImplementationUseSize = 46;
  private const int FeHeaderBodySize = 176 - 16;      // File Entry header, up to L_EA

  /// <summary>
  /// Bytes one short allocation descriptor may address. ECMA-167 caps an extent
  /// length at 2^30-1 and OSTA UDF §2.3.10.1 requires every extent but the last
  /// of a file to be a whole number of blocks, so the usable maximum is the
  /// largest block multiple below 2^30.
  /// </summary>
  private const long MaxExtentBytes = (1L << 30) - Sector;

  /// <summary>
  /// Short allocation descriptors that fit in one File Entry. They start after
  /// the 176-byte header and the extended attributes, of which this writer
  /// records none.
  /// </summary>
  private const int MaxAllocationDescriptors = (Sector - 176) / 8;

  /// <summary>Largest file this writer can address without descriptor continuation.</summary>
  internal const long MaxFileBytes = MaxAllocationDescriptors * MaxExtentBytes;

  /// <summary>
  /// First unique identifier handed to a file or directory. OSTA UDF §3.2.1
  /// reserves 0 for the root directory and 1..15 for the standard's own use.
  /// </summary>
  private const uint FirstUniqueId = 16;

  private readonly List<(string name, byte[] data)> _files = [];

  /// <summary>
  /// ECMA-167 PVD Volume Identifier (dstring at PVD offset 24, 32 bytes).
  /// Linux's udf driver surfaces this as the volume label. Default "UDF Volume".
  /// </summary>
  public string VolumeIdentifier { get; set; } = "UDF Volume";

  /// <summary>
  /// Performs the add file operation.
  /// </summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    _files.Add((name, data));
  }

  /// <summary>
  /// Streaming files keyed by normalised tree path. Populated by
  /// <see cref="AddStreamingFile"/>; consumed in <see cref="BuildTree"/> so the
  /// matching leaf <see cref="Node"/> carries an opener instead of a buffered
  /// body. UDF's descriptor CRC covers only the descriptor tag bytes (FID / FE /
  /// VDS), never file data, so a streamed body produces byte-identical output.
  /// </summary>
  private readonly List<(string name, long size, Func<Stream> opener)> _streamingFiles = [];

  /// <summary>
  /// Adds a streaming file whose body is pulled from <paramref name="openStream"/>
  /// in 64 KiB chunks while the image is written sequentially, never buffered as
  /// a <c>byte[]</c>. <paramref name="size"/> drives the directory/file layout.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    if (size < 0) throw new ArgumentOutOfRangeException(nameof(size), "size must be >= 0.");
    // Register both a placeholder file entry (so BuildTree materialises the leaf
    // node + intermediate directories exactly as the buffered path would) and the
    // opener keyed by the same path.
    _files.Add((name, []));
    _streamingFiles.Add((name, size, openStream));
  }

  // ── Directory tree ──────────────────────────────────────────────────────

  /// <summary>
  /// A node in the directory tree built from the slash-separated input paths.
  /// Directories carry children; files carry their payload. Layout fields are
  /// filled during <see cref="AssignLayout"/>.
  /// </summary>
  private sealed class Node {
    public required string Name;
    public required bool IsDirectory;
    public byte[] Data = [];
    // Streaming file: when StreamOpener is non-null the body is pulled from the
    // opener during the sequential block emit instead of being buffered in Data.
    // StreamSize is the declared byte length driving all layout (DataLength,
    // DataSectors, the FE info-length + allocation descriptor).
    public long? StreamSize;
    public Func<Stream>? StreamOpener;
    public long EffectiveLength => this.StreamSize ?? this.Data.Length;
    public readonly List<Node> Children = [];
    public Node? Parent;

    // Assigned during layout (LBNs relative to the partition start).
    public int FeLbn;          // File Entry block
    public int DataLbn;        // first data block (FID data for dirs, payload for files)
    public int DataSectors;    // sectors occupied by data
    public long DataLength;    // exact byte length of the (directory or file) data
    public uint UniqueId;      // OSTA UDF §3.2.1 unique identifier
  }

  /// <summary>
  /// Writes the to to the supplied output.
  /// </summary>
  public void WriteTo(Stream output) {
    var root = BuildTree();

    root.FeLbn = RootFeLbn;
    root.UniqueId = 0;
    var nextLbn = RootFeLbn + 1;
    var nextUniqueId = FirstUniqueId;
    AssignLayout(root, ref nextLbn, ref nextUniqueId);

    var totalPartitionSectors = nextLbn; // LBNs 0..nextLbn-1 are all in use
    var lastBlock = PartitionStartSector + totalPartitionSectors;

    var (fileCount, directoryCount) = Count(root);

    // ── System area (blocks 0-15) ──
    WritePadding(output, VrsFirstSector);

    // ── Volume recognition sequence (blocks 16-18) ──
    WriteVrs(output, "BEA01");
    WriteVrs(output, "NSR03");
    WriteVrs(output, "TEA01");

    WritePadding(output, MainVdsSector - (VrsFirstSector + 3));

    // ── Both volume descriptor sequences ──
    this.WriteVds(output, MainVdsSector, totalPartitionSectors);
    this.WriteVds(output, ReserveVdsSector, totalPartitionSectors);

    // ── Logical volume integrity sequence ──
    this.WriteLvid(output, LvidSector, totalPartitionSectors, fileCount, directoryCount, nextUniqueId);
    WriteTerminator(output, LvidSector + 1);

    WritePadding(output, AnchorSector - (LvidSector + LvidSectors));

    // ── First anchor ──
    WriteAnchor(output, AnchorSector);

    // ── Partition data (starting at block 257 = LBN 0) ──
    this.WriteFsd(output, FsdLbn, RootFeLbn);

    // Emit blocks in LBN order so the stream stays sequential. Gather every
    // block-producing action keyed by its starting LBN, then drain in order.
    var blocks = new SortedDictionary<int, Action>();
    CollectBlocks(root, blocks, output);

    var written = 1; // LBN 0 (FSD) already written
    foreach (var (lbn, emit) in blocks) {
      if (lbn != written)
        throw new InvalidOperationException($"UDF layout gap: expected LBN {written}, got {lbn}.");
      var before = output.Position;
      emit();
      var produced = (int)((output.Position - before) / Sector);
      written += produced;
    }

    if (written != totalPartitionSectors)
      throw new InvalidOperationException(
        $"UDF layout mismatch: wrote {written} partition sectors, expected {totalPartitionSectors}.");

    // ── Second anchor, in the volume's last block ──
    // ECMA-167 §3/8.4 wants an anchor at block 256 and at the last block of the
    // volume; a volume carrying only the first is one udfinfo reports on.
    WriteAnchor(output, lastBlock);
  }

  /// <summary>
  /// Builds the directory tree from the recorded slash-separated paths,
  /// creating intermediate directory nodes on demand.
  /// </summary>
  private Node BuildTree() {
    var root = new Node { Name = "", IsDirectory = true };

    // Streaming openers keyed by normalised path. The matching leaf takes the
    // opener + declared size instead of a buffered body.
    var streamByPath = new Dictionary<string, (long size, Func<Stream> opener)>(StringComparer.Ordinal);
    foreach (var (name, size, opener) in _streamingFiles) {
      var key = string.Join('/', name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries));
      streamByPath[key] = (size, opener);
    }

    foreach (var (path, data) in _files) {
      var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0) continue;

      var current = root;
      for (var i = 0; i < parts.Length - 1; i++) {
        var dirName = parts[i];
        var child = current.Children.FirstOrDefault(c => c.IsDirectory && c.Name == dirName);
        if (child == null) {
          child = new Node { Name = dirName, IsDirectory = true, Parent = current };
          current.Children.Add(child);
        }
        current = child;
      }

      var fileName = parts[^1];
      // A file path collapsing onto an existing entry is ignored (last writer
      // would otherwise silently overwrite); keep the first occurrence.
      if (current.Children.Any(c => c.Name == fileName)) continue;
      var key = string.Join('/', parts);
      var node = new Node {
        Name = fileName,
        IsDirectory = false,
        Data = data,
        Parent = current,
      };
      if (streamByPath.TryGetValue(key, out var s)) {
        node.StreamSize = s.size;
        node.StreamOpener = s.opener;
      }
      current.Children.Add(node);
    }

    return root;
  }

  /// <summary>Files and directories below (and including) <paramref name="node" />.</summary>
  private static (int files, int directories) Count(Node node) {
    if (!node.IsDirectory) return (1, 0);
    var files = 0;
    var directories = 1;
    foreach (var child in node.Children) {
      var (f, d) = Count(child);
      files += f;
      directories += d;
    }

    return (files, directories);
  }

  /// <summary>
  /// Assigns File Entry and data block numbers depth-first. The node's own FE
  /// LBN must already be set by the caller; this method assigns FE LBNs for
  /// all children first (so a directory's FID data can reference them), then
  /// the directory's own FID-data block(s), then recurses.
  /// </summary>
  private static void AssignLayout(Node node, ref int nextLbn, ref uint nextUniqueId) {
    if (node.IsDirectory) {
      // Reserve FE blocks and identifiers for every child up front: the parent's
      // FIDs name both.
      foreach (var child in node.Children) {
        child.FeLbn = nextLbn++;
        child.UniqueId = nextUniqueId++;
      }

      // Reserve this directory's FID-data block(s).
      var fidData = BuildFidData(node);
      node.DataLength = fidData.Length;
      node.DataSectors = Math.Max(1, (fidData.Length + Sector - 1) / Sector);
      node.DataLbn = nextLbn;
      nextLbn += node.DataSectors;

      if (node.DataSectors > MaxAllocationDescriptors)
        throw new InvalidOperationException(
          $"UDF directory '{node.Name}' needs {node.DataSectors} data blocks but only " +
          $"{MaxAllocationDescriptors} short allocation descriptors fit in one File Entry; " +
          "allocation descriptor continuation is not supported.");

      // Recurse into children so their own subtree blocks are laid out.
      foreach (var child in node.Children)
        AssignLayout(child, ref nextLbn, ref nextUniqueId);
    } else {
      var length = node.EffectiveLength;
      if (length > MaxFileBytes)
        throw new InvalidOperationException(
          $"UDF file '{node.Name}' is {length:N0} bytes; this writer addresses at most " +
          $"{MaxFileBytes:N0} without allocation descriptor continuation.");

      node.DataLength = length;
      // An empty file gets no extent at all: an allocation descriptor naming a
      // block a zero-length file does not own is a chain longer than the size.
      node.DataSectors = (int)((length + Sector - 1) / Sector);
      node.DataLbn = nextLbn;
      nextLbn += node.DataSectors;
    }
  }

  /// <summary>
  /// Queues the block-emitting actions for a node and its subtree, keyed by
  /// the starting LBN of each block group, so the writer can drain them in
  /// strictly ascending order.
  /// </summary>
  private static void CollectBlocks(Node node, SortedDictionary<int, Action> blocks, Stream output) {
    if (node.IsDirectory) {
      var dirNode = node;
      blocks[node.FeLbn] = () => WriteDirectoryFe(output, dirNode);

      blocks[node.DataLbn] = () => {
        var fidData = BuildFidData(dirNode);
        output.Write(fidData);
        var pad = dirNode.DataSectors * Sector - fidData.Length;
        if (pad > 0) output.Write(new byte[pad]);
      };

      foreach (var child in node.Children)
        CollectBlocks(child, blocks, output);
    } else {
      var fileNode = node;
      blocks[node.FeLbn] = () => WriteFileFe(output, fileNode);
      if (fileNode.DataSectors == 0)
        return;

      blocks[node.DataLbn] = () => {
        long produced;
        if (fileNode.StreamOpener != null) {
          // Stream the body straight into the sequential output in 64 KiB chunks
          // — never buffered as a byte[]. Exactly DataLength bytes are copied.
          produced = StreamCopy(output, fileNode.StreamOpener, fileNode.DataLength);
        } else {
          output.Write(fileNode.Data);
          produced = fileNode.Data.Length;
        }
        var pad = (long)fileNode.DataSectors * Sector - produced;
        if (pad > 0) output.Write(new byte[pad]);
      };
    }
  }

  /// <summary>
  /// Copies up to <paramref name="size"/> bytes from a freshly opened source
  /// stream to <paramref name="dst"/> in 64 KiB chunks and returns the number of
  /// bytes actually written. Never buffers the whole body.
  /// </summary>
  private static long StreamCopy(Stream dst, Func<Stream> opener, long size) {
    if (size <= 0) return 0;
    var buf = new byte[64 * 1024];
    using var src = opener();
    long copied = 0;
    while (copied < size) {
      var want = (int)Math.Min(buf.Length, size - copied);
      var n = src.Read(buf, 0, want);
      if (n <= 0) break;
      dst.Write(buf, 0, n);
      copied += n;
    }
    return copied;
  }

  // ── FID building ──────────────────────────────────────────────────────────

  /// <summary>
  /// Builds the directory data for a directory node: a parent FID (zero-length
  /// identifier, parent + directory flags) pointing at the parent's FE,
  /// followed by one FID per child referencing the child's FE.
  /// <para>
  /// The records are written back to back. ECMA-167 §4/14.4 permits a File
  /// Identifier Descriptor to span a logical block boundary, and both mkudffs
  /// and Linux's udf driver rely on that: a directory padded onto block
  /// boundaries makes the driver stop at the first pad byte with "entry at
  /// pos N with incorrect tag 0".
  /// </para>
  /// </summary>
  private static byte[] BuildFidData(Node dir) {
    using var ms = new MemoryStream();

    // Parent FID (flags 0x0A: parent + directory). The parent of the root is
    // itself.
    var parent = dir.Parent ?? dir;
    WriteFid(ms, dir, 0x0A, parent.FeLbn, parent.UniqueId, "");

    foreach (var child in dir.Children) {
      var flags = child.IsDirectory ? (byte)0x02 : (byte)0x00;
      WriteFid(ms, dir, flags, child.FeLbn, child.UniqueId, child.Name);
    }

    return ms.ToArray();
  }

  /// <summary>
  /// Writes one File Identifier Descriptor into a directory's byte stream. The
  /// tag records the logical block the record starts in, which for a record
  /// spanning two blocks is the first of them.
  /// </summary>
  private static void WriteFid(MemoryStream ms, Node dir, byte flags, int icbLbn, uint uniqueId, string name) {
    var nameBytes = OstaCompressedUnicode.Encode(name);
    var padded = (38 + nameBytes.Length + 3) & ~3;
    var buf = new byte[padded];

    var startBlock = dir.DataLbn + (int)(ms.Length / Sector);
    WriteTag(buf, 0, 257, (uint)startBlock);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(16), 1); // FileVersionNumber, OSTA UDF §2.3.4.1
    buf[18] = flags;
    buf[19] = (byte)nameBytes.Length;
    WriteLongAd(buf, 20, Sector, (uint)icbLbn, uniqueId);
    // LengthOfImplementationUse at offset 36 stays zero.
    nameBytes.CopyTo(buf, 38);

    // ECMA-167 §4/14.4.9: the CRC covers the whole record, padding included.
    FinalizeTag(buf, 0, padded - 16);

    ms.Write(buf);
  }

  // ── Descriptor writers ────────────────────────────────────────────────────

  /// <summary>Owner/group/other read+execute, in ECMA-167 permission bits.</summary>
  private const uint DirectoryPermissions =
    (1u << 12) | (1u << 10) | (1u << 7) | (1u << 5) | (1u << 2) | (1u << 0);

  /// <summary>Owner/group/other read, in ECMA-167 permission bits.</summary>
  private const uint FilePermissions = (1u << 12) | (1u << 7) | (1u << 2);

  private static void WritePadding(Stream output, int sectors) {
    for (var i = 0; i < sectors; i++) output.Write(new byte[Sector]);
  }

  private static void WriteVrs(Stream output, string id) {
    var buf = new byte[Sector];
    buf[0] = 0; // structure type
    System.Text.Encoding.ASCII.GetBytes(id).CopyTo(buf, 1);
    buf[6] = 1; // structure version
    output.Write(buf);
  }

  /// <summary>
  /// ECMA-167 §3/10.2 anchor: it names both volume descriptor sequences and
  /// records its own block, which is how a reader recognises it and, with it,
  /// the volume's logical block size.
  /// </summary>
  private static void WriteAnchor(Stream output, int block) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 2, (uint)block);
    WriteExtent(buf, 16, VdsSectors * Sector, MainVdsSector);
    WriteExtent(buf, 24, VdsSectors * Sector, ReserveVdsSector);
    FinalizeTag(buf, 0, VolumeDescriptorBodySize);
    output.Write(buf);
  }

  /// <summary>
  /// Writes one complete volume descriptor sequence: primary volume, logical
  /// volume, partition, implementation use and unallocated space descriptors,
  /// then the terminator, then zero blocks out to the sequence's extent. Both
  /// the main and the reserve sequence carry the same descriptors — only their
  /// tag locations differ.
  /// </summary>
  private void WriteVds(Stream output, int firstBlock, int partitionSectors) {
    this.WritePvd(output, firstBlock);
    this.WriteLvd(output, firstBlock + 1);
    WritePartitionDescriptor(output, firstBlock + 2, PartitionStartSector, partitionSectors);
    this.WriteIuvd(output, firstBlock + 3);
    WriteUsd(output, firstBlock + 4);
    WriteTerminator(output, firstBlock + 5);
    WritePadding(output, VdsSectors - 6);
  }

  /// <summary>
  /// Volume set identifier (ECMA-167 §3/10.1.10). OSTA UDF §2.2.2.5 requires
  /// its first sixteen characters to be unique among volume sets, and to be
  /// hexadecimal digits; deriving them from the volume identifier keeps two
  /// runs over the same input byte-identical while still separating volumes
  /// that are named differently.
  /// </summary>
  private string VolumeSetIdentifier {
    get {
      var hash = 0xCBF29CE484222325UL;
      foreach (var c in this.EffectiveVolumeIdentifier) {
        hash ^= c;
        hash *= 0x100000001B3UL;
      }

      return hash.ToString("x16") + this.EffectiveVolumeIdentifier;
    }
  }

  private string EffectiveVolumeIdentifier
    => string.IsNullOrEmpty(this.VolumeIdentifier) ? "UDF Volume" : this.VolumeIdentifier;

  private void WritePvd(Stream output, int block) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 1, (uint)block);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), 1);   // VolumeDescriptorSequenceNumber
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), 0);   // PrimaryVolumeDescriptorNumber
    OstaCompressedUnicode.WriteDString(buf, 24, 32, this.EffectiveVolumeIdentifier);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(56), 1);   // VolumeSequenceNumber
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(58), 1);   // MaximumVolumeSequenceNumber
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(60), 2);   // InterchangeLevel
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(62), 3);   // MaximumInterchangeLevel
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(64), 1);   // CharacterSetList
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(68), 1);   // MaximumCharacterSetList
    OstaCompressedUnicode.WriteDString(buf, 72, 128, this.VolumeSetIdentifier);
    WriteCharacterSet(buf, 200);                                   // DescriptorCharacterSet
    WriteCharacterSet(buf, 264);                                   // ExplanatoryCharacterSet
    WriteTimestamp(buf, 376, RecordingTime);
    WriteEntityId(buf, 388, ImplementationId, ImplementationSuffix);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(488), 1);  // Flags: volume set identification
    FinalizeTag(buf, 0, VolumeDescriptorBodySize);
    output.Write(buf);
  }

  private void WriteLvd(Stream output, int block) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 6, (uint)block);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), 2);   // VolumeDescriptorSequenceNumber
    WriteCharacterSet(buf, 20);                                    // DescriptorCharacterSet
    OstaCompressedUnicode.WriteDString(buf, 84, 128, this.EffectiveVolumeIdentifier);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(212), Sector);
    WriteEntityId(buf, 216, "*OSTA UDF Compliant", DomainSuffix);
    WriteLongAd(buf, 248, Sector, FsdLbn);                         // LogicalVolumeContentsUse: the FSD
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(264), 6);  // MapTableLength
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(268), 1);  // NumberOfPartitionMaps
    WriteEntityId(buf, 272, ImplementationId, ImplementationSuffix);
    WriteExtent(buf, 432, LvidSectors * Sector, LvidSector);       // IntegritySequenceExtent
    // Type-1 partition map: type(1), length(1), volume sequence(2), partition(2).
    buf[440] = 1;
    buf[441] = 6;
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(442), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(444), 0);
    FinalizeTag(buf, 0, LvdBodySize);
    output.Write(buf);
  }

  private static void WritePartitionDescriptor(Stream output, int block, int partStart, int partLen) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 5, (uint)block);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), 3);   // VolumeDescriptorSequenceNumber
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(20), 1);   // PartitionFlags: allocated
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(22), 0);   // PartitionNumber
    WriteEntityId(buf, 24, "+NSR03", default);                     // PartitionContents
    // PartitionContentsUse holds the Partition Header Descriptor (OSTA UDF
    // §2.2.3). All its space tables stay unrecorded, which a read-only
    // partition is allowed to do since nothing will ever allocate in it.
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(184), 1);  // AccessType: read-only
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(188), (uint)partStart);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(192), (uint)partLen);
    WriteEntityId(buf, 196, ImplementationId, ImplementationSuffix);
    FinalizeTag(buf, 0, VolumeDescriptorBodySize);
    output.Write(buf);
  }

  /// <summary>
  /// ECMA-167 §3/10.4 implementation use volume descriptor carrying the OSTA
  /// UDF §2.2.7 "*UDF LV Info" payload: the logical volume's name and the
  /// identity of whoever recorded it.
  /// </summary>
  private void WriteIuvd(Stream output, int block) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 4, (uint)block);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), 4);   // VolumeDescriptorSequenceNumber
    WriteEntityId(buf, 20, "*UDF LV Info", UdfEntitySuffix);
    WriteCharacterSet(buf, 52);                                    // LVICharset
    OstaCompressedUnicode.WriteDString(buf, 116, 128, this.EffectiveVolumeIdentifier);
    // LVInfo1..3 at 244/280/316 name the owner, organisation and contact; this
    // writer knows none of them and leaves all three empty.
    WriteEntityId(buf, 352, ImplementationId, ImplementationSuffix);
    FinalizeTag(buf, 0, VolumeDescriptorBodySize);
    output.Write(buf);
  }

  /// <summary>
  /// ECMA-167 §3/10.8 unallocated space descriptor. The volume this writer
  /// emits has no space outside the partition to hand out, so it records no
  /// allocation descriptors — but the descriptor itself has to be there.
  /// </summary>
  private static void WriteUsd(Stream output, int block) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 7, (uint)block);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), 5);   // VolumeDescriptorSequenceNumber
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), 0);   // NumberOfAllocationDescriptors
    FinalizeTag(buf, 0, UsdBodySize);
    output.Write(buf);
  }

  private static void WriteTerminator(Stream output, int block) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 8, (uint)block);
    FinalizeTag(buf, 0, VolumeDescriptorBodySize);
    output.Write(buf);
  }

  /// <summary>
  /// ECMA-167 §3/10.10 logical volume integrity descriptor. Its integrity type
  /// says whether the volume was left in a consistent state; without one, every
  /// tool reports the logical volume as inconsistent and the free-space and
  /// object counts as unknown.
  /// </summary>
  private void WriteLvid(Stream output, int block, int partitionSectors,
      int fileCount, int directoryCount, uint nextUniqueId) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 9, (uint)block);
    WriteTimestamp(buf, 16, RecordingTime);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28), 1);   // IntegrityType: close
    // NextIntegrityExtent at 32 stays empty: this is the only integrity
    // descriptor the volume has.
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(40), nextUniqueId); // next UniqueID
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(72), 1);   // NumberOfPartitions
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(76), LvidImplementationUseSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(80), 0);   // FreeSpaceTable: packed solid
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(84), (uint)partitionSectors); // SizeTable
    WriteEntityId(buf, 88, ImplementationId, ImplementationSuffix);
    // The root directory counts: a freshly made empty volume reports one.
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(120), (uint)fileCount);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(124), (uint)directoryCount);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(128), UdfRevision); // MinimumUDFReadRevision
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(130), UdfRevision); // MinimumUDFWriteRevision
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(132), UdfRevision); // MaximumUDFWriteRevision
    FinalizeTag(buf, 0, LvidBodySize);
    output.Write(buf);
  }

  private void WriteFsd(Stream output, int lbn, int rootIcbLbn) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 256, (uint)lbn);
    WriteTimestamp(buf, 16, RecordingTime);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(28), 3);   // InterchangeLevel
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(30), 3);   // MaximumInterchangeLevel
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(32), 1);   // CharacterSetList
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(36), 1);   // MaximumCharacterSetList
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(40), 0);   // FileSetNumber
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(44), 0);   // FileSetDescriptorNumber
    WriteCharacterSet(buf, 48);                                    // LogicalVolumeIdentifierCharacterSet
    OstaCompressedUnicode.WriteDString(buf, 112, 128, this.EffectiveVolumeIdentifier);
    WriteCharacterSet(buf, 240);                                   // FileSetCharacterSet
    OstaCompressedUnicode.WriteDString(buf, 304, 32, this.EffectiveVolumeIdentifier);
    WriteLongAd(buf, 400, Sector, (uint)rootIcbLbn);               // RootDirectoryICB
    WriteEntityId(buf, 416, "*OSTA UDF Compliant", DomainSuffix);
    FinalizeTag(buf, 0, VolumeDescriptorBodySize);
    output.Write(buf);
  }

  /// <summary>
  /// Writes the common part of an ECMA-167 §4/14.9 File Entry and returns the
  /// buffer, leaving the caller to append allocation descriptors.
  /// </summary>
  private static byte[] BeginFileEntry(Node node, byte fileType, uint permissions, ushort linkCount) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 261, (uint)node.FeLbn);
    // ICB tag (ECMA-167 §4/14.6). Strategy type 4 is the only one Linux's udf
    // driver supports; type 0 makes it refuse the entry outright.
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(20), 4);   // StrategyType
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(24), 1);   // MaximumNumberOfEntries
    buf[27] = fileType;
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(34), 0);   // Flags: short allocation descriptors
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(44), permissions);
    // FileLinkCount is at offset 48, after uid/gid/permissions. Offset 28 falls
    // inside the ICB tag and leaves the driver seeing zero links.
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(48), linkCount);
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(56), (ulong)node.DataLength);
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(64), (ulong)node.DataSectors); // LogicalBlocksRecorded
    WriteTimestamp(buf, 72, RecordingTime);                        // AccessTime
    WriteTimestamp(buf, 84, RecordingTime);                        // ModificationTime
    WriteTimestamp(buf, 96, RecordingTime);                        // AttributeTime
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(108), 1);  // Checkpoint
    WriteEntityId(buf, 128, ImplementationId, ImplementationSuffix);
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(160), node.UniqueId);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(168), 0);  // LengthOfExtendedAttributes
    return buf;
  }

  /// <summary>
  /// Writes a directory File Entry (file type 4). The link count is the
  /// parent's reference plus one per subdirectory child, matching ECMA-167's
  /// accounting of incoming directory links.
  /// </summary>
  private static void WriteDirectoryFe(Stream output, Node dir) {
    var subDirCount = dir.Children.Count(c => c.IsDirectory);
    var buf = BeginFileEntry(dir, fileType: 4, DirectoryPermissions, (ushort)(1 + subDirCount));

    // One short allocation descriptor per block, the last one only as long as
    // the directory's remaining bytes. OSTA UDF §2.3.10.1 requires every extent
    // but the last to be a whole number of blocks; making the last one whole
    // too would claim bytes the information length says are not there.
    var lAd = WriteExtents(buf, 176, dir.DataLbn, dir.DataLength);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(172), (uint)lAd);
    FinalizeTag(buf, 0, FeHeaderBodySize + lAd);
    output.Write(buf);
  }

  private static void WriteFileFe(Stream output, Node file) {
    var buf = BeginFileEntry(file, fileType: 5, FilePermissions, linkCount: 1);
    var lAd = WriteExtents(buf, 176, file.DataLbn, file.DataLength);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(172), (uint)lAd);
    FinalizeTag(buf, 0, FeHeaderBodySize + lAd);
    output.Write(buf);
  }

  /// <summary>
  /// Writes the short allocation descriptors covering <paramref name="length" />
  /// contiguous bytes from <paramref name="firstLbn" /> and returns how many
  /// bytes of descriptors that took. A zero-length object gets none at all.
  /// </summary>
  private static int WriteExtents(byte[] buf, int offset, int firstLbn, long length) {
    var written = 0;
    var lbn = firstLbn;
    var remaining = length;
    while (remaining > 0) {
      var take = Math.Min(remaining, MaxExtentBytes);
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + written), (uint)take);
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + written + 4), (uint)lbn);
      written += 8;
      remaining -= take;
      lbn += (int)((take + Sector - 1) / Sector);
    }

    return written;
  }
}
