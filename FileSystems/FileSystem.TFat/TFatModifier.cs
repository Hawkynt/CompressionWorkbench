#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.TFat;

/// <summary>
/// In-place modification of a TFAT image using the Microsoft Windows CE / Windows
/// Embedded Compact alternating-FAT transactional protocol. A single Add or
/// Remove operation reads the active FAT, applies the change into a private
/// in-memory copy that ultimately becomes the new inactive-then-active FAT, and
/// commits by a single 4-byte big-endian sequence-number write at the end of
/// that FAT region. A crash anywhere before the sequence write leaves the old
/// FAT (with the old sequence number) as the still-active copy, so the
/// transaction is invisible — no data loss, no metadata inconsistency.
///
/// <para>Step ordering per call:
/// <list type="number">
///   <item><description>Determine which physical FAT (0 or 1) is currently
///   active — the one whose trailing 4-byte BE sequence number is higher.</description></item>
///   <item><description>Read the active FAT into memory; compute the new FAT
///   image with the change applied (allocate or free clusters, update chains).</description></item>
///   <item><description>Allocate new clusters from the active FAT's free pool
///   (they're free in both copies because both started identical); copy file
///   bytes into those data clusters and flush.</description></item>
///   <item><description>Write or patch the directory entry. Single-sector
///   write — atomic at the device level.</description></item>
///   <item><description>Write the new FAT body into the inactive-FAT region
///   (everything except the trailing 4-byte sequence number). Flush.</description></item>
///   <item><description>Write the 4-byte sequence number = active.seq + 1
///   into the trailing 4 bytes of the formerly-inactive FAT. This is the
///   <b>atomic commit point</b>: after this single sector write, the new FAT
///   becomes active. Flush.</description></item>
/// </list>
/// </para>
///
/// <para>Crash semantics: a crash before step 6 leaves the old FAT (still with
/// the higher sequence) as active. A subsequent open reads the old FAT, ignores
/// the partial new-FAT writes, and the transaction is rolled back. The data
/// bytes written in step 3 become orphans (free clusters in the old FAT
/// pointing at data that is referenced by no chain) — these are harmless until
/// a future allocation overwrites them.</para>
///
/// <para>Limitations:</para>
/// <list type="bullet">
///   <item><description>Root-directory-only — no subdirectory traversal yet.</description></item>
///   <item><description>FAT32 root-cluster updates are not supported (CE TFAT
///   usage typically pins the root cluster). FAT12/16 fixed root is fully
///   supported.</description></item>
///   <item><description>Long-file-name support delegates to the writer's 8.3
///   alias; Add() rebuilds the dir entry as 8.3 only.</description></item>
/// </list>
/// </summary>
public static class TFatModifier {

  /// <summary>
  /// Atomically adds a file to the TFAT image using the alternating-FAT
  /// commit protocol. If a file with the same name already exists, it is
  /// removed in the same transaction (free old chain, allocate new chain,
  /// patch dir entry, commit).
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var ctx = TFatContext.Open(image);

    // 1. Read the active FAT into memory.
    var fatBuf = ctx.ReadActiveFat(image);

    // 2. Resolve any pre-existing entry with the same name; if found, free
    //    its chain in the working FAT and wipe the directory slot.
    var existingFirstCluster = -1;
    var dirEntryIndex = ctx.FindRootDirEntry(image, name, out var firstLfnIndex);
    if (dirEntryIndex >= 0) {
      var entryOff = ctx.RootDirOffset + dirEntryIndex * 32L;
      Span<byte> entry = stackalloc byte[32];
      image.Position = entryOff;
      image.ReadExactly(entry);
      existingFirstCluster = BinaryPrimitives.ReadUInt16LittleEndian(entry[26..]);
      if (ctx.FatType == 32)
        existingFirstCluster |= BinaryPrimitives.ReadUInt16LittleEndian(entry[20..]) << 16;
      if (existingFirstCluster >= 2)
        FreeChainInBuffer(fatBuf, ctx, existingFirstCluster);
    }

    // 3. Allocate clusters in the working FAT for the new payload and write
    //    chain links.
    var clustersNeeded = Math.Max(1, (data.Length + ctx.ClusterSize - 1) / ctx.ClusterSize);
    var newChain = AllocateClustersInBuffer(fatBuf, ctx, clustersNeeded);
    if (newChain.Count < clustersNeeded)
      throw new IOException($"TFAT: not enough free clusters for {clustersNeeded}-cluster file '{name}'.");
    for (var i = 0; i < newChain.Count; i++) {
      var next = (i + 1 < newChain.Count) ? newChain[i + 1] : ctx.EocMarker();
      WriteFatEntry(fatBuf, ctx, newChain[i], next);
    }

