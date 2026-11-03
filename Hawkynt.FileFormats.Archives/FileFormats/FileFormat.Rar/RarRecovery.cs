using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FileFormat.Rar;

/// <summary>Which kind of RAR recovery data a check or repair found.</summary>
public enum RarRecoveryKind {
  /// <summary>No recovery data was found.</summary>
  None,

  /// <summary>A RAR 2.x-4.x recovery record inside the archive (512-byte sectors, interleaved XOR parity).</summary>
  Rar4Record,

  /// <summary>A RAR 5 recovery record inside the archive (Reed-Solomon over GF(2^16)).</summary>
  Rar5Record,

  /// <summary>RAR 3.x-4.x recovery volumes (<c>.rev</c>, Reed-Solomon over GF(2^8)).</summary>
  Rar3Volumes,

  /// <summary>RAR 5 recovery volumes (<c>.rev</c>, Reed-Solomon over GF(2^16)).</summary>
  Rar5Volumes,
}

/// <summary>Outcome of checking or repairing RAR recovery data.</summary>
/// <param name="Kind">The recovery data found.</param>
/// <param name="Intact">Everything the recovery data protects checks out, the recovery data too.</param>
/// <param name="Damaged">Protected units (RAR 5 shards, RAR 4 sectors, volumes) that fail their check.</param>
/// <param name="Recovery">Usable recovery units (RAR 5 blocks, RAR 4 parity sectors, recovery volumes).</param>
/// <param name="Repairable">The damage is within what the recovery data can rebuild.</param>
/// <param name="Repaired">A repair was asked for and every damaged unit was rebuilt and re-verified.</param>
/// <param name="Message">What was found or done, in words.</param>
public sealed record RarRecoveryReport(RarRecoveryKind Kind, bool Intact, int Damaged, int Recovery, bool Repairable, bool Repaired, string Message);

/// <summary>
/// Checks and repairs RAR archives from their recovery records, the counterpart of <c>rar t</c>'s
/// recovery-record test and <c>rar r</c>. The layouts were measured from rar's output
/// (docs/RAR-RECOVERY-ON-DISK.md); rar repairs what <see cref="RarWriter"/> and
/// <see cref="Rar4Writer"/> protect, and this repairs what rar protects.
/// </summary>
/// <remarks>
/// Damage is repaired in place: bytes that changed, not bytes inserted or removed. A recovery
/// record protects the archive up to its own header; damage after that (the record's header, the
/// end-of-archive block) is outside what it can rebuild.
/// </remarks>
public static class RarRecovery {
  /// <summary>Checks the archive at <paramref name="path"/> against its recovery record.</summary>
  public static RarRecoveryReport Verify(string path) {
    using var fs = File.OpenRead(path);
    return Verify(fs);
  }

