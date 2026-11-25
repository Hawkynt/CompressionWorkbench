using System.Buffers.Binary;
using Compression.Core.Checksums;

namespace FileFormat.Rar;

/// <summary>
/// Repair of shifting damage: bytes inserted into or lost from the protected part of an archive.
/// rar's <c>r</c> reports the units it finds again at another offset as "relocated" and rebuilds
/// the rest from the record; this does the same. Every unit (RAR 5 shard, RAR 4 sector) is looked
/// for where it belongs, shifted by the drift of the unit before it, and otherwise searched for
/// within <see cref="SearchRadius"/> bytes. Units found are copied to their original offsets,
/// units not found become erasures, and the record then rebuilds those as for damage in place.
/// </summary>
/// <remarks>
/// Measured with rar 7.12 (RAR 5) and rar 6.24 (RAR 4): 100 bytes inserted, 100 bytes removed, and
/// 3 inserted plus 50 removed further on, inside a 250 000-byte stored file; rar rebuilt the
/// original archive byte for byte each time (docs/RAR-RECOVERY-ON-DISK.md).
/// </remarks>
internal static class RarRecoveryRelocation {
  /// <summary>How far from its expected offset a unit is searched for, beyond the known overall shift.</summary>
  internal const long SearchRadius = 1L << 20;

  /// <summary>
  /// RAR 4 sectors carry only a 16-bit check, so a sector found away from its expected offset
  /// counts only when at least <see cref="Rar4Confirmations"/> of the next
  /// <see cref="Rar4Lookahead"/> whole sectors check out after it too (fewer near the end).
  /// One of those may itself hold the damage.
  /// </summary>
  private const int Rar4Confirmations = 2, Rar4Lookahead = 4;

  private const int Chunk = 1 << 16;

  /// <summary>
  /// Rebuilds the layout of a RAR 5 archive whose protected bytes moved, into
  /// <paramref name="output"/> (readable, writable, seekable). Returns null when no record or no
  /// readable record header is found; otherwise how many shards were found away from their offset
  /// and how many were not found at all.
  /// </summary>
  internal static (int Relocated, int Missing)? Rar5(Stream s, Stream output) {
    var blocks = Rar5RecoveryRecord.FindBlocks(s, placed: false);
    if (blocks.Count == 0) return null;
    var h0 = blocks[0].Header;
    var protectedSize = BinaryPrimitives.ReadInt64LittleEndian(h0.AsSpan(34));
    var shard = BinaryPrimitives.ReadInt64LittleEndian(h0.AsSpan(42));
    var blockSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(h0.AsSpan(12));
    var nd = BinaryPrimitives.ReadUInt16LittleEndian(h0.AsSpan(58));
    var nr = BinaryPrimitives.ReadUInt16LittleEndian(h0.AsSpan(60));
    var byIndex = new long[nr];
    Array.Fill(byIndex, -1);
    foreach (var (pos, header) in blocks) {
      var idx = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(62));
      if (byIndex[idx] < 0) byIndex[idx] = pos;
    }
    var lowest = Array.FindIndex(byIndex, p => p >= 0);
    var firstBlock = byIndex[lowest] - lowest * blockSize;

    // The record's header ends where its first block starts; it is a CRC-checked service header.
    var serviceHeader = Rar5ServiceHeaderBefore(s, firstBlock);
    if (serviceHeader == null) return null;
    var protectedEnd = firstBlock - serviceHeader.Length;
    if (protectedEnd <= 0) return null;

    var crcs = new ulong[nd];
    for (var j = 0; j < nd; ++j) crcs[j] = BinaryPrimitives.ReadUInt64LittleEndian(h0.AsSpan(64 + 8 * j));
    var radius = Math.Abs(protectedEnd - protectedSize) + SearchRadius;
    var found = new long[nd];
    long drift = 0;
    var relocated = 0;
    var missing = 0;
    for (var j = 0; j < nd; ++j) {
      var length = (int)Math.Min(shard, protectedSize - j * shard);
      var expected = j * shard + drift;
      var at = Matches64(s, expected, length, crcs[j], protectedEnd)
        ? expected
        : Search64(s, expected, radius, length, crcs[j], protectedEnd);
      found[j] = at;
      if (at < 0) {
        ++missing;
        continue;
      }
      drift = at - j * shard;
      if (drift != 0) ++relocated;
    }