    // 4. Write file bytes into the allocated data clusters and flush. These
    //    writes go to the data area which is NOT versioned — but the clusters
    //    are free in the old (currently-active) FAT, so even if the
    //    transaction never commits, the old FAT sees them as free and a
    //    later allocation can reuse them.
    var written = 0;
    foreach (var c in newChain) {
      var off = ctx.ClusterToOffset(c);
      var chunk = Math.Min(ctx.ClusterSize, data.Length - written);
      if (chunk > 0) {
        image.Position = off;
        image.Write(data, written, chunk);
        written += chunk;
      }
      // Zero the remainder of the cluster (tip slack) for the final cluster
      // so leftover bytes from a previous deletion don't leak.
      if (chunk < ctx.ClusterSize) {
        var tail = ctx.ClusterSize - chunk;
        var zeros = new byte[tail];
        image.Position = off + chunk;
        image.Write(zeros);
      }
    }
    image.Flush();

    // 5. Patch (or write a fresh) directory entry. The dir entry sits in the
    //    fixed root directory area for FAT12/16 — a single-sector write that
    //    most devices treat as atomic. For TFAT, this happens before the FAT
    //    commit because directory entries are not versioned; a crash leaves
    //    the old FAT active, so even though the dir entry was written, the
    //    chain it references won't be allocated and reading it will safely
    //    bail.
    var (newDirIndex, oldFirstLfnIndex) = ctx.AllocateRootDirEntry(image, name, existingFirstCluster, firstLfnIndex, dirEntryIndex);
    ctx.WriteRootDirEntry(image, newDirIndex, name, newChain[0], data.Length);
    image.Flush();

    // 6. Write the new FAT body (everything except the trailing 4 sequence
    //    bytes) to the inactive FAT region and flush.
    image.Position = ctx.InactiveFatOffset;
    image.Write(fatBuf, 0, ctx.FatRegionLen - 4);
    image.Flush();

    // 7. Atomic commit: write seq = active.seq + 1 into the trailing 4 bytes
    //    of the formerly-inactive FAT. After this single write, the new FAT
    //    becomes active.
    Span<byte> seqBuf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(seqBuf, ctx.ActiveSequence + 1);
    image.Position = ctx.InactiveFatOffset + ctx.FatRegionLen - 4;
    image.Write(seqBuf);
    image.Flush();

