#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.BcacheFs;

/// <summary>
/// Structural parser for bcachefs journal sets (<c>struct jset</c>) and their
/// variable-length entries. This layer deliberately preserves unknown entry
/// types: semantic replay decides whether it understands them, the byte parser
/// must never discard information merely because a newer kernel learned a new
/// journal operation.
/// </summary>
internal static class BcacheFsJournalFormat {
  internal const int SetHeaderBytes = 56;
  internal const int EntryHeaderBytes = 8;

  internal static ulong ExpectedMagic(ulong filesystemMagic)
    => filesystemMagic ^ BcacheFsOnDiskCatalog.JournalSetMagicXor;

  internal static bool TryParse(
      ReadOnlySpan<byte> bytes,
      ulong filesystemMagic,
      out BcacheFsJournalSet? set,
      out string error) {
    set = null;
    error = string.Empty;

    if (bytes.Length < SetHeaderBytes) {
      error = $"journal set is {bytes.Length} bytes; header needs {SetHeaderBytes}.";
      return false;
    }

    var checksumLo = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    var checksumHi = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
    var magic = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
    if (magic != ExpectedMagic(filesystemMagic)) {
      error = $"journal magic 0x{magic:X16} does not match filesystem magic.";
      return false;
    }

    var sequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
    var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]);
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]);
    var payloadU64s = BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]);
    var readClock = BinaryPrimitives.ReadUInt16LittleEndian(bytes[44..]);
    var writeClock = BinaryPrimitives.ReadUInt16LittleEndian(bytes[46..]);
    var lastSequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..]);

    var payloadBytes = (long)payloadU64s * sizeof(ulong);
    var totalBytes = SetHeaderBytes + payloadBytes;
    if (totalBytes > bytes.Length || totalBytes > int.MaxValue) {
      error = $"journal set claims {totalBytes} bytes but only {bytes.Length} are available.";
      return false;
    }

    var entries = new List<BcacheFsJournalEntry>();
    var cursor = SetHeaderBytes;
    var end = (int)totalBytes;
    while (cursor < end) {
      if (end - cursor < EntryHeaderBytes) {
        error = $"journal entry header at byte {cursor} is truncated.";
        return false;
      }

      var payloadWords = BinaryPrimitives.ReadUInt16LittleEndian(bytes[cursor..]);
      var btree = bytes[cursor + 2];
      var level = bytes[cursor + 3];
      var rawType = bytes[cursor + 4];
      var entryBytes = EntryHeaderBytes + (long)payloadWords * sizeof(ulong);
      if (entryBytes > int.MaxValue || cursor + entryBytes > end) {
        error = $"journal entry at byte {cursor} runs past the set boundary.";
        return false;
      }

      var payloadLength = (int)entryBytes - EntryHeaderBytes;
      var payload = bytes.Slice(cursor + EntryHeaderBytes, payloadLength).ToArray();
      entries.Add(new BcacheFsJournalEntry(
        new BcacheFsJournalEntryHeader(payloadWords, btree, level, rawType),
        payload));
      cursor += (int)entryBytes;
    }

    var header = new BcacheFsJournalSetHeader(
      checksumLo, checksumHi, magic, sequence, version, flags, payloadU64s,
      readClock, writeClock, lastSequence);
    set = new BcacheFsJournalSet(header, entries);
    return true;
  }
}

internal readonly record struct BcacheFsJournalSetHeader(
  ulong ChecksumLo,
  ulong ChecksumHi,
  ulong Magic,
  ulong Sequence,
  uint Version,
  uint Flags,
  uint PayloadU64s,
  ushort ReadClock,
  ushort WriteClock,
  ulong LastSequence) {

  internal BcacheFsChecksumType ChecksumType
    => (BcacheFsChecksumType)(this.Flags & 0xF);

  internal bool BigEndian => (this.Flags & (1U << 4)) != 0;
  internal bool NoFlush => (this.Flags & (1U << 5)) != 0;
  internal bool HasOverwrites => (this.Flags & (1U << 6)) != 0;

  internal long TotalBytes
    => BcacheFsJournalFormat.SetHeaderBytes + (long)this.PayloadU64s * sizeof(ulong);
}

internal readonly record struct BcacheFsJournalEntryHeader(
  ushort PayloadU64s,
  byte BtreeId,
  byte Level,
  byte RawType) {

  internal BcacheFsJournalEntryType? Type
    => Enum.IsDefined(typeof(BcacheFsJournalEntryType), this.RawType)
      ? (BcacheFsJournalEntryType)this.RawType
      : null;

  internal int TotalBytes
    => checked(BcacheFsJournalFormat.EntryHeaderBytes + this.PayloadU64s * sizeof(ulong));
}

internal sealed record BcacheFsJournalEntry(
  BcacheFsJournalEntryHeader Header,
  byte[] Payload);

internal sealed record BcacheFsJournalSet(
  BcacheFsJournalSetHeader Header,
  IReadOnlyList<BcacheFsJournalEntry> Entries);
