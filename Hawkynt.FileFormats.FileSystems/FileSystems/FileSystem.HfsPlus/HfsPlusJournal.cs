using System.Buffers.Binary;

namespace FileSystem.HfsPlus;

/// <summary>
/// The HFS+ metadata journal: where it is, whether it holds transactions, and the blocks a replay
/// of those transactions writes.
/// </summary>
/// <remarks>
/// <para>
/// Apple TN1150 "Journal" is the normative source. The volume header's journalInfoBlock names the
/// journal info block (big-endian: flags, 32-byte device signature, offset u64, size u64); the
/// journal header at that offset (magic 'JNLx', endian 0x12345678, start u64, end u64, size u64,
/// blhdr_size u32, checksum u32, jhdr_size u32) is written in the byte order of the machine that
/// wrote it, which the magic reveals. The bytes between start and end, in a circular buffer that
/// skips the header's jhdr_size bytes when it wraps, are block lists: a header (max_blocks u16,
/// num_blocks u16, bytes_used u32, checksum u32, flags u32) whose first block_info is reserved,
/// then (bnum u64, bsize u32, reserved u32) per block, then the blocks' bytes. Block bnum lands
/// at byte bnum * jhdr_size of the volume; a bnum of all ones is a block the transaction dropped.
/// Both checksums are TN1150's: ~fold(c = (c &lt;&lt; 8) ^ (c + byte)) over the header with its
/// checksum field zeroed — 44 bytes for the journal header, 32 for a block list header.
/// </para>
/// <para>
/// Replay stops at the first block list whose header fails its checks and keeps only what came
/// before the last transaction seen to begin (a block list flagged as a transaction's first, flags
/// bit 2, opens one; a journal that never flags makes each block list a transaction): that
/// transaction's end was never confirmed, so what a crash may have left half-written is not
/// applied. Apple's replay (hfs_journal.c) stops at the same place.
/// </para>
/// </remarks>
internal sealed class HfsPlusJournal {
  private const uint VolumeJournaledMask = 1u << 13;
  private const uint JournalInFileSystem = 1;
  private const uint JournalOnOtherDevice = 2;
  private const uint JournalNeedsInit = 4;
  private const uint JournalMagic = 0x4A4E4C78;  // 'JNLx'
  private const uint EndianMagic = 0x12345678;
  private const int HeaderChecksumBytes = 44;
  private const int BlockListChecksumBytes = 32;
  private const uint CheckChecksumsFlag = 1;      // BLHDR_CHECK_CHECKSUMS
  private const uint FirstHeaderFlag = 2;         // BLHDR_FIRST_HEADER

  /// <summary>Byte offset of the journal header within the volume.</summary>
  internal long Offset { get; }
  /// <summary>The journal's size in bytes, header included.</summary>
  internal long Size { get; }
  internal bool OnOtherDevice { get; }
  internal bool NeedsInitialization { get; }

  private HfsPlusJournal(long offset, long size, uint flags) {
    this.Offset = offset;
    this.Size = size;
    this.OnOtherDevice = (flags & JournalOnOtherDevice) != 0 || (flags & JournalInFileSystem) == 0;
    this.NeedsInitialization = (flags & JournalNeedsInit) != 0;
  }

  internal readonly record struct Header(bool LittleEndian, long Start, long End, long Size, uint BlockListHeaderSize, uint SectorSize);

  /// <summary>The journal of a volume whose header says it is journaled, or null when it says not or the info block is out of reach.</summary>
  internal static HfsPlusJournal? Locate(ReadOnlySpan<byte> volumeHeader, Func<long, int, byte[]?> read, uint blockSize) {
    if ((BinaryPrimitives.ReadUInt32BigEndian(volumeHeader[4..]) & VolumeJournaledMask) == 0) return null;
    var infoBlock = BinaryPrimitives.ReadUInt32BigEndian(volumeHeader[12..]);
    if (infoBlock == 0) return null;
    var info = read((long)infoBlock * blockSize, 52);
    if (info is null) return null;
    return new HfsPlusJournal((long)BinaryPrimitives.ReadUInt64BigEndian(info.AsSpan(36)),
      (long)BinaryPrimitives.ReadUInt64BigEndian(info.AsSpan(44)), BinaryPrimitives.ReadUInt32BigEndian(info));
  }