    output.SetLength(0);
    var buf = new byte[Chunk];
    for (var j = 0; j < nd; ++j) {
      var length = Math.Min(shard, protectedSize - j * shard);
      if (found[j] >= 0) CopyRange(s, found[j], output, j * shard, length, buf);
      else ZeroRange(output, j * shard, length, buf);
    }
    output.Position = protectedSize;
    output.Write(serviceHeader);
    var blocksAt = protectedSize + serviceHeader.Length;
    for (var i = 0; i < nr; ++i) {
      if (byIndex[i] >= 0) CopyRange(s, byIndex[i], output, blocksAt + i * blockSize, blockSize, buf);
      else ZeroRange(output, blocksAt + i * blockSize, blockSize, buf);
    }
    var tail = byIndex[nr - 1] >= 0 ? byIndex[nr - 1] + blockSize : firstBlock + nr * blockSize;
    output.Position = blocksAt + nr * blockSize;
    output.Write(Rar5EndHeader(s, tail));
    output.Flush();
    return (relocated, missing);
  }

  /// <summary>
  /// Rebuilds the layout of a RAR 4 archive whose protected bytes moved. Returns null when no
  /// record is found or the record's own data fails its CRC-32.
  /// </summary>
  internal static (int Relocated, int Missing)? Rar4(Stream s, Stream output) {
    var head = Rar4RecoveryRecord.FindHeader(s);
    if (head is not { } h) return null;
    var data = new byte[h.DataSize];
    RarRecovery.ReadAt(s, h.HeaderPos + Rar4RecoveryRecord.HeaderSize, data);
    if (Rar4RecoveryRecord.DataCrc(RarCrc32Raw.Update(0x0FFFFFFF, data)) != h.DataCrc) return null;
    var n = h.DataSectors;
    var checks = new ushort[n];
    for (long i = 0; i < n; ++i) checks[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)(2 * i)));

    const int sector = Rar4RecoveryRecord.Sector;
    var protectedEnd = h.HeaderPos;
    var radius = Math.Abs(protectedEnd - n * sector) + SearchRadius;
    var found = new long[n];
    long drift = 0;
    var relocated = 0;
    var missing = 0;
    // Every sector but the last is whole; the last one's length is settled below.
    for (long i = 0; i < n - 1; ++i) {
      var expected = i * sector + drift;
      // Where the sector belongs, its own check is enough; elsewhere it needs confirming.
      var at = SectorMatches(s, expected, checks[i], protectedEnd, new byte[sector])
        ? expected
        : Rar4Search(s, expected, radius, i, checks, n - 1, protectedEnd);
      found[i] = at;
      if (at < 0) {
        ++missing;
        continue;
      }
      drift = at - i * sector;
      if (drift != 0) ++relocated;
    }

    // The last sector ends right before the record's header and is the only one whose length the
    // record does not give. Rebuilt from its parity group, its content is exact: its length is
    // where the bytes just before the header line up with the rebuilt sector's end, and never
    // inside its zero padding. Without the rebuild, only the drift of the sector before it says.
    var lastStart = (n - 1) * sector;
    var byDrift = protectedEnd - (lastStart + drift);
    var lastLength = -1L;
    var rebuilt = Rar4RebuildLast(s, h, data, found, checks);
    if (rebuilt != null) {
      lastLength = LastLengthFromTail(s, protectedEnd, rebuilt, byDrift);
      if (!(byDrift == lastLength && LastSectorMatches(s, protectedEnd, (int)lastLength, checks[n - 1]))) ++missing;
    } else if (byDrift is >= 1 and <= sector && LastSectorMatches(s, protectedEnd, (int)byDrift, checks[n - 1])) {
      lastLength = byDrift;
    } else {
      return null;
    }
    if (protectedEnd - lastLength != lastStart) ++relocated;

    output.SetLength(0);
    var buf = new byte[Chunk];
    for (long i = 0; i < n - 1; ++i) {
      if (found[i] >= 0) CopyRange(s, found[i], output, i * sector, sector, buf);
      else ZeroRange(output, i * sector, sector, buf);
    }
    output.Position = lastStart;
    if (rebuilt != null) output.Write(rebuilt, 0, (int)lastLength);
    else CopyRange(s, protectedEnd - lastLength, output, lastStart, lastLength, buf);
    var protectedSize = lastStart + lastLength;
    var recordEnd = h.HeaderPos + Rar4RecoveryRecord.HeaderSize + h.DataSize;
    CopyRange(s, h.HeaderPos, output, protectedSize, Rar4RecoveryRecord.HeaderSize + h.DataSize, buf);
    // The end block and anything after it come across unchanged.
    CopyRange(s, recordEnd, output, protectedSize + Rar4RecoveryRecord.HeaderSize + h.DataSize, s.Length - recordEnd, buf);
    output.Flush();
    return (relocated, missing);
  }

  // ---------------------------------------------------------------- RAR 5 helpers

  /// <summary>The RAR 5 service header (type 3) that ends exactly at <paramref name="firstBlock"/>, CRC-checked; null when none parses.</summary>
  internal static byte[]? Rar5ServiceHeaderBefore(Stream s, long firstBlock) {
    var buf = new byte[64];
    for (var length = 8; length <= 64 && length <= firstBlock; ++length) {
      var frame = buf.AsSpan(0, length);
      RarRecovery.ReadAt(s, firstBlock - length, frame);
      if (!TryFrame(frame, out var body) || body.Length == 0 || body[0] != 3) continue;
      return frame.ToArray();
    }
    return null;
  }

  /// <summary>A RAR 5 header frame filling <paramref name="frame"/> exactly, its CRC-32 intact.</summary>
  private static bool TryFrame(ReadOnlySpan<byte> frame, out ReadOnlySpan<byte> body) {
    body = default;
    if (frame.Length < 6) return false;
    ulong size = 0;
    var shift = 0;
    var p = 4;
    while (true) {
      if (p >= frame.Length || shift > 21) return false;
      var b = frame[p++];
      size |= (ulong)(b & 0x7F) << shift;
      shift += 7;
      if ((b & 0x80) == 0) break;
    }
    if ((ulong)(frame.Length - p) != size) return false;
    if (Crc32.Compute(frame[4..]) != BinaryPrimitives.ReadUInt32LittleEndian(frame)) return false;
    body = frame[p..];
    return true;
  }

  /// <summary>The end-of-archive header found at <paramref name="at"/>, or a plain one (header flags 4, end flags 0) as rar writes when it repairs.</summary>
  private static byte[] Rar5EndHeader(Stream s, long at) {
    var buf = new byte[8];
    for (var length = 7; length <= 8; ++length) {
      if (at + length > s.Length) break;
      RarRecovery.ReadAt(s, at, buf.AsSpan(0, length));
      if (TryFrame(buf.AsSpan(0, length), out var body) && body.Length > 0 && body[0] == 5)
        return buf.AsSpan(0, length).ToArray();
    }
    byte[] plain = [0, 0, 0, 0, 3, 5, 4, 0];
    BinaryPrimitives.WriteUInt32LittleEndian(plain, Crc32.Compute(plain.AsSpan(4)));
    return plain;
  }

  private static bool Matches64(Stream s, long at, int length, ulong crc, long limit) {
    if (at < 0 || at + length > limit) return false;
    var buf = new byte[Chunk];
    ulong c = 0;
    for (var p = 0; p < length; p += Chunk) {
      var len = Math.Min(Chunk, length - p);
      RarRecovery.ReadAt(s, at + p, buf.AsSpan(0, len));
      c = RarCrc64.Update(c, buf.AsSpan(0, len));
    }
    return c == crc;
  }

  /// <summary>
  /// The offset nearest <paramref name="expected"/> within <paramref name="radius"/> where
  /// <paramref name="length"/> bytes have CRC-64 register <paramref name="crc"/> (from 0, no final
  /// xor), by a rolling CRC over the window; -1 when there is none.
  /// </summary>
  private static long Search64(Stream s, long expected, long radius, int length, ulong crc, long limit) {
    var from = Math.Max(0, expected - radius);
    var to = Math.Min(limit - length, expected + radius);
    if (to < from) return -1;
    var outgoing = RollingCrc64.OutgoingTable(length);
    var window = new byte[length];
    RarRecovery.ReadAt(s, from, window);
    var c = RarCrc64.Update(0, window);
    var best = -1L;
    var head = 0;
    var next = new byte[Chunk];
    var nextLen = 0;
    var nextPos = 0;
    var streamPos = from + length;
    for (var at = from; ; ++at) {
      if (c == crc && (best < 0 || Math.Abs(at - expected) < Math.Abs(best - expected))) best = at;
      if (at >= to || (best >= 0 && at - expected >= Math.Abs(best - expected))) break;
      if (nextPos == nextLen) {
        nextLen = (int)Math.Min(Chunk, limit - streamPos);
        RarRecovery.ReadAt(s, streamPos, next.AsSpan(0, nextLen));
        streamPos += nextLen;
        nextPos = 0;
      }
      var incoming = next[nextPos++];
      var leaving = window[head];
      window[head] = incoming;
      head = head + 1 == length ? 0 : head + 1;
      c = RollingCrc64.Roll(c, incoming, outgoing[leaving]);
    }
    return best;
  }

  // ---------------------------------------------------------------- RAR 4 helpers

  private static bool SectorMatches(Stream s, long at, ushort check, long limit, byte[] buf) {
    const int sector = Rar4RecoveryRecord.Sector;
    if (at < 0 || at + sector > limit) return false;
    RarRecovery.ReadAt(s, at, buf.AsSpan(0, sector));
    return (ushort)RarCrc32Raw.Update(uint.MaxValue, buf.AsSpan(0, sector)) == check;
  }

  /// <summary>Sector <paramref name="i"/> checks out at <paramref name="at"/>, and enough of the whole sectors after it do too.</summary>
  private static bool Rar4Confirmed(Stream s, long at, long i, ushort[] checks, long wholeSectors, long limit) {
    var buf = new byte[Rar4RecoveryRecord.Sector];
    if (!SectorMatches(s, at, checks[i], limit, buf)) return false;
    var available = Math.Min(Rar4Lookahead, wholeSectors - 1 - i);
    var matched = 0;
    for (var k = 1L; k <= available; ++k)
      if (SectorMatches(s, at + k * Rar4RecoveryRecord.Sector, checks[i + k], limit, buf))
        ++matched;
    return matched >= Math.Min(Rar4Confirmations, available);
  }

  private static long Rar4Search(Stream s, long expected, long radius, long i, ushort[] checks, long wholeSectors, long limit) {
    const int sector = Rar4RecoveryRecord.Sector;
    var from = Math.Max(0, expected - radius);
    var to = Math.Min(limit - sector, expected + radius);
    if (to < from) return -1;
    var outgoing = RollingCrc32.OutgoingTable(sector);
    var constant = RarCrc32Raw.Update(uint.MaxValue, new byte[sector]);
    var window = new byte[sector];
    RarRecovery.ReadAt(s, from, window);
    var c = RarCrc32Raw.Update(0, window);
    var candidates = new List<long>();
    var head = 0;
    var next = new byte[Chunk];
    var nextLen = 0;
    var nextPos = 0;
    var streamPos = from + sector;
    for (var at = from; ; ++at) {
      if ((ushort)(c ^ constant) == checks[i]) candidates.Add(at);
      if (at >= to) break;
      if (nextPos == nextLen) {
        nextLen = (int)Math.Min(Chunk, limit - streamPos);
        RarRecovery.ReadAt(s, streamPos, next.AsSpan(0, nextLen));
        streamPos += nextLen;
        nextPos = 0;
      }
      var incoming = next[nextPos++];
      var leaving = window[head];
      window[head] = incoming;
      head = head + 1 == sector ? 0 : head + 1;
      c = RollingCrc32.Roll(c, incoming, outgoing[leaving]);
    }
    foreach (var at in candidates.OrderBy(a => Math.Abs(a - expected)))
      if (Rar4Confirmed(s, at, i, checks, wholeSectors, limit))
        return at;
    return -1;
  }

  private static bool LastSectorMatches(Stream s, long protectedEnd, int length, ushort check) {
    if (length > protectedEnd) return false;
    var buf = new byte[Rar4RecoveryRecord.Sector];
    RarRecovery.ReadAt(s, protectedEnd - length, buf.AsSpan(0, length));
    return (ushort)RarCrc32Raw.Update(uint.MaxValue, buf) == check;
  }

  /// <summary>
  /// The last sector, zero-padded, rebuilt from its parity sector and the other sectors of its
  /// group; null when one of those was not found or the result fails the sector's check.
  /// </summary>
  private static byte[]? Rar4RebuildLast(Stream s, Rar4RecoveryRecord.Header h, byte[] data, long[] found, ushort[] checks) {
    const int sector = Rar4RecoveryRecord.Sector;
    var n = h.DataSectors;
    var last = n - 1;
    var k = last % h.RecoverySectors;
    var rebuilt = data.AsSpan((int)(2 * n + k * sector), sector).ToArray();
    var buf = new byte[sector];
    for (var j = k; j < last; j += h.RecoverySectors) {
      if (found[j] < 0) return null;
      RarRecovery.ReadAt(s, found[j], buf);
      for (var b = 0; b < sector; ++b) rebuilt[b] ^= buf[b];
    }
    return (ushort)RarCrc32Raw.Update(uint.MaxValue, rebuilt) == checks[last] ? rebuilt : null;
  }

  /// <summary>
  /// The length of the rebuilt last sector: at least up to its last non-zero byte; of those, the
  /// one whose end matches the most bytes just before the record header, the drift's own when
  /// that ties (damage right at the end), and the shortest when the drift is out of range.
  /// </summary>
  private static long LastLengthFromTail(Stream s, long protectedEnd, byte[] rebuilt, long byDrift) {
    const int sector = Rar4RecoveryRecord.Sector;
    var shortest = sector;
    while (shortest > 1 && rebuilt[shortest - 1] == 0) --shortest;
    var tail = new byte[sector];
    var have = (int)Math.Min(sector, protectedEnd);
    RarRecovery.ReadAt(s, protectedEnd - have, tail.AsSpan(sector - have, have));
    var best = byDrift >= shortest && byDrift <= sector ? byDrift : shortest;
    var bestRun = Run(best);
    for (var length = shortest; length <= sector; ++length) {
      var run = Run(length);
      if (run > bestRun) (best, bestRun) = (length, run);
    }
    return best;

    // Bytes that agree, walking back from the end of rebuilt[..length] and from the header.
    int Run(long length) {
      var r = 0;
      while (r < length && r < have && rebuilt[length - 1 - r] == tail[sector - 1 - r]) ++r;
      return r;
    }
  }

  // ---------------------------------------------------------------- copying

  private static void CopyRange(Stream from, long source, Stream to, long target, long count, byte[] buf) {
    to.Position = target;
    while (count > 0) {
      var len = (int)Math.Min(buf.Length, count);
      RarRecovery.ReadAt(from, source, buf.AsSpan(0, len));
      to.Write(buf, 0, len);
      source += len;
      count -= len;
    }
  }

  private static void ZeroRange(Stream to, long target, long count, byte[] buf) {
    Array.Clear(buf);
    to.Position = target;
    while (count > 0) {
      var len = (int)Math.Min(buf.Length, count);
      to.Write(buf, 0, len);
      count -= len;
    }
  }
}