    _ = newDirIndex;
    _ = oldFirstLfnIndex;
  }

  /// <summary>
  /// Atomically removes a file from the TFAT image using the alternating-FAT
  /// commit protocol. Frees the cluster chain in the working FAT, marks the
  /// directory entry deleted (0xE5), optionally wipes the data bytes, then
  /// commits by writing the new FAT and bumping the sequence number.
  /// </summary>
  public static void RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var ctx = TFatContext.Open(image);

    // 1. Find the directory entry.
    var dirIndex = ctx.FindRootDirEntry(image, name, out var firstLfnIndex);
    if (dirIndex < 0)
      throw new FileNotFoundException($"TFAT: file '{name}' not found in root directory.");

    // 2. Read the entry to get the first cluster.
    var entryOff = ctx.RootDirOffset + dirIndex * 32L;
    Span<byte> entry = stackalloc byte[32];
    image.Position = entryOff;
    image.ReadExactly(entry);
    var firstCluster = (int)BinaryPrimitives.ReadUInt16LittleEndian(entry[26..]);
    if (ctx.FatType == 32)
      firstCluster |= BinaryPrimitives.ReadUInt16LittleEndian(entry[20..]) << 16;

    // 3. Read the active FAT and compute the freed chain in the working copy.
    var fatBuf = ctx.ReadActiveFat(image);
    var chain = WalkChainInBuffer(fatBuf, ctx, firstCluster);
    foreach (var c in chain)
      WriteFatEntry(fatBuf, ctx, c, 0);

    // 4. Optionally wipe the cluster data bytes (secure-delete). Same
    //    rationale as Add: the data area is not versioned, but the clusters
    //    are about to become free in the new (committed) FAT — wiping them
    //    means no forensic trace remains after commit.
    if (wipeData) {
      var zeros = new byte[ctx.ClusterSize];
      foreach (var c in chain) {
        var off = ctx.ClusterToOffset(c);
        image.Position = off;
        image.Write(zeros);
      }
    }

    // 5. Mark the directory entries deleted (0xE5 first byte). LFN precursors
    //    too. Zero the rest of the slot bytes so the filename leaves no trace.
    var from = firstLfnIndex >= 0 ? firstLfnIndex : dirIndex;
    Span<byte> deleted = stackalloc byte[32];
    deleted.Clear();
    deleted[0] = 0xE5;
    for (var i = from; i <= dirIndex; i++) {
      image.Position = ctx.RootDirOffset + i * 32L;
      image.Write(deleted);
    }
    image.Flush();

    // 6. Write the new FAT body to the inactive FAT region (minus trailing seq).
    image.Position = ctx.InactiveFatOffset;
    image.Write(fatBuf, 0, ctx.FatRegionLen - 4);
    image.Flush();

    // 7. Atomic commit: bump sequence number.
    Span<byte> seqBuf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(seqBuf, ctx.ActiveSequence + 1);
    image.Position = ctx.InactiveFatOffset + ctx.FatRegionLen - 4;
    image.Write(seqBuf);
    image.Flush();
  }

  // ── In-memory FAT-buffer helpers ──────────────────────────────────────────

  private static void WriteFatEntry(byte[] buf, TFatContext ctx, int cluster, int value) {
    switch (ctx.FatType) {
      case 12: {
        var pos = cluster * 3 / 2;
        if (pos + 1 >= buf.Length) return;
        if ((cluster & 1) == 0) {
          buf[pos] = (byte)(value & 0xFF);
          buf[pos + 1] = (byte)((buf[pos + 1] & 0xF0) | ((value >> 8) & 0x0F));
        } else {
          buf[pos] = (byte)((buf[pos] & 0x0F) | ((value << 4) & 0xF0));
          buf[pos + 1] = (byte)((value >> 4) & 0xFF);
        }
        break;
      }
      case 16: {
        var pos = cluster * 2;
        if (pos + 2 <= buf.Length)
          BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos), (ushort)value);
        break;
      }
      default: {
        var pos = cluster * 4;
        if (pos + 4 <= buf.Length) {
          var existing = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
          var newVal = (existing & 0xF0000000u) | ((uint)value & 0x0FFFFFFFu);
          BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos), newVal);
        }
        break;
      }
    }
  }

  private static int ReadFatEntry(byte[] buf, TFatContext ctx, int cluster) {
    switch (ctx.FatType) {
      case 12: {
        var pos = cluster * 3 / 2;
        if (pos + 2 > buf.Length) return ctx.EocMarker();
        var v = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
        return (cluster & 1) != 0 ? v >> 4 : v & 0xFFF;
      }
      case 16: {
        var pos = cluster * 2;
        if (pos + 2 > buf.Length) return ctx.EocMarker();
        return BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
      }
      default: {
        var pos = cluster * 4;
        if (pos + 4 > buf.Length) return ctx.EocMarker();
        return BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos)) & 0x0FFFFFFF;
      }
    }
  }

  private static List<int> WalkChainInBuffer(byte[] buf, TFatContext ctx, int startCluster) {
    var chain = new List<int>();
    var c = startCluster;
    var seen = new HashSet<int>();
    while (c >= 2 && c <= ctx.TotalDataClusters + 1 && !ctx.IsEoc(c) && seen.Add(c)) {
      chain.Add(c);
      c = ReadFatEntry(buf, ctx, c);
    }
    return chain;
  }

  private static void FreeChainInBuffer(byte[] buf, TFatContext ctx, int startCluster) {
    var chain = WalkChainInBuffer(buf, ctx, startCluster);
    foreach (var c in chain)
      WriteFatEntry(buf, ctx, c, 0);
  }

  /// <summary>
  /// Finds <paramref name="count"/> free clusters in the working FAT buffer
  /// and returns them in ascending order. Does not yet mark them allocated —
  /// the caller links them with WriteFatEntry calls.
  /// </summary>
  private static List<int> AllocateClustersInBuffer(byte[] buf, TFatContext ctx, int count) {
    var result = new List<int>(count);
    for (var c = 2; c <= ctx.TotalDataClusters + 1 && result.Count < count; c++) {
      var v = ReadFatEntry(buf, ctx, c);
      if (v == 0) result.Add(c);
    }
    return result;
  }
}

