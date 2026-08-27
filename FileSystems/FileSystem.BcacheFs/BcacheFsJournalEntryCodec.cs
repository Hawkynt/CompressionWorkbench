#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.BcacheFs;

/// <summary>Common jset_entry framing used by both journal sets and sb clean.</summary>
internal static class BcacheFsJournalEntryCodec {
  internal static bool TryParseList(
      ReadOnlySpan<byte> bytes,
      out IReadOnlyList<BcacheFsJournalEntry> entries,
      out string error) {
    var result = new List<BcacheFsJournalEntry>();
    var cursor = 0;
    while (cursor < bytes.Length) {
      if (bytes.Length - cursor < BcacheFsJournalFormat.EntryHeaderBytes) {
        entries = result;
        error = $"journal entry header at byte {cursor} is truncated.";
        return false;
      }

      var payloadU64s = BinaryPrimitives.ReadUInt16LittleEndian(bytes[cursor..]);
      var totalBytes = BcacheFsJournalFormat.EntryHeaderBytes + (long)payloadU64s * sizeof(ulong);
      if (totalBytes > int.MaxValue || cursor + totalBytes > bytes.Length) {
        entries = result;
        error = $"journal entry at byte {cursor} runs past container boundary.";
        return false;
      }

      var payloadLength = (int)totalBytes - BcacheFsJournalFormat.EntryHeaderBytes;
      result.Add(new BcacheFsJournalEntry(
        new BcacheFsJournalEntryHeader(
          payloadU64s,
          bytes[cursor + 2],
          bytes[cursor + 3],
          bytes[cursor + 4]),
        bytes.Slice(cursor + BcacheFsJournalFormat.EntryHeaderBytes, payloadLength).ToArray()));
      cursor += (int)totalBytes;
    }

    entries = result;
    error = string.Empty;
    return true;
  }

  internal static bool TryReadKeys(
      BcacheFsJournalEntry entry,
      out IReadOnlyList<BcacheFsRawKey> keys,
      out string error,
      bool bigEndian = false) {
    var result = new List<BcacheFsRawKey>();
    var cursor = 0;
    while (cursor < entry.Payload.Length) {
      var remaining = entry.Payload.AsSpan(cursor);
      if (remaining.Length < 8 || remaining[0] == 0) {
        keys = result;
        error = $"zero/truncated bkey at payload byte {cursor}.";
        return false;
      }
      var keyBytes = remaining[0] * sizeof(ulong);
      if (keyBytes > remaining.Length) {
        keys = result;
        error = $"bkey at payload byte {cursor} overruns journal entry.";
        return false;
      }
      if ((remaining[1] & 0x7F) != BcacheFsFormat.KeyFormatCurrent) {
        keys = result;
        error = $"durable journal bkey has format {remaining[1] & 0x7F}; expected KEY_FORMAT_CURRENT.";
        return false;
      }
      if (!BcacheFsRawKeyCodec.TryDecode(
          remaining[..keyBytes], null, out var key, out var decodeError, bigEndian)) {
        keys = result;
        error = decodeError;
        return false;
      }
      result.Add(key!);
      cursor += keyBytes;
    }

    keys = result;
    error = string.Empty;
    return true;
  }
}
