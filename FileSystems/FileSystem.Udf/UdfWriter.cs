#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Udf;

/// <summary>
/// Writes a minimal UDF 1.02 filesystem image (ECMA-167). Builds a real
/// directory tree from slash-separated file paths, short allocation
/// descriptors. Computes ECMA-167 §7.2.1 DescriptorCRC
/// (CRC-16/CCITT, init=0, poly=0x1021, non-reflected) and TagChecksum for
/// every descriptor tag so that strict readers (xorriso, Linux udf.ko,
/// mkudffs fsck) accept the produced images.
///
/// Layout:
/// <code>
/// Sectors   0-15:  System area
/// Sector   16:     VRS BEA01
/// Sector   17:     VRS NSR02
/// Sector   18:     VRS TEA01
/// Sector   32-35:  Main VDS (PVD + Partition + LVD + Terminator)
/// Sector  256:     AVDP
/// Sector  257:     Partition start: File Set Descriptor (FSD) at LBN 0
/// Sector  258:     Root directory File Entry at LBN 1
/// Sector  259+:    Per-node File Entries, directory FID data, file data
/// </code>
///
/// A directory's data is a sequence of File Identifier Descriptors (FID,
/// tag 257). The first FID of every directory is the parent entry (Parent
/// flag 0x08, zero-length identifier, ICB pointing at the parent FE). Every
/// directory and file is a File Entry (FE, tag 261); directories carry file
/// type 4, regular files file type 5. A subdirectory FID carries the
/// Directory flag 0x02 and points at the child directory's FE.
/// </summary>
public sealed class UdfWriter {
  private const int Sector = 2048;
  private const int PartitionStartSector = 257;

  // Descriptor body sizes per ECMA-167 §10. The body starts at offset 16
  // (after the 16-byte descriptor tag) and DescriptorCRCLength covers
  // exactly these many bytes. Using fixed structure sizes (rather than
  // the full sector) keeps us compatible with real UDF implementations.
  private const int PvdBodySize = 496;          // AVDP/PVD/PD sector size 512 - 16 tag
  private const int AvdpBodySize = 496;
  private const int PdBodySize = 496;
  private const int LvdBodySize = 440 - 16;     // 440 header + zero partition maps
  private const int TerminatorBodySize = 496;
  private const int FsdBodySize = 496;
  private const int FeBodyHeader = 160;         // 176 - 16 (up to L_EA), plus L_EA + L_AD content

  private readonly List<(string name, byte[] data)> _files = [];

  /// <summary>
  /// ECMA-167 PVD Volume Identifier (dstring at PVD offset 24, 32 bytes).
  /// Linux's udf driver surfaces this as the volume label. Default "UDF Volume".
  /// Truncated to 31 bytes (ECMA-167 dstring length byte caps at 31).
  /// </summary>
  public string VolumeIdentifier { get; set; } = "UDF Volume";

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    _files.Add((name, data));
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
    public readonly List<Node> Children = [];
    public Node? Parent;