/// <summary>
/// Cached BPB + active-FAT parameters parsed from the boot sector. Kept
/// internal to the TFAT modifier so that the public API surface remains the
/// two static methods <see cref="TFatModifier.AddFile"/> and
/// <see cref="TFatModifier.RemoveFile"/>.
/// </summary>
internal sealed class TFatContext {
  public int BytesPerSector { get; private set; }
  public int SectorsPerCluster { get; private set; }
  public int ReservedSectors { get; private set; }
  public int FatCount { get; private set; }
  public int RootEntryCount { get; private set; }
  public int TotalSectors { get; private set; }
  public int FatSize { get; private set; }
  public int RootDirSectors { get; private set; }
  public int FirstDataSector { get; private set; }
  public int TotalDataClusters { get; private set; }
  public int FatType { get; private set; }
  public int ClusterSize { get; private set; }
  public long FirstDataByte { get; private set; }

  public long Fat1Offset { get; private set; }
  public long Fat2Offset { get; private set; }
  public int FatRegionLen { get; private set; }
  public uint Fat1Sequence { get; private set; }
  public uint Fat2Sequence { get; private set; }

  /// <summary>0 if FAT1 is currently active, 1 if FAT2 is currently active.</summary>
  public int ActiveFatIndex { get; private set; }
  public long ActiveFatOffset { get; private set; }
  public long InactiveFatOffset { get; private set; }
  public uint ActiveSequence { get; private set; }
  public uint InactiveSequence { get; private set; }

  public long RootDirOffset { get; private set; }
  public int RootDirCapacityBytes { get; private set; }
  public int RootCluster { get; private set; }

  public static TFatContext Open(Stream image) {
    var ctx = new TFatContext();
    Span<byte> bpb = stackalloc byte[512];
    image.Position = 0;
    image.ReadExactly(bpb);

    ctx.BytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bpb[11..]);
    if (ctx.BytesPerSector is 0 or > 4096) ctx.BytesPerSector = 512;
    ctx.SectorsPerCluster = bpb[13] == 0 ? 1 : bpb[13];
    ctx.ReservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(bpb[14..]);
    ctx.FatCount = bpb[16] == 0 ? 2 : bpb[16];
    ctx.RootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(bpb[17..]);
    var ts16 = (int)BinaryPrimitives.ReadUInt16LittleEndian(bpb[19..]);
    ctx.TotalSectors = ts16 == 0 ? BinaryPrimitives.ReadInt32LittleEndian(bpb[32..]) : ts16;
    var fs16 = (int)BinaryPrimitives.ReadUInt16LittleEndian(bpb[22..]);
    ctx.FatSize = fs16 == 0 ? BinaryPrimitives.ReadInt32LittleEndian(bpb[36..]) : fs16;
    ctx.RootDirSectors = (ctx.RootEntryCount * 32 + ctx.BytesPerSector - 1) / ctx.BytesPerSector;
    ctx.FirstDataSector = ctx.ReservedSectors + ctx.FatCount * ctx.FatSize + ctx.RootDirSectors;
    ctx.TotalDataClusters = (ctx.TotalSectors - ctx.FirstDataSector) / ctx.SectorsPerCluster;
    ctx.FatType = ctx.TotalDataClusters < 4085 ? 12 : ctx.TotalDataClusters < 65525 ? 16 : 32;
    ctx.ClusterSize = ctx.SectorsPerCluster * ctx.BytesPerSector;
    ctx.FirstDataByte = (long)ctx.FirstDataSector * ctx.BytesPerSector;

    if (ctx.FatCount != 2)
      throw new InvalidDataException($"TFAT: requires exactly 2 FATs, found {ctx.FatCount}.");

    if (ctx.FatType == 32) {
      ctx.RootCluster = BinaryPrimitives.ReadInt32LittleEndian(bpb[44..]);
      throw new NotSupportedException(
        "TFAT in-place modification: FAT32 root cluster updates are not yet supported. " +
        "CE TFAT usage typically pins the root cluster; FAT12/16 is the supported path.");
    }

    ctx.Fat1Offset = (long)ctx.ReservedSectors * ctx.BytesPerSector;
    ctx.Fat2Offset = ctx.Fat1Offset + (long)ctx.FatSize * ctx.BytesPerSector;
    ctx.FatRegionLen = ctx.FatSize * ctx.BytesPerSector;

