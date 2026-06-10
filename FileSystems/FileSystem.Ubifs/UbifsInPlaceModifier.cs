#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Registry;

namespace FileSystem.Ubifs;

/// <summary>
/// True in-place modifier for UBIFS images emitted by <see cref="UbifsWriter"/>.
/// Performs Add / Replace / Remove by appending fresh INO / DENT / DATA nodes
/// at the next free position in the journal log, with monotonically increasing
/// <c>sqnum</c>. The reader visits nodes in linear order so the highest-sqnum
/// entry for any given inode / dentry / data-block key naturally wins.
/// </summary>
/// <remarks>
/// <para><b>Spec semantic (kernel <c>fs/ubifs/journal.c</c>):</b> UBIFS is a
/// wandering-tree, log-structured filesystem. Every mutation appends new nodes
/// to the journal head — existing committed nodes are NEVER overwritten until
/// commit-merge collapses them out. This modifier preserves that invariant:
/// every byte of every node written by the original writer stays byte-identical
/// at its original offset after Add / Replace / Remove.</para>
///
/// <para><b>What changes on disk:</b> only the 0xFF padding tail of the last
/// in-use LEB (the journal head) gets overwritten with the new nodes; if the
/// new nodes spill past the current LEB the image grows by additional LEBs of
/// nodes followed by 0xFF padding to the LEB boundary. Nodes are 8-byte
/// aligned and never straddle an LEB boundary (per UBIFS layout rules).</para>
///
/// <para><b>What's NOT done (out of scope — multi-week wandering-tree commit):</b>
/// TNC (Tree Node Cache) index B+tree update, LPT (LEB Properties Tree) free-space
/// accounting, commit-start / reference / orphan node emission, journal-head
/// rotation, garbage-collection of obsoleted nodes. A real kernel mount would
/// reject this image; the in-tree linear-scan reader round-trips it correctly
/// because last-sqnum-wins is exactly what TNC lookups would compute after a
/// hypothetical commit-merge.</para>
/// </remarks>
public static class UbifsInPlaceModifier {

  // Mirror UbifsWriter constants we depend on.
  private const uint NodeMagic = UbifsWriter.NodeMagic;
  private const int CommonHeaderSize = UbifsWriter.CommonHeaderSize;
  private const int BlockSize = UbifsWriter.BlockSize;

  private const byte NodeTypeInode = UbifsWriter.NodeTypeInode;
  private const byte NodeTypeData = UbifsWriter.NodeTypeData;
  private const byte NodeTypeDentry = UbifsWriter.NodeTypeDentry;
  private const byte NodeTypeSuperblock = UbifsWriter.NodeTypeSuperblock;

  private const uint KeyTypeIno = UbifsWriter.KeyTypeIno;
  private const uint KeyTypeData = UbifsWriter.KeyTypeData;
  private const uint KeyTypeDent = UbifsWriter.KeyTypeDent;

  private const ushort ComprNone = UbifsWriter.ComprNone;
  private const ushort ComprZlib = UbifsWriter.ComprZlib;

  private const uint ModeFile = UbifsWriter.ModeFile;
  private const byte DtReg = UbifsWriter.DtReg;

  private const int InodeNodeSize = UbifsWriter.InodeNodeSize;
  private const int DentryFixedSize = UbifsWriter.DentryFixedSize;
  private const int DataFixedSize = UbifsWriter.DataFixedSize;

  private const int DefaultLebSize = UbifsWriter.DefaultLebSize;

  /// <summary>
  /// Result of scanning an existing UBIFS image so we know where the journal
  /// head is, what sqnum to start from, and what inode numbers are already taken.
  /// </summary>
  private sealed class ImageState {
    public int LebSize;
    public ulong NextSqnum;
    public uint NextInode;
    /// <summary>Live (sqnum-winning) name → inode map, filtered to non-tombstones.</summary>
    public Dictionary<string, uint> LiveNamesToInode = new(StringComparer.Ordinal);
    /// <summary>Sqnum-winning per-name dentry record (parent + sqnum + inum).</summary>
    public Dictionary<string, (uint Parent, uint Inum, ulong Sqnum)> LatestDentByName = new(StringComparer.Ordinal);
    /// <summary>Per-name child inode + parent inode of the latest live dentry. Used to pick the parent for new data nodes.</summary>
    public uint RootInode = 1;
  }