    // Assigned during layout (LBNs relative to the partition start).
    public int FeLbn;          // File Entry block
    public int DataLbn;        // first data block (FID data for dirs, payload for files)
    public int DataSectors;    // sectors occupied by data
    public int DataLength;     // exact byte length of the (directory or file) data
  }

  public void WriteTo(Stream output) {
    var root = BuildTree();

    // LBN 0 is the FSD; the root File Entry lives at LBN 1. Everything else
    // (per-node FEs, directory FID data, file payloads) is laid out after.
    root.FeLbn = 1;
    var nextLbn = 2;
    AssignLayout(root, ref nextLbn);

    var totalPartitionSectors = nextLbn; // LBNs 0..nextLbn-1 are all in use
    var totalImageSectors = PartitionStartSector + totalPartitionSectors;

    // ── Write system area (sectors 0-15) ──
    WritePadding(output, 16);

    // ── Write VRS (sectors 16-18) ──
    WriteVrs(output, "BEA01");
    WriteVrs(output, "NSR02");
    WriteVrs(output, "TEA01");

    // ── Padding to sector 32 ──
    WritePadding(output, 32 - 19);

    // ── Main VDS at sectors 32-35 ──
    this.WritePvd(output, 32, totalImageSectors);
    WritePartitionDescriptor(output, 33, PartitionStartSector, totalPartitionSectors);
    WriteLvd(output, 34);
    WriteTerminator(output, 35);

    // ── Padding to sector 256 ──
    WritePadding(output, 256 - 36);

    // ── AVDP at sector 256 ──
    WriteAvdp(output, 256, mainVdsLoc: 32, mainVdsLen: 4 * Sector);

    // ── Partition data (starting at sector 257 = LBN 0) ──
    WriteFsd(output, lbn: 0, rootIcbLbn: root.FeLbn);

    // Emit blocks in LBN order so the stream stays sequential. Gather every
    // block-producing action keyed by its starting LBN, then drain in order.
    blocksOutput = output;
    var blocks = new SortedDictionary<int, Action>();
    CollectBlocks(root, blocks);

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
  }

  /// <summary>
  /// Builds the directory tree from the recorded slash-separated paths,
  /// creating intermediate directory nodes on demand.
  /// </summary>
  private Node BuildTree() {
    var root = new Node { Name = "", IsDirectory = true };

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
      current.Children.Add(new Node {
        Name = fileName,
        IsDirectory = false,
        Data = data,
        Parent = current,
      });
    }

    return root;
  }

  /// <summary>
  /// Assigns File Entry and data block numbers depth-first. The node's own FE
  /// LBN must already be set by the caller; this method assigns FE LBNs for
  /// all children first (so a directory's FID data can reference them), then
  /// the directory's own FID-data block(s), then recurses.
  /// </summary>
  private void AssignLayout(Node node, ref int nextLbn) {
    if (node.IsDirectory) {
      // Reserve FE blocks for every child up front.
      foreach (var child in node.Children)
        child.FeLbn = nextLbn++;

      // Reserve this directory's FID-data block(s).
      var fidData = BuildFidData(node);
      node.DataLength = fidData.Length;
      node.DataSectors = Math.Max(1, (fidData.Length + Sector - 1) / Sector);
      node.DataLbn = nextLbn;
      nextLbn += node.DataSectors;

      // Recurse into children so their own subtree blocks are laid out.
      foreach (var child in node.Children)
        AssignLayout(child, ref nextLbn);
    } else {
      node.DataLength = node.Data.Length;
      node.DataSectors = Math.Max(1, (node.Data.Length + Sector - 1) / Sector);
      node.DataLbn = nextLbn;
      nextLbn += node.DataSectors;
    }
  }

  /// <summary>
  /// Queues the block-emitting actions for a node and its subtree, keyed by
  /// the starting LBN of each block group, so the writer can drain them in
  /// strictly ascending order.
  /// </summary>
  private void CollectBlocks(Node node, SortedDictionary<int, Action> blocks) {
    if (node.IsDirectory) {
      // This directory's File Entry.
      var dirNode = node;
      blocks[node.FeLbn] = () => WriteDirectoryFe(blocksOutput, dirNode);

      // This directory's FID data.
      blocks[node.DataLbn] = () => {
        var fidData = BuildFidData(dirNode);
        blocksOutput.Write(fidData);
        var pad = dirNode.DataSectors * Sector - fidData.Length;
        if (pad > 0) blocksOutput.Write(new byte[pad]);
      };

      foreach (var child in node.Children)
        CollectBlocks(child, blocks);
    } else {
      var fileNode = node;
      blocks[node.FeLbn] = () => WriteFileFe(blocksOutput, fileNode.FeLbn, fileNode.Data.Length, fileNode.DataLbn);
      blocks[node.DataLbn] = () => {
        blocksOutput.Write(fileNode.Data);
        var pad = fileNode.DataSectors * Sector - fileNode.Data.Length;
        if (pad > 0) blocksOutput.Write(new byte[pad]);
      };
    }
  }

  // The output stream is captured for the duration of WriteTo so the block
  // actions stay simple closures; set before CollectBlocks runs.
  private Stream blocksOutput = Stream.Null;

  // ── FID building ──────────────────────────────────────────────────────────

  /// <summary>
  /// Builds the directory data for a directory node: a parent FID (zero-length
  /// identifier, parent + directory flags) pointing at the parent's FE,
  /// followed by one FID per child referencing the child's FE.
  /// <para>
  /// ECMA-167 §4/14.4: a File Identifier Descriptor may not cross a logical
  /// block boundary. When the next FID would straddle the current block, the
  /// remainder of that block is zero-padded and the FID starts at the next
  /// block. The returned buffer is therefore a multiple of the block size, so
  /// the directory spans whole blocks regardless of entry count.
  /// </para>
  /// </summary>
  private static byte[] BuildFidData(Node dir) {
    using var ms = new MemoryStream();

    // Parent FID (flags=0x0A: parent + directory). The parent of the root is
    // itself.
    var parentFeLbn = (dir.Parent ?? dir).FeLbn;
    WriteFidBlockAligned(ms, 0x0A, parentFeLbn, "");

    foreach (var child in dir.Children) {
      var flags = child.IsDirectory ? (byte)0x02 : (byte)0x00;
      WriteFidBlockAligned(ms, flags, child.FeLbn, child.Name);
    }

    return ms.ToArray();
  }

  /// <summary>
  /// Writes one FID, first padding to the next logical block boundary if the
  /// FID would otherwise cross it (ECMA-167 §14.4 forbids that crossing).
  /// </summary>
  private static void WriteFidBlockAligned(MemoryStream ms, byte flags, int icbLbn, string name) {
    var fidLen = FidLength(name);
    var posInBlock = (int)(ms.Length % Sector);
    if (posInBlock + fidLen > Sector) {
      var pad = Sector - posInBlock;
      ms.Write(new byte[pad]);
    }
    WriteFid(ms, flags, icbLbn, name);
  }

  /// <summary>Padded on-disk byte length of a FID for the given identifier.</summary>
  private static int FidLength(string name) {
    var nameLen = name.Length == 0 ? 0 : EncodeCs0(name).Length;
    return (38 + nameLen + 3) & ~3;
  }

  private static void WriteFid(Stream s, byte flags, int icbLbn, string name) {
    var nameBytes = name.Length == 0 ? [] : EncodeCs0(name);
    var fidLen = 38 + nameBytes.Length;
    var padded = (fidLen + 3) & ~3;
    var buf = new byte[padded];

    // Tag: FID = 257
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), 257);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 2); // descriptor version
    buf[18] = flags;
    buf[19] = (byte)nameBytes.Length; // identifier length
    // ICB at offset 20: long_ad (16 bytes) — length(4) + lbn(4) + partRef(2) + impl(6)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), (uint)Sector);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24), (uint)icbLbn);
    // lIU at offset 36 = 0
    // Name at offset 38
    nameBytes.CopyTo(buf, 38);

    // ECMA-167 §14.4: FID DescriptorCRCLength covers the entire padded FID
    // minus the 16-byte tag.
    FinalizeTag(buf, 0, padded - 16);

    s.Write(buf);
  }

  private static byte[] EncodeCs0(string name) {
    var utf8 = Encoding.UTF8.GetBytes(name);
    var result = new byte[1 + utf8.Length];
    result[0] = 8; // CS0 compression ID = UTF-8
    utf8.CopyTo(result, 1);
    return result;
  }

  // ── Descriptor writers ────────────────────────────────────────────────────

  // UDF domain entity-identifier suffix (UDF 1.02): UDFRevision(2 LE)=0x0102,
  // DomainFlags(1)=0, Reserved(5). Stamped after the "*OSTA UDF Compliant" id.
  private static readonly byte[] DomainSuffix = [0x02, 0x01, 0, 0, 0, 0, 0, 0];

  // ECMA-167 §7.4 EntityID (regid): Flags(1) + Identifier(23) + Suffix(8).
  private static void WriteRegid(byte[] buf, int off, string id, ReadOnlySpan<byte> suffix) {
    buf[off] = 0;
    var idb = Encoding.ASCII.GetBytes(id);
    Array.Copy(idb, 0, buf, off + 1, Math.Min(idb.Length, 23));
    if (!suffix.IsEmpty)
      suffix[..Math.Min(suffix.Length, 8)].CopyTo(buf.AsSpan(off + 24, 8));
  }

  // ECMA-167 §7.2.1 charspec: CharacterSetType(1)=0 (CS0) + CharacterSetInfo(63).
  private static void WriteCharspec(byte[] buf, int off) {
    buf[off] = 0;
    Encoding.ASCII.GetBytes("OSTA Compressed Unicode").CopyTo(buf, off + 1);
  }

  private static void WriteTag(byte[] buf, int off, ushort tagId, uint tagLocation) {
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off), tagId);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 2), 2); // descriptor version
    // DescriptorCRC (off+8..9), DescriptorCRCLength (off+10..11), TagChecksum (off+4)
    // filled in by FinalizeTag after the descriptor body is populated.
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 12), tagLocation);
  }

  /// <summary>
  /// Finalizes a UDF descriptor tag per ECMA-167 §7.2.1 by computing the
  /// CRC-16/CCITT (init=0, poly=0x1021, non-reflected) over <paramref name="bodyLength"/>
  /// bytes starting at <c>tagOffset + 16</c>, storing it in the tag at offsets 8..9,
  /// writing the DescriptorCRCLength at offsets 10..11, and finally computing the
  /// byte-sum-mod-256 TagChecksum at offset 4.
  /// </summary>
  private static void FinalizeTag(byte[] buf, int tagOffset, int bodyLength) {
    var bodyStart = tagOffset + 16;
    if (bodyStart + bodyLength > buf.Length)
      bodyLength = buf.Length - bodyStart;
    if (bodyLength < 0) bodyLength = 0;

    var crc = Crc16Ccitt.Compute(buf.AsSpan(bodyStart, bodyLength));
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tagOffset + 8), crc);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(tagOffset + 10), (ushort)bodyLength);

    // TagChecksum = (sum of bytes [0..3, 5..15]) mod 256. Byte at offset 4
    // is excluded (it IS the checksum) and must be zero while computing.
    buf[tagOffset + 4] = 0;
    byte sum = 0;
    for (var i = 0; i < 16; i++) {
      if (i == 4) continue;
      sum = (byte)(sum + buf[tagOffset + i]);
    }
    buf[tagOffset + 4] = sum;
  }

  private static void WritePadding(Stream output, int sectors) {
    for (var i = 0; i < sectors; i++) output.Write(new byte[Sector]);
  }

  private static void WriteVrs(Stream output, string id) {
    var buf = new byte[Sector];
    buf[0] = 0; // structure type
    Encoding.ASCII.GetBytes(id).CopyTo(buf, 1);
    buf[6] = 1; // structure version
    output.Write(buf);
  }

  private static void WriteAvdp(Stream output, int sectorNum, int mainVdsLoc, int mainVdsLen) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 2, (uint)sectorNum);
    // Main VDS extent: length(4) + location(4) at offset 16
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), (uint)mainVdsLen);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), (uint)mainVdsLoc);
    FinalizeTag(buf, 0, AvdpBodySize);
    output.Write(buf);
  }

  private void WritePvd(Stream output, int sectorNum, int totalSectors) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 1, (uint)sectorNum); // Primary Volume Descriptor
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), 1); // VDS number
    // Volume Identifier — ECMA-167 dstring (max 31 ASCII bytes + length byte at offset+31).
    var volId = string.IsNullOrEmpty(this.VolumeIdentifier) ? "UDF Volume" : this.VolumeIdentifier;
    if (volId.Length > 31) volId = volId[..31];
    Encoding.ASCII.GetBytes(volId).CopyTo(buf, 24);
    FinalizeTag(buf, 0, PvdBodySize);
    output.Write(buf);
  }

  private static void WritePartitionDescriptor(Stream output, int sectorNum, int partStart, int partLen) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 5, (uint)sectorNum);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), 1);          // VDS number
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(20), 1);          // partition flags = allocated
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(22), 0);          // partition number 0
    WriteRegid(buf, 24, "+NSR02", default);                              // partition contents (ECMA-167 §4)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(184), 1);         // access type = read-only
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(188), (uint)partStart);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(192), (uint)partLen);
    FinalizeTag(buf, 0, PdBodySize);
    output.Write(buf);
  }

  private void WriteLvd(Stream output, int sectorNum) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 6, (uint)sectorNum);
    WriteCharspec(buf, 20);                                              // descriptor character set (CS0)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(212), (uint)Sector); // logical block size
    WriteRegid(buf, 216, "*OSTA UDF Compliant", DomainSuffix);          // domain identifier (kernel-mandatory)
    // logical_volume_contents_use @248: FSD long_ad (length=4, lbn=4, partRef=2, impl=6)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(248), (uint)Sector); // extent length
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(252), 0); // FSD LBN = 0
    // partRef at 256 = 0 (default)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(264), 6);        // map table length (one Type-1 map)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(268), 1);        // number of partition maps
    WriteRegid(buf, 272, "*CompressionWorkbench", default);             // implementation identifier
    // Type-1 partition map @440: type(1)=1, length(1)=6, vol_seq(2)=1, part_num(2)=0
    buf[440] = 1;
    buf[441] = 6;
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(442), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(444), 0);
    // CRC must cover through the partition map (offset 16..446).
    FinalizeTag(buf, 0, 446 - 16);
    output.Write(buf);
  }

  private static void WriteTerminator(Stream output, int sectorNum) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 8, (uint)sectorNum);
    FinalizeTag(buf, 0, TerminatorBodySize);
    output.Write(buf);
  }

  private static void WriteFsd(Stream output, int lbn, int rootIcbLbn) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 256, (uint)lbn);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(28), 3);          // interchange level
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(30), 3);          // max interchange level
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(32), 1);          // charset list
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(36), 1);          // max charset list
    WriteCharspec(buf, 48);                                              // logical volume id charset (CS0)
    WriteCharspec(buf, 240);                                             // file set charset (CS0)
    // Root ICB: long_ad at offset 400
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(400), (uint)Sector);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(404), (uint)rootIcbLbn);
    WriteRegid(buf, 416, "*OSTA UDF Compliant", DomainSuffix);          // domain identifier (kernel-checked)
    FinalizeTag(buf, 0, FsdBodySize);
    output.Write(buf);
  }

  /// <summary>
  /// Writes a directory File Entry (file type 4). The file link count is set
  /// to 1 (the parent's reference) plus one per subdirectory child, matching
  /// ECMA-167's accounting of incoming directory links.
  /// </summary>
  private static void WriteDirectoryFe(Stream output, Node dir) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 261, (uint)dir.FeLbn);
    // ICB tag at offset 16: strategy_type(2)@20 must be 4 (the only type the
    // kernel supports; 0 → "unsupported strategy type"), max_entries(2)@24 = 1.
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(20), 4);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(24), 1);
    buf[27] = 4; // file type = directory
    // File link count is at FE offset 48 (ECMA-167 §14.9, after uid/gid/perms);
    // offset 28 falls inside the ICB tag and leaves the kernel seeing link
    // count 0 → "Error in udf_iget". Parent link + one per child directory.
    var subDirCount = dir.Children.Count(c => c.IsDirectory);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(48), (ushort)(1 + subDirCount));
    // icb flags at offset 34: adType=0 (short ADs)
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(34), 0);
    // info length at offset 56 — the directory's FID data is block-aligned, so
    // this covers whole blocks (one short AD each).
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(56), (ulong)dir.DataLength);

    // One short AD per logical block. ECMA-167 forbids a FID from crossing a
    // block boundary, so BuildFidData already padded the data to a block
    // multiple; describe each block with its own descriptor (length always a
    // full block). Multiple ADs lift the single-block directory cap.
    var blocks = dir.DataSectors;
    // Short ADs live inside the FE sector after the 176-byte header, so their
    // count is bounded by the sector. ~234 blocks ≈ 478 KiB of FID data, which
    // is thousands of small entries; beyond that an AD-continuation extent
    // would be required (not yet implemented).
    var maxAds = (Sector - 176) / 8;
    if (blocks > maxAds)
      throw new InvalidOperationException(
        $"UDF directory '{dir.Name}' needs {blocks} data blocks but only {maxAds} short " +
        "allocation descriptors fit in one File Entry; AD-continuation extents are not supported.");
    var lAd = blocks * 8;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(172), (uint)lAd);
    for (var b = 0; b < blocks; b++) {
      var adOff = 176 + b * 8;
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(adOff), (uint)Sector);          // extent length
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(adOff + 4), (uint)(dir.DataLbn + b)); // LBN
    }
    // File Entry body: 176-byte header (minus 16-byte tag) + L_EA(0) + L_AD bytes.
    FinalizeTag(buf, 0, FeBodyHeader + 0 + lAd);
    output.Write(buf);
  }

  private static void WriteFileFe(Stream output, int lbn, int fileSize, int dataLbn) {
    var buf = new byte[Sector];
    WriteTag(buf, 0, 261, (uint)lbn);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(20), 4); // ICB strategy type 4
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(24), 1); // max entries 1
    buf[27] = 5; // file type = file (regular)
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(48), 1); // file link count (ECMA-167 §14.9 offset)
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(34), 0); // adType=0 short
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(56), (ulong)fileSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(172), 8); // L_AD = 8
    var allocLen = Math.Max(fileSize, Sector); // at least one sector
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(176), (uint)allocLen);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(180), (uint)dataLbn);
    FinalizeTag(buf, 0, FeBodyHeader + 0 + 8);
    output.Write(buf);
  }
}