    Span<byte> seqBuf = stackalloc byte[4];
    image.Position = ctx.Fat1Offset + ctx.FatRegionLen - 4;
    image.ReadExactly(seqBuf);
    ctx.Fat1Sequence = BinaryPrimitives.ReadUInt32BigEndian(seqBuf);
    image.Position = ctx.Fat2Offset + ctx.FatRegionLen - 4;
    image.ReadExactly(seqBuf);
    ctx.Fat2Sequence = BinaryPrimitives.ReadUInt32BigEndian(seqBuf);

    // Active = higher sequence; tie → FAT2 (Microsoft CE default).
    if (ctx.Fat2Sequence >= ctx.Fat1Sequence) {
      ctx.ActiveFatIndex = 1;
      ctx.ActiveFatOffset = ctx.Fat2Offset;
      ctx.InactiveFatOffset = ctx.Fat1Offset;
      ctx.ActiveSequence = ctx.Fat2Sequence;
      ctx.InactiveSequence = ctx.Fat1Sequence;
    } else {
      ctx.ActiveFatIndex = 0;
      ctx.ActiveFatOffset = ctx.Fat1Offset;
      ctx.InactiveFatOffset = ctx.Fat2Offset;
      ctx.ActiveSequence = ctx.Fat1Sequence;
      ctx.InactiveSequence = ctx.Fat2Sequence;
    }

    ctx.RootDirOffset = (long)(ctx.ReservedSectors + ctx.FatCount * ctx.FatSize) * ctx.BytesPerSector;
    ctx.RootDirCapacityBytes = ctx.RootEntryCount * 32;

