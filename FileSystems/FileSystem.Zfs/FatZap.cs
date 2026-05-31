#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Zfs;

/// <summary>
/// Fat-ZAP block layout — the form a ZAP object takes once its entries no longer fit in a
/// single micro-ZAP block. A fat ZAP is a sequence of equally sized blocks within the ZAP
/// object's data:
/// <code>
///   block 0           zap_phys_t header (ZBT_HEADER) + embedded leaf pointer table
///   block 1 .. N      zap_leaf_phys_t leaves (ZBT_LEAF), each chunked name/value storage
/// </code>
/// <para>
/// Per <c>include/sys/zap_impl.h</c> and <c>include/sys/zap_leaf.h</c>:
/// </para>
/// <code>
/// struct zap_phys {
///   u64 zap_block_type;       // ZBT_HEADER
///   u64 zap_magic;            // 0x2F52AB2AB
///   struct zap_table_phys {   // zap_ptrtbl
///     u64 zt_blk; u64 zt_numblks; u64 zt_shift; u64 zt_nextblk; u64 zt_blks_copied;
///   } zap_ptrtbl;
///   u64 zap_freeblk;
///   u64 zap_num_leaves;
///   u64 zap_num_entries;
///   u64 zap_salt;
///   u64 zap_normflags;
///   u64 zap_flags;
///   // second half of the block: embedded pointer table (1 &lt;&lt; zt_shift entries) when
///   // zt_blk == 0.
/// };
/// </code>
/// <para>
/// When the pointer table is embedded (the common case for moderately sized directories),
/// the table occupies the second half of the header block and indexes a leaf by the top
/// <c>zt_shift</c> bits of the 64-bit ZAP hash.
/// </para>
/// <para>
/// Each leaf is a <c>zap_leaf_phys_t</c>: a fixed header, a chain-head hash table, then an
/// array of <c>zap_leaf_chunk</c> slots. Entries are <c>ZAP_CHUNK_ENTRY</c> chunks that
/// reference <c>ZAP_CHUNK_ARRAY</c> chunks holding the name bytes and the 8-byte value.
/// Entries that hash into the same leaf bucket are singly linked through
/// <c>le_next</c> / the leaf's <c>l_hash</c> chain heads.
/// </para>
/// <para>
/// The values stored are the directory-entry encoding used by the rest of the writer:
/// <c>(type &lt;&lt; 60) | objId</c>, matching the micro-ZAP.
/// </para>
/// </summary>
internal static class FatZap {
  // zap_phys_t field offsets.
  private const int PhysBlockType = 0x00;
  private const int PhysMagic = 0x08;
  private const int PhysPtrtblBlk = 0x10;
  private const int PhysPtrtblNumblks = 0x18;
  private const int PhysPtrtblShift = 0x20;
  private const int PhysFreeblk = 0x38;
  private const int PhysNumLeaves = 0x40;
  private const int PhysNumEntries = 0x48;
  private const int PhysSalt = 0x50;

  // zap_leaf_phys_t header (zap_leaf_header).
  private const int LeafHdrBlockType = 0x00; // u64 lh_block_type (ZBT_LEAF)
  private const int LeafHdrMagic = 0x10;      // u64 lh_magic
  private const int LeafHdrNfree = 0x18;      // u16
  private const int LeafHdrNentries = 0x1A;   // u16
  private const int LeafHdrPrefixLen = 0x1C;  // u16
  private const int LeafHdrFreelist = 0x1E;   // u16
  private const int LeafHeaderSize = 0x30;    // 48 bytes, then l_hash[] follows
  private const ulong LeafMagic = 0x2AB1EAFUL;

  // Chunk geometry. ZAP_LEAF_CHUNKSIZE = 24 in OpenZFS.
  private const int ChunkSize = 24;
  private const byte ChunkEntry = 252; // ZAP_CHUNK_ENTRY
  private const byte ChunkArray = 251; // ZAP_CHUNK_ARRAY
  private const byte ChunkFree = 253;  // ZAP_CHUNK_FREE
  private const int ArrayBytesPerChunk = 21; // ZAP_LEAF_ARRAY_BYTES
  private const ushort ChunkNull = 0xFFFF;   // CHAIN_END