  /// <summary>Checks a seekable archive stream against its recovery record.</summary>
  public static RarRecoveryReport Verify(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanSeek || !archive.CanRead) throw new ArgumentException("The archive stream must be readable and seekable.", nameof(archive));
    return Rar5RecoveryRecord.Find(archive) is { } r5 ? Rar5RecoveryRecord.Process(archive, r5, repair: false)
      : Rar4RecoveryRecord.Find(archive) is { } r4 ? Rar4RecoveryRecord.Process(archive, r4, repair: false)
      : new RarRecoveryReport(RarRecoveryKind.None, false, 0, 0, false, false, "No recovery record found.");
  }

  /// <summary>Repairs <paramref name="sourcePath"/> into <paramref name="destinationPath"/> from its recovery record.</summary>
  public static RarRecoveryReport Repair(string sourcePath, string destinationPath) {
    using var src = File.OpenRead(sourcePath);
    using var dst = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
    return Repair(src, dst);
  }

  /// <summary>
  /// Copies <paramref name="source"/> to <paramref name="destination"/> and repairs the copy from its
  /// recovery record. <paramref name="destination"/> must be readable, writable and seekable.
  /// </summary>
  public static RarRecoveryReport Repair(Stream source, Stream destination) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(destination);
    if (!destination.CanSeek || !destination.CanRead || !destination.CanWrite)
      throw new ArgumentException("The destination must be readable, writable and seekable.", nameof(destination));
    destination.SetLength(0);
    source.CopyTo(destination);
    destination.Flush();
    return RepairInPlace(destination);
  }

  /// <summary>Repairs a readable, writable, seekable archive stream in place.</summary>
  public static RarRecoveryReport RepairInPlace(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    return Rar5RecoveryRecord.Find(archive) is { } r5 ? Rar5RecoveryRecord.Process(archive, r5, repair: true)
      : Rar4RecoveryRecord.Find(archive) is { } r4 ? Rar4RecoveryRecord.Process(archive, r4, repair: true)
      : new RarRecoveryReport(RarRecoveryKind.None, false, 0, 0, false, false, "No recovery record found.");
  }

  internal static void ReadAt(Stream s, long position, Span<byte> buffer) {
    s.Position = position;
    var got = 0;
    while (got < buffer.Length) {
      var n = s.Read(buffer[got..]);
      if (n <= 0) {
        buffer[got..].Clear();
        return;
      }
      got += n;
    }
  }

  /// <summary>Positions of <paramref name="pattern"/> in the stream, scanned in chunks.</summary>
  internal static IEnumerable<long> Scan(Stream s, byte[] pattern) {
    var buf = new byte[1 << 20];
    var keep = pattern.Length - 1;
    long bufStart = 0;
    var len = 0;
    s.Position = 0;
    while (true) {
      var n = s.Read(buf, len, buf.Length - len);
      if (n <= 0 && len < pattern.Length) yield break;
      len += Math.Max(n, 0);
      var span = buf.AsSpan(0, len);
      var from = 0;
      var hits = new List<long>();
      while (true) {
        var i = span[from..].IndexOf(pattern);
        if (i < 0) break;
        hits.Add(bufStart + from + i);
        from += i + 1;
      }
      foreach (var h in hits) yield return h;
      if (n <= 0) yield break;
      var cut = Math.Max(0, len - keep);
      Buffer.BlockCopy(buf, cut, buf, 0, len - cut);
      bufStart += cut;
      len -= cut;
      s.Position = bufStart + len;
    }
  }
}

/// <summary>
/// The RAR 5 recovery record: blocks of a 64-byte header, a CRC-64 table of the data shards plus a
/// random identifier, then one Reed-Solomon recovery shard (GF(2^16), Cauchy coefficients
/// 1 / ((dataShards + row) xor column)) over the archive bytes before the record's header.
/// </summary>
internal static class Rar5RecoveryRecord {
  internal static readonly byte[] Magic = "{RB}"u8.ToArray();
  private const int HeaderBytes = 64;
  private const int MaxDataShards = 200;
  private const int Chunk = 1 << 16;

  internal sealed record Layout(long Protected, long ShardSize, int DataShards, int RecoveryShards, long BlockSize, int HeaderSize,
    ulong[] ShardCrcs, ulong Id, byte[] HeaderTemplate, long FirstBlock, bool[] ValidBlocks);

  /// <summary>Shard size and counts rar 7.12 uses for an archive of <paramref name="protectedSize"/> bytes.</summary>
  internal static (int DataShards, int RecoveryShards, long ShardSize) Plan(long protectedSize, int percent) {
    var nd = (int)Math.Clamp((protectedSize + 1023) / 1024, 1, MaxDataShards);
    var nr = Math.Max(1, nd * percent / 100);
    var s = (protectedSize + nd - 1) / nd;
    s += s & 1;
    return (nd, nr, s);
  }

  internal static long BlockSize(int dataShards, long shardSize) => HeaderBytes + 8L * (dataShards + 1) + shardSize;