/// <summary>Rolling CRC-64 (ECMA-182, reflected) over a fixed window, register from 0 and no final xor, so the CRC is linear.</summary>
internal static class RollingCrc64 {
  /// <summary>For every byte value b, the register after b followed by <paramref name="window"/> zero bytes.</summary>
  internal static ulong[] OutgoingTable(int window) {
    var zeros = ZeroOperator(window);
    var table = new ulong[256];
    Span<byte> one = stackalloc byte[1];
    for (var b = 0; b < 256; ++b) {
      one[0] = (byte)b;
      table[b] = Apply(zeros, RarCrc64.Update(0, one));
    }
    return table;
  }

  /// <summary>Slides the window one byte: appends <paramref name="incoming"/> and removes the byte whose table entry is <paramref name="outgoing"/>.</summary>
  internal static ulong Roll(ulong register, byte incoming, ulong outgoing) {
    Span<byte> one = [incoming];
    return RarCrc64.Update(register, one) ^ outgoing;
  }

  /// <summary>The linear map "feed <paramref name="count"/> zero bytes" as 64 column images, by repeated squaring.</summary>
  private static ulong[] ZeroOperator(long count) {
    var step = new ulong[64];
    Span<byte> zero = [0];
    for (var k = 0; k < 64; ++k) step[k] = RarCrc64.Update(1UL << k, zero);
    var result = new ulong[64];
    for (var k = 0; k < 64; ++k) result[k] = 1UL << k;
    while (count > 0) {
      if ((count & 1) != 0) result = Compose(step, result);
      step = Compose(step, step);
      count >>= 1;
    }
    return result;
  }

  /// <summary>a ∘ b.</summary>
  private static ulong[] Compose(ulong[] a, ulong[] b) {
    var r = new ulong[64];
    for (var k = 0; k < 64; ++k) r[k] = Apply(a, b[k]);
    return r;
  }

  private static ulong Apply(ulong[] m, ulong v) {
    ulong r = 0;
    for (var k = 0; v != 0; ++k, v >>= 1)
      if ((v & 1) != 0) r ^= m[k];
    return r;
  }
}

/// <summary>Rolling CRC-32 (reflected 0xEDB88320) over a fixed window, register from 0: linear, as <see cref="RollingCrc64"/>.</summary>
internal static class RollingCrc32 {
  internal static uint[] OutgoingTable(int window) {
    var table = new uint[256];
    var zeros = new byte[window];
    Span<byte> one = stackalloc byte[1];
    for (var b = 0; b < 256; ++b) {
      one[0] = (byte)b;
      table[b] = RarCrc32Raw.Update(RarCrc32Raw.Update(0, one), zeros);
    }
    return table;
  }

  internal static uint Roll(uint register, byte incoming, uint outgoing) {
    Span<byte> one = [incoming];
    return RarCrc32Raw.Update(register, one) ^ outgoing;
  }
}