  /// <summary>The salt baked into images so the layout is reproducible.</summary>
  private const ulong Salt = 0x1234567890ABCDEFUL;

  /// <summary>
  /// Result of encoding a fat ZAP: the header block (block 0) followed by the leaf blocks,
  /// each exactly <paramref name="BlockSize"/> bytes, concatenated in block order.
  /// </summary>
  public sealed record EncodeResult(byte[] Body, int BlockSize, int BlockCount);

  /// <summary>
  /// Salted ZAP name hash, mirroring OpenZFS <c>zap_hash()</c> for the no-normalisation case:
  /// a rotating CRC-ish fold over the name bytes seeded by the salt, returning a 64-bit hash
  /// whose <em>high</em> bits select the leaf.
  /// </summary>
  public static ulong Hash(ulong salt, string name) {
    // OpenZFS uses a table-driven CRC fold; here we use a deterministic 64-bit mix that the
    // reader reproduces identically. Placement only needs to be stable, not bit-compatible
    // with the kernel, since this writer/reader pair owns both ends.
    var h = salt;
    foreach (var ch in Encoding.UTF8.GetBytes(name)) {
      h = (h >> 8) ^ Crc64Table[(byte)(h ^ ch)];
    }
    // Keep the hash in the top bits so the pointer-table index (top zt_shift bits) varies.
    return h;
  }

  private static readonly ulong[] Crc64Table = BuildCrc64Table();

  private static ulong[] BuildCrc64Table() {
    const ulong poly = 0xC96C5795D7870F42UL; // CRC-64/XZ reversed polynomial
    var table = new ulong[256];
    for (var i = 0; i < 256; i++) {
      var crc = (ulong)i;
      for (var j = 0; j < 8; j++)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
      table[i] = crc;
    }
    return table;
  }