  /// <summary>
  /// Computes the record over bytes [0, <paramref name="protectedSize"/>) of <paramref name="file"/>
  /// and writes its blocks at <paramref name="dataPosition"/>.
  /// </summary>
  internal static void Write(Stream file, long protectedSize, int percent, long dataPosition) {
    var (nd, nr, s) = Plan(protectedSize, percent);
    var hdr = HeaderBytes + 8 * (nd + 1);
    var block = BlockSize(nd, s);
    var crcs = new ulong[nd];
    var data = new byte[Chunk];
    var rec = new byte[nr][];
    for (var i = 0; i < nr; ++i) rec[i] = new byte[Chunk];
    var logs = new int[nr, nd];
    for (var i = 0; i < nr; ++i)
      for (var j = 0; j < nd; ++j) logs[i, j] = RarGf16.LogOf(RarGf16.Cauchy(nd + i, j));

    for (long c = 0; c < s; c += Chunk) {
      var len = (int)Math.Min(Chunk, s - c);
      foreach (var r in rec) r.AsSpan(0, len).Clear();
      for (var j = 0; j < nd; ++j) {
        var at = j * s + c;
        var real = (int)Math.Clamp(protectedSize - at, 0, len);
        RarRecovery.ReadAt(file, at, data.AsSpan(0, real));
        data.AsSpan(real, len - real).Clear();
        crcs[j] = RarCrc64.Update(crcs[j], data.AsSpan(0, real));
        for (var i = 0; i < nr; ++i) RarGf16.MulAdd(rec[i].AsSpan(0, len), data.AsSpan(0, len), logs[i, j]);
      }
      for (var i = 0; i < nr; ++i) {
        file.Position = dataPosition + i * block + hdr + c;
        file.Write(rec[i], 0, len);
      }
    }

    var id = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
    var header = new byte[hdr];
    Magic.CopyTo(header, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)block);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)hdr);
    header[20] = 1;
    header[21] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(30), (uint)(protectedSize - (nd - 1) * s));
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(34), (ulong)protectedSize);
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(42), (ulong)s);
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(50), (ulong)block);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(58), (ushort)nd);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(60), (ushort)nr);
    for (var j = 0; j < nd; ++j) BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(HeaderBytes + 8 * j), crcs[j]);
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(HeaderBytes + 8 * nd), id);
    for (var i = 0; i < nr; ++i) WriteBlockHeader(file, dataPosition + i * block, header, i, block);
  }

  /// <summary>Writes a block's header with its index and the CRC-64 over the block from byte 12.</summary>
  private static void WriteBlockHeader(Stream file, long position, byte[] template, int index, long block) {
    var header = (byte[])template.Clone();
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(62), (ushort)index);
    var crc = RarCrc64.Update(ulong.MaxValue, header.AsSpan(12));
    var buf = new byte[Chunk];
    for (long p = header.Length; p < block; p += Chunk) {
      var len = (int)Math.Min(Chunk, block - p);
      RarRecovery.ReadAt(file, position + p, buf.AsSpan(0, len));
      crc = RarCrc64.Update(crc, buf.AsSpan(0, len));
    }
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(4), ~crc);
    file.Position = position;
    file.Write(header);
  }

  /// <summary>Finds the record's blocks by their signature and CRC, wherever they are.</summary>
  internal static Layout? Find(Stream s) {
    var found = new List<(long Pos, byte[] Header)>();
    var head = new byte[HeaderBytes];
    foreach (var pos in RarRecovery.Scan(s, Magic).ToList()) {
      RarRecovery.ReadAt(s, pos, head);
      var block = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
      var hdr = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(16));
      var prot = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(34));
      var shard = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(42));
      var block64 = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(50));
      var nd = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(58));
      var nr = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(60));
      var idx = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(62));
      if (nd == 0 || nr == 0 || idx >= nr || hdr != HeaderBytes + 8u * (nd + 1u) || block64 != block
          || shard <= 0 || (shard & 1) != 0 || block != hdr + shard || prot <= 0 || prot > pos || pos + block > s.Length)
        continue;
      // The record's header sits between the protected bytes and its first block; a record found
      // further away belongs to an archive stored inside this one.
      var firstAt = pos - idx * (long)block;
      if (firstAt < prot || firstAt - prot > 1024) continue;
      var full = new byte[hdr];
      RarRecovery.ReadAt(s, pos, full);
      var crc = RarCrc64.Update(ulong.MaxValue, full.AsSpan(12));
      var buf = new byte[Chunk];
      for (long p = hdr; p < block; p += Chunk) {
        var len = (int)Math.Min(Chunk, block - p);
        RarRecovery.ReadAt(s, pos + p, buf.AsSpan(0, len));
        crc = RarCrc64.Update(crc, buf.AsSpan(0, len));
      }
      if (~crc != BinaryPrimitives.ReadUInt64LittleEndian(full.AsSpan(4))) continue;
      found.Add((pos, full));
    }
    if (found.Count == 0) return null;

    // The blocks of one record share everything but their index; take the largest such group.
    var group = found.GroupBy(f => Convert.ToHexString(f.Header.AsSpan(12, 50)) + Convert.ToHexString(f.Header.AsSpan(HeaderBytes)))
      .OrderByDescending(g => g.Count()).First().ToList();
    var h0 = group[0].Header;
    var nd0 = BinaryPrimitives.ReadUInt16LittleEndian(h0.AsSpan(58));
    var nr0 = BinaryPrimitives.ReadUInt16LittleEndian(h0.AsSpan(60));
    var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(h0.AsSpan(12));
    var first = group[0].Pos - BinaryPrimitives.ReadUInt16LittleEndian(h0.AsSpan(62)) * (long)blockSize;
    var valid = new bool[nr0];
    foreach (var (pos, header) in group) {
      var idx = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(62));
      if (pos == first + idx * (long)blockSize) valid[idx] = true;
    }
    var crcs = new ulong[nd0];
    for (var j = 0; j < nd0; ++j) crcs[j] = BinaryPrimitives.ReadUInt64LittleEndian(h0.AsSpan(HeaderBytes + 8 * j));
    return new Layout(BinaryPrimitives.ReadInt64LittleEndian(h0.AsSpan(34)), BinaryPrimitives.ReadInt64LittleEndian(h0.AsSpan(42)),
      nd0, nr0, blockSize, h0.Length, crcs, BinaryPrimitives.ReadUInt64LittleEndian(h0.AsSpan(HeaderBytes + 8 * nd0)), h0, first, valid);
  }

  private static ulong ShardCrc(Stream s, Layout l, int j) {
    var start = j * l.ShardSize;
    var end = Math.Min(start + l.ShardSize, l.Protected);
    var buf = new byte[Chunk];
    ulong crc = 0;
    for (var p = start; p < end; p += Chunk) {
      var len = (int)Math.Min(Chunk, end - p);
      RarRecovery.ReadAt(s, p, buf.AsSpan(0, len));
      crc = RarCrc64.Update(crc, buf.AsSpan(0, len));
    }
    return crc;
  }

  internal static RarRecoveryReport Process(Stream s, Layout l, bool repair) {
    var damaged = new List<int>();
    for (var j = 0; j < l.DataShards; ++j)
      if (ShardCrc(s, l, j) != l.ShardCrcs[j]) damaged.Add(j);
    var validRows = Enumerable.Range(0, l.RecoveryShards).Where(i => l.ValidBlocks[i]).ToList();
    var badBlocks = l.RecoveryShards - validRows.Count;
    var intact = damaged.Count == 0 && badBlocks == 0;
    var repairable = damaged.Count <= validRows.Count;
    var what = $"RAR 5 recovery record: {l.DataShards} data shards of {l.ShardSize} bytes, {l.RecoveryShards} recovery blocks; "
      + $"{damaged.Count} damaged shard(s), {badBlocks} damaged recovery block(s).";
    if (!repair || intact)
      return new RarRecoveryReport(RarRecoveryKind.Rar5Record, intact, damaged.Count, validRows.Count, repairable, false,
        intact ? "RAR 5 recovery record: archive and record intact." : what);
    if (!repairable)
      return new RarRecoveryReport(RarRecoveryKind.Rar5Record, false, damaged.Count, validRows.Count, false, false,
        what + " More damage than the record can rebuild.");

    if (damaged.Count > 0) RebuildShards(s, l, damaged, validRows.Take(damaged.Count).ToArray());
    var still = damaged.Where(j => ShardCrc(s, l, j) != l.ShardCrcs[j]).ToList();
    if (still.Count > 0)
      return new RarRecoveryReport(RarRecoveryKind.Rar5Record, false, still.Count, validRows.Count, true, false,
        what + " Rebuilt shards still fail their CRC-64; the archive was not repaired.");
    // Damaged recovery blocks are recomputed from the repaired data, when the file still holds them.
    for (var i = 0; i < l.RecoveryShards; ++i)
      if (!l.ValidBlocks[i] && l.FirstBlock + (i + 1) * l.BlockSize <= s.Length)
        RebuildBlock(s, l, i);
    s.Flush();
    return new RarRecoveryReport(RarRecoveryKind.Rar5Record, false, damaged.Count, validRows.Count, true, true,
      what + " Repaired.");
  }

  /// <summary>Solves the Cauchy system for the erased shards, chunk by chunk, and writes them back.</summary>
  private static void RebuildShards(Stream s, Layout l, List<int> erased, int[] rows) {
    var n = erased.Count;
    var m = new ushort[n, n];
    for (var e = 0; e < n; ++e)
      for (var k = 0; k < n; ++k) m[e, k] = RarGf16.Cauchy(l.DataShards + rows[e], erased[k]);
    if (!RarGf16.Invert(m)) throw new InvalidDataException("RAR 5 recovery: singular recovery matrix.");
    var rhs = new byte[n][];
    for (var e = 0; e < n; ++e) rhs[e] = new byte[Chunk];
    var data = new byte[Chunk];
    var output = new byte[Chunk];
    var isErased = new bool[l.DataShards];
    foreach (var j in erased) isErased[j] = true;
    for (long c = 0; c < l.ShardSize; c += Chunk) {
      var len = (int)Math.Min(Chunk, l.ShardSize - c);
      for (var e = 0; e < n; ++e)
        RarRecovery.ReadAt(s, l.FirstBlock + rows[e] * l.BlockSize + l.HeaderSize + c, rhs[e].AsSpan(0, len));
      for (var j = 0; j < l.DataShards; ++j) {
        if (isErased[j]) continue;
        var at = j * l.ShardSize + c;
        var real = (int)Math.Clamp(l.Protected - at, 0, len);
        if (real == 0) continue;
        RarRecovery.ReadAt(s, at, data.AsSpan(0, real));
        data.AsSpan(real, len - real).Clear();
        for (var e = 0; e < n; ++e)
          RarGf16.MulAdd(rhs[e].AsSpan(0, len), data.AsSpan(0, len), RarGf16.LogOf(RarGf16.Cauchy(l.DataShards + rows[e], j)));
      }
      for (var k = 0; k < n; ++k) {
        output.AsSpan(0, len).Clear();
        for (var e = 0; e < n; ++e)
          if (m[k, e] != 0) RarGf16.MulAdd(output.AsSpan(0, len), rhs[e].AsSpan(0, len), RarGf16.LogOf(m[k, e]));
        var at = erased[k] * l.ShardSize + c;
        var real = (int)Math.Clamp(l.Protected - at, 0, len);
        if (real == 0) continue;
        s.Position = at;
        s.Write(output, 0, real);
      }
    }
  }

  private static void RebuildBlock(Stream s, Layout l, int index) {
    var rec = new byte[Chunk];
    var data = new byte[Chunk];
    var pos = l.FirstBlock + index * l.BlockSize;
    for (long c = 0; c < l.ShardSize; c += Chunk) {
      var len = (int)Math.Min(Chunk, l.ShardSize - c);
      rec.AsSpan(0, len).Clear();
      for (var j = 0; j < l.DataShards; ++j) {
        var at = j * l.ShardSize + c;
        var real = (int)Math.Clamp(l.Protected - at, 0, len);
        if (real == 0) continue;
        RarRecovery.ReadAt(s, at, data.AsSpan(0, real));
        data.AsSpan(real, len - real).Clear();
        RarGf16.MulAdd(rec.AsSpan(0, len), data.AsSpan(0, len), RarGf16.LogOf(RarGf16.Cauchy(l.DataShards + index, j)));
      }
      s.Position = pos + l.HeaderSize + c;
      s.Write(rec, 0, len);
    }
    var header = (byte[])l.HeaderTemplate.Clone();
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(62), (ushort)index);
    var crc = RarCrc64.Update(ulong.MaxValue, header.AsSpan(12));
    var buf = new byte[Chunk];
    for (long p = header.Length; p < l.BlockSize; p += Chunk) {
      var len = (int)Math.Min(Chunk, l.BlockSize - p);
      RarRecovery.ReadAt(s, pos + p, buf.AsSpan(0, len));
      crc = RarCrc64.Update(crc, buf.AsSpan(0, len));
    }
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(4), ~crc);
    s.Position = pos;
    s.Write(header);
  }
}