    return ctx;
  }

  /// <summary>Read the currently-active FAT region (full region including the trailing seq).</summary>
  public byte[] ReadActiveFat(Stream image) {
    var buf = new byte[this.FatRegionLen];
    image.Position = this.ActiveFatOffset;
    image.ReadExactly(buf);
    return buf;
  }

  public long ClusterToOffset(int cluster) =>
    this.FirstDataByte + (long)(cluster - 2) * this.ClusterSize;

  public bool IsEoc(int cluster) => this.FatType switch {
    12 => cluster >= 0xFF8,
    16 => cluster >= 0xFFF8,
    _ => cluster >= 0x0FFFFFF8,
  };

  public int EocMarker() => this.FatType switch {
    12 => 0xFFF,
    16 => 0xFFFF,
    _ => 0x0FFFFFFF,
  };

  /// <summary>
  /// Locates a directory entry by short-name match in the fixed FAT12/16 root.
  /// Returns the index of the 8.3 dirent, and (out) the index of the first LFN
  /// slot if any precede it.
  /// </summary>
  public int FindRootDirEntry(Stream image, string fileName, out int firstLfnIndex) {
    firstLfnIndex = -1;
    var maxEntries = this.RootDirCapacityBytes / 32;
    var slotBuf = new byte[32];
    var nameKey = ShortenForCompare(fileName);
    var firstLfn = -1;

    for (var i = 0; i < maxEntries; i++) {
      image.Position = this.RootDirOffset + i * 32L;
      image.ReadExactly(slotBuf, 0, 32);
      var first = slotBuf[0];
      if (first == 0x00) break;
      if (first == 0xE5) { firstLfn = -1; continue; }

      var attr = slotBuf[11];
      if ((attr & 0x3F) == 0x0F) {
        if (firstLfn < 0) firstLfn = i;
        continue;
      }
      if ((attr & 0x08) != 0) { firstLfn = -1; continue; }

      var sn = DecodeShortName(slotBuf.AsSpan(0, 11));
      if (sn.Equals(nameKey, StringComparison.OrdinalIgnoreCase)) {
        firstLfnIndex = firstLfn;
        return i;
      }
      firstLfn = -1;
    }
    return -1;
  }

  /// <summary>
  /// Allocates a fresh root-directory slot for an Add. If a previous entry with
  /// the same name existed (<paramref name="existingFirstCluster"/> &gt;= 0), the
  /// slot is overwritten in place by returning the same index — the caller
  /// writes the new short-name entry on top.
  /// </summary>
  public (int Index, int OldLfnIndex) AllocateRootDirEntry(
      Stream image, string fileName, int existingFirstCluster, int oldFirstLfnIndex, int oldDirIndex) {
    // If overwriting an existing entry, reuse its slot — both the LFN slots
    // (if any) and the short-name slot. Wipe the old LFN slots to 0xE5.
    if (existingFirstCluster >= 0 && oldDirIndex >= 0) {
      if (oldFirstLfnIndex >= 0) {
        Span<byte> deleted = stackalloc byte[32];
        deleted.Clear();
        deleted[0] = 0xE5;
        for (var i = oldFirstLfnIndex; i < oldDirIndex; i++) {
          image.Position = this.RootDirOffset + i * 32L;
          image.Write(deleted);
        }
      }
      return (oldDirIndex, oldFirstLfnIndex);
    }

    // Otherwise scan for the first 0x00 (free at end) or 0xE5 (deleted) slot.
    var maxEntries = this.RootDirCapacityBytes / 32;
    var slotBuf = new byte[32];
    for (var i = 0; i < maxEntries; i++) {
      image.Position = this.RootDirOffset + i * 32L;
      image.ReadExactly(slotBuf, 0, 32);
      var first = slotBuf[0];
      if (first is 0x00 or 0xE5)
        return (i, -1);
    }
    throw new IOException("TFAT: root directory is full.");
  }

  /// <summary>
  /// Writes a fresh 8.3 directory entry at the given slot. Long-name input is
  /// truncated/uppercased to fit (LFN precursor slots not emitted by the
  /// modifier path — Add() uses a single 32-byte entry).
  /// </summary>
  public void WriteRootDirEntry(Stream image, int slotIndex, string fileName, int firstCluster, long size) {
    Span<byte> entry = stackalloc byte[32];
    entry.Clear();

    var sn = MakeShortName(fileName);
    Encoding.ASCII.GetBytes(sn.AsSpan(0, 8), entry[..8]);
    Encoding.ASCII.GetBytes(sn.AsSpan(8, 3), entry[8..11]);
    entry[11] = 0x20; // archive attribute

    BinaryPrimitives.WriteUInt16LittleEndian(entry[26..], (ushort)(firstCluster & 0xFFFF));
    if (this.FatType == 32)
      BinaryPrimitives.WriteUInt16LittleEndian(entry[20..], (ushort)((firstCluster >> 16) & 0xFFFF));
    BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], (uint)size);

    image.Position = this.RootDirOffset + slotIndex * 32L;
    image.Write(entry);
  }

  /// <summary>
  /// Turns an arbitrary filename into an 11-byte (8 + 3) uppercase 8.3 form
  /// for short-name matching and storage. Disallowed chars become '_' and the
  /// base/ext are right-padded with spaces.
  /// </summary>
  private static string MakeShortName(string fileName) {
    var leaf = Path.GetFileName(fileName);
    var dot = leaf.LastIndexOf('.');
    var basePart = dot >= 0 ? leaf[..dot] : leaf;
    var extPart = dot >= 0 ? leaf[(dot + 1)..] : "";

    var b = new StringBuilder();
    foreach (var c in basePart) {
      if (b.Length >= 8) break;
      b.Append(Is83(char.ToUpperInvariant(c)) ? char.ToUpperInvariant(c) : '_');
    }
    while (b.Length < 8) b.Append(' ');

    var e = new StringBuilder();
    foreach (var c in extPart) {
      if (e.Length >= 3) break;
      e.Append(Is83(char.ToUpperInvariant(c)) ? char.ToUpperInvariant(c) : '_');
    }
    while (e.Length < 3) e.Append(' ');

    return b.ToString() + e;
  }

  private static bool Is83(char c) =>
    c is >= 'A' and <= 'Z' or >= '0' and <= '9'
    or '_' or '-' or '$' or '%' or '\'' or '@' or '~' or '`' or '!'
    or '(' or ')' or '{' or '}' or '^' or '#' or '&';

  private static string ShortenForCompare(string fileName) {
    var sn = MakeShortName(fileName);
    return DecodeShortName(Encoding.ASCII.GetBytes(sn));
  }

  private static string DecodeShortName(ReadOnlySpan<byte> raw) {
    var name = Encoding.ASCII.GetString(raw[..8]).TrimEnd(' ');
    var ext = Encoding.ASCII.GetString(raw.Slice(8, 3)).TrimEnd(' ');
    return ext.Length == 0 ? name : $"{name}.{ext}";
  }
}