  /// <summary>
  /// Appends DENT + INO + DATA nodes for each input. Files that already exist
  /// (by archive name) are routed through <see cref="ReplaceFile"/> so the
  /// existing inode # is reused and only DATA nodes are appended. New files
  /// get fresh inode numbers (max-seen + 1).
  /// </summary>
  public static void AddFiles(Stream image, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("UBIFS in-place modify requires a read/write/seek stream.", nameof(image));

    var state = ScanState(image);
    var appendPos = FindAppendPosition(image, state.LebSize);
    var sqnum = state.NextSqnum;
    var nextInode = state.NextInode;

    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var name = LeafName(input.ArchiveName);
      if (name.Length == 0) continue;
      var content = input.ReadContent();

      // If already present, replace by appending fresh INO + DATA nodes for the
      // existing inode and a fresh DENT (latest-sqnum wins). No new inode # is
      // burned in that case.
      if (state.LatestDentByName.TryGetValue(name, out var existing) && existing.Inum != 0) {
        var inum = existing.Inum;
        AppendInodeAndData(image, ref appendPos, ref sqnum, state.LebSize, inum, content);
        // Also re-write a dentry so the parent → child link stays the highest-sqnum.
        AppendDentry(image, ref appendPos, ref sqnum, state.LebSize, parent: existing.Parent, child: inum, dt: DtReg, name);
        state.LatestDentByName[name] = (existing.Parent, inum, sqnum - 1);
        continue;
      }

      var fileInode = nextInode++;
      AppendInodeAndData(image, ref appendPos, ref sqnum, state.LebSize, fileInode, content);
      AppendDentry(image, ref appendPos, ref sqnum, state.LebSize, parent: state.RootInode, child: fileInode, dt: DtReg, name);
      state.LatestDentByName[name] = (state.RootInode, fileInode, sqnum - 1);
    }
  }

  /// <summary>
  /// Appends fresh DATA nodes for an existing file's inode (same inode #, new
  /// sqnum) so reader's last-write-wins picks the new content for every block
  /// the new payload covers. Old DATA nodes stay byte-identical at their
  /// original offsets — only the journal tail grows.
  /// </summary>
  public static void ReplaceFile(Stream image, string name, byte[] newData) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(newData);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("UBIFS in-place modify requires a read/write/seek stream.", nameof(image));

    var leaf = LeafName(name);
    var state = ScanState(image);
    if (!state.LatestDentByName.TryGetValue(leaf, out var existing) || existing.Inum == 0)
      throw new FileNotFoundException($"UBIFS entry '{leaf}' not present (or already tombstoned).");

    var appendPos = FindAppendPosition(image, state.LebSize);
    var sqnum = state.NextSqnum;
    AppendInodeAndData(image, ref appendPos, ref sqnum, state.LebSize, existing.Inum, newData);
  }

  /// <summary>
  /// Appends a tombstone DENT (<c>inum=0</c>) for each named entry. The reader's
  /// last-write-wins behaviour drops the entry from the listing because the
  /// tombstone has the highest sqnum. Old DENT and INO nodes stay byte-identical
  /// at their original offsets.
  /// </summary>
  public static void RemoveFiles(Stream image, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("UBIFS in-place modify requires a read/write/seek stream.", nameof(image));

    var state = ScanState(image);
    var appendPos = FindAppendPosition(image, state.LebSize);
    var sqnum = state.NextSqnum;

    foreach (var raw in entryNames) {
      if (string.IsNullOrEmpty(raw)) continue;
      var leaf = LeafName(raw);
      if (!state.LatestDentByName.TryGetValue(leaf, out var existing) || existing.Inum == 0) {
        // Nothing to tombstone (already absent or already tombstoned).
        continue;
      }
      // Tombstone: dentry with inum=0 under the same parent + name + new sqnum.
      AppendDentry(image, ref appendPos, ref sqnum, state.LebSize, parent: existing.Parent, child: 0, dt: DtReg, leaf);
      state.LatestDentByName[leaf] = (existing.Parent, 0, sqnum - 1);
    }
  }

  // ── State scan ────────────────────────────────────────────────────────────

  /// <summary>
  /// Reads the existing image into memory once, walks every node in linear
  /// order, and returns the data we need to drive append-based mutation.
  /// </summary>
  private static ImageState ScanState(Stream image) {
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var buf = ms.GetBuffer();
    var len = (int)ms.Length;
    var span = buf.AsSpan(0, len);

    var state = new ImageState {
      LebSize = ReadLebSizeFromSuperblock(span),
      NextSqnum = 1,
      NextInode = 2,
    };

    var maxInode = 1u; // root
    ulong maxSqnum = 0;

    for (var off = 0; off + CommonHeaderSize <= span.Length; ++off) {
      if (BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off, 4)) != NodeMagic) continue;
      var nodeLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 16, 4));
      if (nodeLen < CommonHeaderSize || nodeLen > span.Length - off) continue;
      var nodeType = span[off + 20];
      var sqnum = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(off + 8, 8));
      if (sqnum > maxSqnum) maxSqnum = sqnum;

      switch (nodeType) {
        case NodeTypeInode: {
          var inum = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 24, 4));
          if (inum > maxInode) maxInode = inum;
          break;
        }
        case NodeTypeDentry: {
          if (off + DentryFixedSize <= span.Length) {
            var parent = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 24, 4));
            var child = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 40, 4));
            var nlen = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(off + 50, 2));
            if (nlen > 0 && nlen <= 255 && off + DentryFixedSize + nlen <= span.Length) {
              var name = Encoding.UTF8.GetString(span.Slice(off + DentryFixedSize, nlen));
              if (!state.LatestDentByName.TryGetValue(name, out var prev) || sqnum > prev.Sqnum)
                state.LatestDentByName[name] = (parent, child, sqnum);
            }
            if (child > maxInode) maxInode = child;
          }
          break;
        }
      }

      off += nodeLen - 1;
    }

    state.NextSqnum = maxSqnum + 1;
    state.NextInode = maxInode + 1;
    return state;
  }

  private static int ReadLebSizeFromSuperblock(ReadOnlySpan<byte> image) {
    // Linear scan to find a superblock node and read its leb_size field at +24+8.
    for (var off = 0; off + 64 <= image.Length; ++off) {
      if (BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off, 4)) != NodeMagic) continue;
      if (image[off + 20] != NodeTypeSuperblock) continue;
      var leb = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off + 24 + 8, 4));
      if (leb >= 4096 && (leb & (leb - 1)) == 0) return leb;
    }
    return DefaultLebSize;
  }

  /// <summary>
  /// Finds the first 8-byte-aligned 0xFF run that follows the last in-use node,
  /// i.e. the journal head where the next appended node should land. Falls back
  /// to "extend by one LEB at the current image length" if the existing image
  /// has no padding gap to write into.
  /// </summary>
  private static long FindAppendPosition(Stream image, int lebSize) {
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var buf = ms.GetBuffer();
    var len = (int)ms.Length;
    if (len == 0) return 0;
    var span = buf.AsSpan(0, len);

    // Walk forward through every node to find the position past the last one.
    var pastLastNode = 0;
    for (var off = 0; off + CommonHeaderSize <= span.Length; ++off) {
      if (BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off, 4)) != NodeMagic) continue;
      var nodeLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 16, 4));
      if (nodeLen < CommonHeaderSize || nodeLen > span.Length - off) continue;
      pastLastNode = off + nodeLen;
      off += nodeLen - 1;
    }

    if (pastLastNode == 0) return len; // no nodes — append at EOF

    // Align to 8 bytes (UBIFS obj-align).
    var aligned = (pastLastNode + 7) & ~7;
    return aligned;
  }

  // ── Append primitives ─────────────────────────────────────────────────────

  /// <summary>
  /// Appends an INO node followed by one DATA node per 4 KiB block of the
  /// payload. Caller advances <paramref name="appendPos"/> + <paramref name="sqnum"/>.
  /// </summary>
  private static void AppendInodeAndData(Stream image, ref long appendPos, ref ulong sqnum, int lebSize, uint inum, byte[] data) {
    var ino = BuildInodeNode(ref sqnum, inum, ModeFile | 0x01A4, (ulong)data.Length);
    WriteNodeAtJournalHead(image, ref appendPos, lebSize, ino);

    for (var blockIdx = 0u; (long)blockIdx * BlockSize < data.Length; ++blockIdx) {
      var start = (int)blockIdx * BlockSize;
      var chunkLen = Math.Min(BlockSize, data.Length - start);
      var chunk = new byte[chunkLen];
      Array.Copy(data, start, chunk, 0, chunkLen);
      var dataNode = BuildDataNode(ref sqnum, inum, blockIdx, chunk);
      WriteNodeAtJournalHead(image, ref appendPos, lebSize, dataNode);
    }
  }

  private static void AppendDentry(Stream image, ref long appendPos, ref ulong sqnum, int lebSize, uint parent, uint child, byte dt, string name) {
    var dent = BuildDentryNode(ref sqnum, parent, child, dt, name);
    WriteNodeAtJournalHead(image, ref appendPos, lebSize, dent);
  }

  /// <summary>
  /// Writes a node at the current journal head. If the node would straddle an
  /// LEB boundary, pads the rest of the current LEB with 0xFF and starts on
  /// the next LEB (extending the image as needed). Advances
  /// <paramref name="appendPos"/> to the next 8-byte-aligned slot past the
  /// written node.
  /// </summary>
  private static void WriteNodeAtJournalHead(Stream image, ref long appendPos, int lebSize, byte[] node) {
    // Determine which LEB we're in and how far into it.
    var lebIndex = appendPos / lebSize;
    var posInLeb = (int)(appendPos - lebIndex * lebSize);

    if (posInLeb + node.Length > lebSize) {
      // Pad current LEB to its boundary with 0xFF and move to next LEB start.
      var padLen = lebSize - posInLeb;
      image.Position = appendPos;
      var pad = new byte[padLen];
      Array.Fill(pad, (byte)0xFF);
      image.Write(pad, 0, padLen);
      appendPos += padLen;
    }

    image.Position = appendPos;
    image.Write(node, 0, node.Length);
    appendPos += node.Length;

    // 8-byte align for the next node.
    var align = (int)(appendPos & 7);
    if (align != 0) {
      var fill = 8 - align;
      var padBuf = new byte[fill];
      Array.Fill(padBuf, (byte)0xFF);
      image.Position = appendPos;
      image.Write(padBuf, 0, fill);
      appendPos += fill;
    }

    image.Flush();
  }

  // ── Node builders (mirror UbifsWriter, ref-counter-driven sqnum) ─────────

  private static void StampHeader(byte[] node, byte type, ref ulong sqnum) {
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(0, 4), NodeMagic);
    BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(8, 8), sqnum++);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(16, 4), (uint)node.Length);
    node[20] = type;
    node[21] = 0;
    node[22] = 0;
    node[23] = 0;
  }

  private static void FinalizeCrc(byte[] node) {
    var crc = Crc32.Compute(node.AsSpan(8, node.Length - 8));
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(4, 4), crc);
  }

  private static byte[] BuildInodeNode(ref ulong sqnum, uint inum, uint mode, ulong size) {
    var node = new byte[InodeNodeSize];
    StampHeader(node, NodeTypeInode, ref sqnum);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(24, 4), inum);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(28, 4), KeyTypeIno << 29);
    BinaryPrimitives.WriteUInt64LittleEndian(node.AsSpan(48, 8), size);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(92, 4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(96, 4), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(100, 4), mode);
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(128, 2), ComprZlib);
    FinalizeCrc(node);
    return node;
  }

  private static byte[] BuildDentryNode(ref ulong sqnum, uint parent, uint child, byte dt, string name) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    if (nameBytes.Length > 255)
      throw new ArgumentException("Dentry name exceeds 255 bytes.", nameof(name));

    var len = DentryFixedSize + nameBytes.Length + 1;
    var node = new byte[len];
    StampHeader(node, NodeTypeDentry, ref sqnum);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(24, 4), parent);
    var keyHi = (KeyTypeDent << 29) | (NameHash(nameBytes) & 0x1FFFFFFFu);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(28, 4), keyHi);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(40, 4), child);
    node[48] = 0;
    node[49] = dt;
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(50, 2), (ushort)nameBytes.Length);
    nameBytes.CopyTo(node, DentryFixedSize);
    FinalizeCrc(node);
    return node;
  }

  private static byte[] BuildDataNode(ref ulong sqnum, uint inum, uint blockIdx, byte[] uncompressed) {
    var compressed = UbifsWriter.ZlibCompress(uncompressed);
    byte[] payload;
    ushort comprType;
    if (compressed.Length < uncompressed.Length) {
      payload = compressed;
      comprType = ComprZlib;
    } else {
      payload = uncompressed;
      comprType = ComprNone;
    }

    var len = DataFixedSize + payload.Length;
    var node = new byte[len];
    StampHeader(node, NodeTypeData, ref sqnum);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(24, 4), inum);
    var keyHi = (KeyTypeData << 29) | (blockIdx & 0x1FFFFFFFu);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(28, 4), keyHi);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(40, 4), (uint)uncompressed.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(44, 2), comprType);
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(46, 2), (ushort)Math.Min(payload.Length, ushort.MaxValue));
    payload.CopyTo(node, DataFixedSize);
    FinalizeCrc(node);
    return node;
  }

  private static uint NameHash(byte[] name) {
    uint a = 0;
    foreach (var b in name) a += b * 11u;
    return a & 0x1FFFFFFFu;
  }

  private static string LeafName(string name) {
    var leaf = name;
    var slash = Math.Max(leaf.LastIndexOf('/'), leaf.LastIndexOf('\\'));
    if (slash >= 0) leaf = leaf[(slash + 1)..];
    return leaf;
  }
}