/// <summary>
/// The RAR 2.x-4.x recovery record: a sub-block named <c>RR</c> whose data is one 16-bit check per
/// 512-byte sector of the archive before it (the low half of the CRC-32 register, not inverted),
/// then R parity sectors, parity k the XOR of every sector i with i mod R = k.
/// </summary>
internal static class Rar4RecoveryRecord {
  internal static readonly byte[] Marker = "Protect+"u8.ToArray();
  internal const int Sector = 512;
  internal const int HeaderSize = 54;

  internal sealed record Layout(long Protected, long HeaderPos, int RecoverySectors, long DataSectors, long DataPos, long DataSize, uint DataCrc);

  /// <summary>Parity sectors for <paramref name="percent"/> of <paramref name="sectors"/> data sectors.</summary>
  internal static int Plan(long sectors, int percent) => (int)Math.Clamp((sectors * percent + 99) / 100, 1, 0xFFFFFF);

  /// <summary>The CRC-32 rar stores for the record's data: the register starts at 0x0FFFFFFF.</summary>
  internal static uint DataCrc(uint register) => ~register;

  /// <summary>
  /// Builds the record's header and data over bytes [0, <paramref name="protectedSize"/>) of
  /// <paramref name="file"/>.
  /// </summary>
  internal static byte[] Build(Stream file, long protectedSize, int percent, uint dosTime) {
    var n = (protectedSize + Sector - 1) / Sector;
    var r = Plan(n, percent);
    var data = new byte[2 * n + (long)Sector * r];
    var sector = new byte[Sector];
    for (long i = 0; i < n; ++i) {
      var real = (int)Math.Min(Sector, protectedSize - i * Sector);
      RarRecovery.ReadAt(file, i * Sector, sector.AsSpan(0, real));
      sector.AsSpan(real).Clear();
      BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan((int)(2 * i)), (ushort)RarCrc32Raw.Update(uint.MaxValue, sector));
      var parity = data.AsSpan((int)(2 * n + (i % r) * Sector), Sector);
      for (var k = 0; k < Sector; ++k) parity[k] ^= sector[k];
    }
    var h = new byte[HeaderSize + data.Length];
    h[2] = RarConstants.Rar4TypeNewSub;
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(3), 0xC000);
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(5), HeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(7), (uint)data.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(11), (uint)data.Length);
    h[15] = 2;
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), DataCrc(RarCrc32Raw.Update(0x0FFFFFFF, data)));
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(20), dosTime);
    h[24] = 29;
    h[25] = RarConstants.Rar4MethodStore;
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(26), 2);
    "RR"u8.CopyTo(h.AsSpan(32));
    Marker.CopyTo(h, 34);
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(42), (uint)r);
    BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(46), (ulong)n);
    BinaryPrimitives.WriteUInt16LittleEndian(h, (ushort)Compression.Core.Checksums.Crc32.Compute(h.AsSpan(2, HeaderSize - 2)));
    data.CopyTo(h, HeaderSize);
    return h;
  }

  internal static Layout? Find(Stream s) {
    var head = new byte[HeaderSize];
    foreach (var m in RarRecovery.Scan(s, Marker).ToList()) {
      var pos = m - 34;
      if (pos < 7) continue;
      RarRecovery.ReadAt(s, pos, head);
      if (head[2] != RarConstants.Rar4TypeNewSub || BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(5)) != HeaderSize
          || BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(26)) != 2 || head[32] != (byte)'R' || head[33] != (byte)'R'
          || BinaryPrimitives.ReadUInt16LittleEndian(head) != (ushort)Compression.Core.Checksums.Crc32.Compute(head.AsSpan(2, HeaderSize - 2)))
        continue;
      var r = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(42));
      var n = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(46));
      var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(7));
      if (r <= 0 || n != (pos + Sector - 1) / Sector || size != 2 * n + (long)Sector * r || pos + HeaderSize + size > s.Length) continue;
      return new Layout(pos, pos, r, n, pos + HeaderSize, size, BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(16)));
    }
    return null;
  }

  internal static RarRecoveryReport Process(Stream s, Layout l, bool repair) {
    var data = new byte[l.DataSize];
    RarRecovery.ReadAt(s, l.DataPos, data);
    if (DataCrc(RarCrc32Raw.Update(0x0FFFFFFF, data)) != l.DataCrc)
      return new RarRecoveryReport(RarRecoveryKind.Rar4Record, false, 0, 0, false, false,
        "RAR 4 recovery record: the record itself is damaged (its CRC-32 fails), so it cannot be trusted for a repair.");
    var sector = new byte[Sector];
    var damaged = new List<long>();
    for (long i = 0; i < l.DataSectors; ++i) {
      ReadSector(s, l, i, sector);
      if ((ushort)RarCrc32Raw.Update(uint.MaxValue, sector) != BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)(2 * i))))
        damaged.Add(i);
    }
    var groups = damaged.GroupBy(i => i % l.RecoverySectors).ToList();
    var repairable = groups.All(g => g.Count() == 1);
    var what = $"RAR 4 recovery record: {l.DataSectors} sectors, {l.RecoverySectors} parity sectors; {damaged.Count} damaged sector(s).";
    if (!repair || damaged.Count == 0)
      return new RarRecoveryReport(RarRecoveryKind.Rar4Record, damaged.Count == 0, damaged.Count, l.RecoverySectors, repairable, false,
        damaged.Count == 0 ? "RAR 4 recovery record: archive and record intact." : what);
    if (!repairable)
      return new RarRecoveryReport(RarRecoveryKind.Rar4Record, false, damaged.Count, l.RecoverySectors, false, false,
        what + " Two damaged sectors share a parity sector; the record cannot rebuild them.");
    var rebuilt = new byte[Sector];
    foreach (var i in damaged) {
      var k = i % l.RecoverySectors;
      data.AsSpan((int)(2 * l.DataSectors + k * Sector), Sector).CopyTo(rebuilt);
      for (var j = k; j < l.DataSectors; j += l.RecoverySectors) {
        if (j == i) continue;
        ReadSector(s, l, j, sector);
        for (var b = 0; b < Sector; ++b) rebuilt[b] ^= sector[b];
      }
      if ((ushort)RarCrc32Raw.Update(uint.MaxValue, rebuilt) != BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)(2 * i))))
        return new RarRecoveryReport(RarRecoveryKind.Rar4Record, false, damaged.Count, l.RecoverySectors, true, false,
          what + $" Sector {i} did not rebuild to its check; the archive was not repaired.");
      s.Position = i * Sector;
      s.Write(rebuilt, 0, (int)Math.Min(Sector, l.Protected - i * Sector));
    }
    s.Flush();
    return new RarRecoveryReport(RarRecoveryKind.Rar4Record, false, damaged.Count, l.RecoverySectors, true, true, what + " Repaired.");
  }

  private static void ReadSector(Stream s, Layout l, long i, byte[] sector) {
    var real = (int)Math.Min(Sector, l.Protected - i * Sector);
    RarRecovery.ReadAt(s, i * Sector, sector.AsSpan(0, real));
    sector.AsSpan(real).Clear();
  }
}