  /// <summary>Whether the volume in <paramref name="volume"/> is journaled and its journal holds transactions.</summary>
  internal static bool HasPendingTransactions(Stream volume) {
    if (!volume.CanSeek || volume.Length < 1536) return false;
    byte[]? Read(long offset, int length) {
      if (offset < 0 || offset + length > volume.Length) return null;
      var buffer = new byte[length];
      volume.Position = offset;
      volume.ReadExactly(buffer);
      return buffer;
    }
    var header = Read(1024, 512)!;
    var signature = BinaryPrimitives.ReadUInt16BigEndian(header);
    if (signature is not (0x482B or 0x4858)) return false;
    var blockSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(40));
    if (blockSize == 0 || Locate(header, Read, blockSize) is not { } journal) return false;
    var pending = journal.TryReadHeader(Read, out var h) && h.Start != h.End;
    volume.Position = 0;
    return pending;
  }

  internal static HfsPlusJournal? Locate(byte[] image, ReadOnlySpan<byte> volumeHeader, uint blockSize)
    => Locate(volumeHeader, (offset, length) => offset >= 0 && offset + length <= image.Length ? image.AsSpan((int)offset, length).ToArray() : null, blockSize);

  internal bool TryReadHeader(byte[] image, out Header header)
    => this.TryReadHeader((offset, length) => offset >= 0 && offset + length <= image.Length ? image.AsSpan((int)offset, length).ToArray() : null, out header);

  /// <summary>Reads and checks the journal header (magic, endian mark, checksum, sizes).</summary>
  internal bool TryReadHeader(Func<long, int, byte[]?> read, out Header header) {
    header = default;
    if (this.OnOtherDevice || this.Size <= 0) return false;
    var raw = read(this.Offset, 48);
    if (raw is null) return false;
    bool little;
    if (BinaryPrimitives.ReadUInt32BigEndian(raw) == JournalMagic) little = false;
    else if (BinaryPrimitives.ReadUInt32LittleEndian(raw) == JournalMagic) little = true;
    else return false;
    if (U32(raw, 4, little) != EndianMagic) return false;
    var stored = U32(raw, 36, little);
    var check = raw.AsSpan(0, HeaderChecksumBytes).ToArray();
    check.AsSpan(36, 4).Clear();
    if (Checksum(check) != stored) return false;
    header = new Header(little, (long)U64(raw, 8, little), (long)U64(raw, 16, little), (long)U64(raw, 24, little), U32(raw, 32, little), U32(raw, 40, little));
    var h = header;
    return h.Size == this.Size && h.SectorSize is >= 512 and <= 65536 && h.BlockListHeaderSize >= BlockListChecksumBytes
           && h.Start >= h.SectorSize && h.Start < h.Size && h.End >= h.SectorSize && h.End < h.Size;
  }

  /// <summary>
  /// The (volume offset, bytes) writes a replay of the journal's committed transactions makes, in
  /// the order they apply; empty when the journal is empty or not readable.
  /// </summary>
  internal List<(long Offset, byte[] Data)> Replay(Func<long, int, byte[]?> read) => this.Plan(read).Writes;

  /// <summary>
  /// What a replay does: the writes, in the order they apply, and where the journal's start and
  /// end both point once they are applied. <see cref="ReachedEnd"/> is false when a block list
  /// between start and end failed its checks, so the walk never confirmed where the log ends.
  /// </summary>
  internal sealed record ReplayPlan(List<(long Offset, byte[] Data)> Writes, long NewStart, bool ReachedEnd);

  /// <summary>
  /// Plans the replay. The block lists from start to end are applied as committed. Past the end
  /// the walk carries on while the log continues: a block list whose header checksum holds and
  /// whose sequence number follows (the next transaction's is one more than the last one's; a
  /// block list continuing a transaction repeats its number) and, when it is flagged
  /// BLHDR_CHECK_CHECKSUMS, every block's TN1150 checksum over its whole bytes matches. A
  /// transaction that is not whole there is not applied, and the journal then starts where the
  /// last whole one ended. macOS 26 behaves exactly so on volumes it left with a committed and
  /// with a torn transaction past the recorded end (Compression.Tests HfsPlusJournalAppleReplayTests).
  /// </summary>
  internal ReplayPlan Plan(Func<long, int, byte[]?> read) {
    var writes = new List<(long, byte[])>();
    if (!this.TryReadHeader(read, out var header)) return new(writes, 0, false);
    var transactionStart = 0;   // index of the first write of the transaction being read
    var flagsTransactions = false;
    var position = header.Start;
    var guard = 0;
    uint? lastSequence = null;
    while (position != header.End && guard++ < 1_000_000) {
      var listStart = position;
      var blockList = this.ReadCircular(read, header, ref position, (int)header.BlockListHeaderSize);
      if (blockList is null || !ValidBlockList(blockList, header, out var count, out var bytesUsed, out var flags)) break;
      // A journal that flags transaction starts groups block lists; one that never does makes
      // each block list its own transaction.
      if ((flags & FirstHeaderFlag) != 0) {
        flagsTransactions = true;
        transactionStart = writes.Count;
      } else if (!flagsTransactions) {
        transactionStart = writes.Count;
      }
      if (this.ReadBlocks(read, header, blockList, count, ref position, verify: false) is not { } listWrites) break;
      writes.AddRange(listWrites);
      lastSequence = U32(blockList, 28, header.LittleEndian);
      // A block list's bytes_used covers its header and every block, which is where the next one starts.
      position = Advance(header, listStart, bytesUsed);
    }
    if (position != header.End) {
      writes.RemoveRange(transactionStart, writes.Count - transactionStart);
      return new(writes, position, false);
    }
    return new(writes, this.ContinuePastEnd(read, header, lastSequence, writes), true);
  }

  /// <summary>
  /// Applies whole transactions that follow the recorded end (see <see cref="Plan"/>) and returns
  /// the position just after the last one applied.
  /// </summary>
  private long ContinuePastEnd(Func<long, int, byte[]?> read, Header header, uint? lastSequence, List<(long, byte[])> writes) {
    // Only a log whose replayed part numbers its transactions says what may follow it.
    if (lastSequence is not { } last) return header.End;
    var committedEnd = header.End;
    var listStart = header.End;
    var pending = new List<(long, byte[])>();
    uint? transaction = null;
    var transactionEnd = header.End;
    for (var guard = 0; guard < 1_000_000; guard++) {
      var position = listStart;
      var blockList = this.ReadCircular(read, header, ref position, (int)header.BlockListHeaderSize);
      if (blockList is null || !ValidBlockList(blockList, header, out var count, out var bytesUsed, out var flags)) break;
      var sequence = U32(blockList, 28, header.LittleEndian);
      if ((flags & FirstHeaderFlag) != 0) {
        // A new transaction follows, so the one before it is whole.
        if (transaction is { } previous) {
          writes.AddRange(pending);
          pending.Clear();
          committedEnd = transactionEnd;
          last = previous;
          transaction = null;
        }
        if (sequence != unchecked(last + 1)) break;
        transaction = sequence;
      } else if (transaction is null || sequence != transaction.Value) {
        break;
      }
      if (this.ReadBlocks(read, header, blockList, count, ref position, verify: (flags & CheckChecksumsFlag) != 0) is not { } listWrites) {
        // A torn block list: the transaction it belongs to is dropped whole.
        pending.Clear();
        transaction = null;
        break;
      }
      // The log past its end never runs over its start: those bytes are still the live log.
      var next = Advance(header, listStart, bytesUsed);
      if (Distance(header, header.End, next) > Distance(header, header.End, header.Start)) {
        pending.Clear();
        transaction = null;
        break;
      }
      pending.AddRange(listWrites);
      listStart = transactionEnd = next;
      if (listStart == header.Start) break;
    }
    // The last transaction whose every block list checked out is whole as far as the log shows.
    if (transaction is not null) {
      writes.AddRange(pending);
      committedEnd = transactionEnd;
    }
    return committedEnd;
  }

  /// <summary>The writes one block list carries, or null when a block is out of reach or (when verified) fails its checksum.</summary>
  private List<(long, byte[])>? ReadBlocks(Func<long, int, byte[]?> read, Header header, byte[] blockList, int count, ref long position, bool verify) {
    var listWrites = new List<(long, byte[])>();
    for (var i = 1; i < count; i++) {
      var bnum = U64(blockList, 16 + 16 * i, header.LittleEndian);
      var size = (int)U32(blockList, 24 + 16 * i, header.LittleEndian);
      if (size <= 0) return null;
      var data = this.ReadCircular(read, header, ref position, size);
      if (data is null) return null;
      if (verify && Checksum(data) != U32(blockList, 28 + 16 * i, header.LittleEndian)) return null;
      if (bnum != ulong.MaxValue) listWrites.Add(((long)bnum * header.SectorSize, data));
    }
    return listWrites;
  }

  /// <summary>
  /// Replays the journal into <paramref name="image"/> itself, as a mount does: every write of
  /// <see cref="Plan"/> applied, then the journal header's start and end both set to where the
  /// log ends and its checksum recomputed. The result is byte-identical to the volume macOS leaves
  /// after replaying the same journal. Returns false when the journal held nothing; throws
  /// <see cref="NotSupportedException"/> (image untouched) when a block list before the recorded
  /// end is damaged, since where macOS then restarts the log has not been measured.
  /// </summary>
  internal bool ReplayInPlace(byte[] image) {
    byte[]? Read(long offset, int length) => offset >= 0 && offset + length <= image.Length ? image.AsSpan((int)offset, length).ToArray() : null;
    if (!this.TryReadHeader(Read, out var header))
      throw new NotSupportedException("HFS+: the volume's journal header is not valid; it is not replayed.");
    var plan = this.Plan(Read);
    if (header.Start == header.End && plan.NewStart == header.End) return false;
    if (!plan.ReachedEnd)
      throw new NotSupportedException("HFS+: a block list inside the journal is damaged; the journal is not replayed in place.");
    foreach (var (offset, data) in plan.Writes) {
      if (offset < 0 || offset + data.Length > image.Length)
        throw new NotSupportedException("HFS+: the journal writes past the end of the image; it is not replayed in place.");
    }
    foreach (var (offset, data) in plan.Writes) data.CopyTo(image, offset);
    var raw = image.AsSpan((int)this.Offset, HeaderChecksumBytes);
    WriteU64(raw[8..], (ulong)plan.NewStart, header.LittleEndian);
    WriteU64(raw[16..], (ulong)plan.NewStart, header.LittleEndian);
    WriteU32(raw[36..], 0, header.LittleEndian);
    WriteU32(raw[36..], Checksum(raw), header.LittleEndian);
    return true;
  }

  private static void WriteU64(Span<byte> at, ulong value, bool little) {
    if (little) BinaryPrimitives.WriteUInt64LittleEndian(at, value);
    else BinaryPrimitives.WriteUInt64BigEndian(at, value);
  }

  private static void WriteU32(Span<byte> at, uint value, bool little) {
    if (little) BinaryPrimitives.WriteUInt32LittleEndian(at, value);
    else BinaryPrimitives.WriteUInt32BigEndian(at, value);
  }

  private static bool ValidBlockList(byte[] blockList, Header header, out int count, out long bytesUsed, out uint flags) {
    var little = header.LittleEndian;
    var max = little ? BinaryPrimitives.ReadUInt16LittleEndian(blockList) : BinaryPrimitives.ReadUInt16BigEndian(blockList);
    count = little ? BinaryPrimitives.ReadUInt16LittleEndian(blockList.AsSpan(2)) : BinaryPrimitives.ReadUInt16BigEndian(blockList.AsSpan(2));
    bytesUsed = U32(blockList, 4, little);
    flags = U32(blockList, 12, little);
    var check = blockList.AsSpan(0, BlockListChecksumBytes).ToArray();
    check.AsSpan(8, 4).Clear();
    if (Checksum(check) != U32(blockList, 8, little)) return false;
    return count > 0 && count <= max && 16 + 16L * count <= header.BlockListHeaderSize
           && bytesUsed >= header.BlockListHeaderSize && bytesUsed < header.Size;
  }

  /// <summary>Reads from the circular buffer, wrapping past the end to just after the journal header.</summary>
  private byte[]? ReadCircular(Func<long, int, byte[]?> read, Header header, ref long position, int length) {
    var result = new byte[length];
    var done = 0;
    while (done < length) {
      if (position >= header.Size) position = header.SectorSize;
      var take = (int)Math.Min(length - done, header.Size - position);
      var chunk = read(this.Offset + position, take);
      if (chunk is null) return null;
      chunk.CopyTo(result, done);
      done += take;
      position += take;
    }
    if (position >= header.Size) position = header.SectorSize;
    return result;
  }

  /// <summary>Bytes of log from <paramref name="from"/> forward to <paramref name="to"/>, wrapping past the header.</summary>
  private static long Distance(Header header, long from, long to)
    => to >= from ? to - from : header.Size - from + (to - header.SectorSize);

  private static long Advance(Header header, long position, long bytes) {
    position += bytes;
    if (position >= header.Size) position = position % header.Size + header.SectorSize;
    return position;
  }

  /// <summary>TN1150's journal checksum.</summary>
  internal static uint Checksum(ReadOnlySpan<byte> bytes) {
    var sum = 0u;
    foreach (var b in bytes) sum = (sum << 8) ^ (sum + b);
    return ~sum;
  }

  private static uint U32(byte[] b, int at, bool little) => little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
  private static ulong U64(byte[] b, int at, bool little) => little ? BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(at));
}