  /// <summary>
  /// Encodes <paramref name="entries"/> into a fat ZAP. Picks a power-of-two pointer-table
  /// size large enough that no single leaf overflows its chunk capacity, then assigns each
  /// entry to its leaf by the top bits of the salted hash.
  /// </summary>
  public static EncodeResult Encode(
    IReadOnlyList<(string Name, ulong Value)> entries, int blockSize = 4096) {

    if (blockSize < 512 || (blockSize & (blockSize - 1)) != 0)
      throw new ArgumentException("Fat-ZAP block size must be a power of two >= 512.", nameof(blockSize));

    var numChunks = NumChunks(blockSize);
    var numHash = NumHash(blockSize);
    var ptrtblShift = ChoosePtrtblShift(entries, blockSize, numChunks);
    var numPtrs = 1 << ptrtblShift;

    // The embedded pointer table must fit in the second half of the header block.
    var embeddedTableBytes = numPtrs * 8;
    if (embeddedTableBytes > blockSize / 2)
      throw new InvalidOperationException(
        $"Fat-ZAP pointer table ({numPtrs} entries) does not fit the header block.");

    // Group entries by leaf index. With an embedded table there is exactly one leaf per
    // pointer slot (no shared leaves / chaining of leaves needed), so leaf i lives at
    // block (i + 1).
    var perLeaf = new List<(string Name, ulong Value, ulong Hash)>[numPtrs];
    for (var i = 0; i < numPtrs; i++)
      perLeaf[i] = new List<(string, ulong, ulong)>();

    foreach (var (name, value) in entries) {
      var h = Hash(Salt, name);
      var idx = (int)(h >> (64 - ptrtblShift));
      perLeaf[idx].Add((name, value, h));
    }

    foreach (var bucket in perLeaf) {
      var need = bucket.Sum(e => ChunksForEntry(e.Name));
      if (need > numChunks)
        throw new InvalidOperationException(
          "Fat-ZAP leaf overflow — increase block size or pointer-table shift.");
    }

    var blockCount = 1 + numPtrs;
    var body = new byte[blockCount * blockSize];

    // ---- header block ----
    var hdr = body.AsSpan(0, blockSize);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(PhysBlockType, 8), ZfsConstants.ZbtHeader);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(PhysMagic, 8), ZfsConstants.ZapMagic);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(PhysPtrtblBlk, 8), 0); // embedded
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(PhysPtrtblNumblks, 8), 0);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(PhysPtrtblShift, 8), (ulong)ptrtblShift);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(PhysFreeblk, 8), (ulong)blockCount);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(PhysNumLeaves, 8), (ulong)numPtrs);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(PhysNumEntries, 8), (ulong)entries.Count);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(PhysSalt, 8), Salt);

    var table = hdr.Slice(blockSize / 2, embeddedTableBytes);
    for (var i = 0; i < numPtrs; i++)
      BinaryPrimitives.WriteUInt64LittleEndian(table.Slice(i * 8, 8), (ulong)(i + 1));

    // ---- leaf blocks ----
    for (var i = 0; i < numPtrs; i++)
      EncodeLeaf(body, (i + 1) * blockSize, numChunks, numHash, ptrtblShift, perLeaf[i]);

    return new EncodeResult(body, blockSize, blockCount);
  }

  // Span-typed parameters cannot be captured by local functions, so the leaf coders operate
  // on the backing array plus the leaf's base offset and index chunks by absolute offset.
  private static void EncodeLeaf(
    byte[] body, int baseOff, int numChunks, int numHash, int ptrtblShift,
    List<(string Name, ulong Value, ulong Hash)> bucket) {

    var leaf = body.AsSpan(baseOff);
    BinaryPrimitives.WriteUInt64LittleEndian(leaf.Slice(LeafHdrBlockType, 8), ZfsConstants.ZbtLeaf);
    BinaryPrimitives.WriteUInt64LittleEndian(leaf.Slice(LeafHdrMagic, 8), LeafMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(leaf.Slice(LeafHdrNentries, 2), (ushort)bucket.Count);

    var hashTableOffset = LeafHeaderSize;
    var chunkArrayOffset = hashTableOffset + numHash * 2;

    // Initialise chain heads to CHAIN_END.
    for (var i = 0; i < numHash; i++)
      BinaryPrimitives.WriteUInt16LittleEndian(leaf.Slice(hashTableOffset + i * 2, 2), ChunkNull);

    // Mark all chunks free initially (free tag in the first byte of each chunk).
    for (var i = 0; i < numChunks; i++)
      leaf[chunkArrayOffset + i * ChunkSize] = ChunkFree;

    var nextChunk = 0;

    int AllocChunk() {
      if (nextChunk >= numChunks)
        throw new InvalidOperationException("Fat-ZAP leaf chunk exhaustion.");
      return nextChunk++;
    }

    int ChunkOff(int idx) => baseOff + chunkArrayOffset + idx * ChunkSize;

    int WriteArray(byte[] data) {
      // Write data into a singly linked list of array chunks; return the head chunk index.
      var chunkCount = Math.Max(1, (data.Length + ArrayBytesPerChunk - 1) / ArrayBytesPerChunk);
      var chunks = new int[chunkCount];
      for (var c = 0; c < chunkCount; c++) chunks[c] = AllocChunk();

      for (var c = 0; c < chunkCount; c++) {
        var arr = body.AsSpan(ChunkOff(chunks[c]), ChunkSize);
        arr.Clear();
        arr[0] = ChunkArray;
        var srcOff = c * ArrayBytesPerChunk;
        var n = Math.Min(ArrayBytesPerChunk, data.Length - srcOff);
        if (n > 0) data.AsSpan(srcOff, n).CopyTo(arr.Slice(1, n));
        var next = c + 1 < chunkCount ? (ushort)chunks[c + 1] : ChunkNull;
        BinaryPrimitives.WriteUInt16LittleEndian(arr.Slice(1 + ArrayBytesPerChunk, 2), next);
      }
      return chunks[0];
    }

    foreach (var (name, value, hash) in bucket) {
      var nameBytes = Encoding.UTF8.GetBytes(name);
      var nameWithNul = new byte[nameBytes.Length + 1];
      nameBytes.CopyTo(nameWithNul, 0);

      var nameHeadChunk = WriteArray(nameWithNul);
      var valueBytes = new byte[8];
      BinaryPrimitives.WriteUInt64LittleEndian(valueBytes, value);
      var valueHeadChunk = WriteArray(valueBytes);

      // Entry chunk.
      var entryIdx = AllocChunk();
      var entry = body.AsSpan(ChunkOff(entryIdx), ChunkSize);
      entry.Clear();
      entry[0] = ChunkEntry;
      entry[1] = 8;                       // [1] value integer width (bytes)
      // [2..3] le_next (chain link, u16)
      BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(4, 2), (ushort)nameHeadChunk);  // le_name_chunk
      BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(6, 2), (ushort)nameWithNul.Length); // le_name_numints
      BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(8, 2), (ushort)valueHeadChunk); // le_value_chunk
      BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(10, 2), 1);                     // le_value_numints
      // [12..15] le_cd (u32)
      BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(16, 8), hash);                  // le_hash

      // Link into the chain bucket selected by the bits below the pointer-table prefix.
      var hashBucket = (int)((hash >> (64 - ptrtblShift - HashShiftFor(numHash))) & (ulong)(numHash - 1));
      var headOff = baseOff + hashTableOffset + hashBucket * 2;
      var head = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(headOff, 2));
      BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(2, 2), head);
      BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(headOff, 2), (ushort)entryIdx);
    }
  }

  /// <summary>
  /// Decodes a fat ZAP whose header block sits at the start of <paramref name="body"/>,
  /// followed by its leaf blocks. Returns the name/value pairs, ignoring chain order.
  /// </summary>
  public static List<(string Name, ulong Value)> Decode(byte[] body) {
    var result = new List<(string, ulong)>();
    if (body.Length < 16) return result;
    if (BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(PhysBlockType, 8)) != ZfsConstants.ZbtHeader)
      return result;
    if (BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(PhysMagic, 8)) != ZfsConstants.ZapMagic)
      return result;

    var ptrtblShift = (int)BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(PhysPtrtblShift, 8));
    var numLeaves = (int)BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(PhysNumLeaves, 8));
    var numPtrs = 1 << ptrtblShift;

    // Block size is the body length divided by the total block count (1 + numPtrs).
    var blockCount = 1 + numPtrs;
    if (body.Length % blockCount != 0) {
      // Pointer table may have fewer distinct leaves than slots; fall back to deriving the
      // block size from the embedded table extent assumption: blocks are equal-sized and the
      // total is (1 + numLeaves). Try that.
      blockCount = 1 + numLeaves;
      if (blockCount == 0 || body.Length % blockCount != 0)
        return result;
    }
    var blockSize = body.Length / blockCount;

    var numChunks = NumChunks(blockSize);
    var numHash = NumHash(blockSize);
    var seen = new HashSet<int>();

    var tableOffset = blockSize / 2;
    for (var p = 0; p < numPtrs; p++) {
      var leafBlk = (int)BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(tableOffset + p * 8, 8));
      if (leafBlk <= 0 || leafBlk >= blockCount) continue;
      if (!seen.Add(leafBlk)) continue; // shared leaf — decode once
      DecodeLeaf(body, leafBlk * blockSize, numChunks, numHash, result);
    }

    return result;
  }

  private static void DecodeLeaf(
    byte[] body, int baseOff, int numChunks, int numHash,
    List<(string, ulong)> result) {

    var leaf = body.AsSpan(baseOff);
    if (BinaryPrimitives.ReadUInt64LittleEndian(leaf.Slice(LeafHdrBlockType, 8)) != ZfsConstants.ZbtLeaf)
      return;
    if (BinaryPrimitives.ReadUInt64LittleEndian(leaf.Slice(LeafHdrMagic, 8)) != LeafMagic)
      return;

    var chunkArrayOffset = LeafHeaderSize + numHash * 2;
    int ChunkOff(int idx) => baseOff + chunkArrayOffset + idx * ChunkSize;

    byte[] ReadArray(int head, int byteCount) {
      var outBuf = new byte[byteCount];
      var written = 0;
      var c = head;
      var guard = 0;
      while (c != ChunkNull && c < numChunks && written < byteCount) {
        if (++guard > numChunks) break;
        var arr = body.AsSpan(ChunkOff(c), ChunkSize);
        if (arr[0] != ChunkArray) break;
        var n = Math.Min(ArrayBytesPerChunk, byteCount - written);
        arr.Slice(1, n).CopyTo(outBuf.AsSpan(written));
        written += n;
        c = BinaryPrimitives.ReadUInt16LittleEndian(arr.Slice(1 + ArrayBytesPerChunk, 2));
      }
      return outBuf;
    }

    for (var i = 0; i < numChunks; i++) {
      var chunk = body.AsSpan(ChunkOff(i), ChunkSize);
      if (chunk[0] != ChunkEntry) continue;

      var nameChunk = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(4, 2));
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(6, 2));
      var valueChunk = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(8, 2));

      var nameBytes = ReadArray(nameChunk, nameLen);
      var valueRaw = ReadArray(valueChunk, 8);
      if (valueRaw.Length < 8) continue;

      // Strip trailing NUL on the name.
      var nameSpan = nameBytes.AsSpan();
      var nul = nameSpan.IndexOf((byte)0);
      if (nul >= 0) nameSpan = nameSpan[..nul];
      var name = Encoding.UTF8.GetString(nameSpan);
      var value = BinaryPrimitives.ReadUInt64LittleEndian(valueRaw);
      result.Add((name, value));
    }
  }

  // ---- geometry helpers ----

  /// <summary>Number of chunk slots in a leaf of the given block size.</summary>
  internal static int NumChunks(int blockSize) {
    var avail = blockSize - LeafHeaderSize - NumHash(blockSize) * 2;
    return avail / ChunkSize;
  }

  /// <summary>Number of chain-head buckets in a leaf's hash table.</summary>
  internal static int NumHash(int blockSize) {
    // Roughly one bucket per two chunks; keep it a power of two for cheap masking.
    var approxChunks = (blockSize - LeafHeaderSize) / ChunkSize;
    var n = 1;
    while (n < approxChunks) n <<= 1;
    return Math.Max(8, n / 2);
  }

  private static int HashShiftFor(int numHash) {
    var shift = 0;
    while ((1 << shift) < numHash) shift++;
    return shift;
  }

  /// <summary>Chunks consumed by one entry: 1 entry chunk + name array chunks + 1 value chunk.</summary>
  private static int ChunksForEntry(string name) {
    var nameBytes = Encoding.UTF8.GetByteCount(name) + 1; // incl. NUL
    var nameChunks = Math.Max(1, (nameBytes + ArrayBytesPerChunk - 1) / ArrayBytesPerChunk);
    const int valueChunks = 1; // 8 bytes fit one array chunk
    return 1 + nameChunks + valueChunks;
  }

  /// <summary>
  /// Chooses the smallest pointer-table shift (power-of-two leaf count) such that, given the
  /// salted hash distribution, no leaf is expected to overflow its chunk capacity.
  /// </summary>
  private static int ChoosePtrtblShift(
    IReadOnlyList<(string Name, ulong Value)> entries, int blockSize, int numChunks) {

    for (var shift = 0; shift <= 16; shift++) {
      var numPtrs = 1 << shift;
      if (numPtrs * 8 > blockSize / 2) break; // table no longer fits header block
      var load = new int[numPtrs];
      var ok = true;
      foreach (var (name, _) in entries) {
        var h = Hash(Salt, name);
        var idx = (int)(h >> (64 - shift));
        if (shift == 0) idx = 0;
        load[idx] += ChunksForEntry(name);
        if (load[idx] > numChunks) { ok = false; break; }
      }
      if (ok) return shift;
    }
    throw new InvalidOperationException(
      "Fat-ZAP could not fit entries; directory exceeds the embedded-pointer-table capacity.");
  }
}
