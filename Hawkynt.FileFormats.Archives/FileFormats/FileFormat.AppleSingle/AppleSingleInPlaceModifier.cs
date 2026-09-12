#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.AppleSingle;

/// <summary>
/// In-place modifier for AppleSingle (RFC 1740) containers. Supports
/// Add / Replace / Remove against the 12-byte entry-directory slots and
/// the per-entry payload area that follows them.
/// </summary>
/// <remarks>
/// <para><b>Add new id</b>: appends the payload at EOF, then grows the
/// directory by one 12-byte slot. Existing payloads remain byte-identical
/// — when the existing data area starts at <c>26 + 12*N</c> (the normal
/// layout produced by <see cref="AppleSingleWriter"/>), each payload is
/// physically shifted forward by 12 bytes and its directory offset is
/// rewritten so the byte content at the new offset matches the byte
/// content at the old offset.</para>
/// <para><b>Replace existing id</b>: when the new bytes fit at the
/// previous offset, the slot is rewritten in place; otherwise the slot
/// is repointed to a fresh region appended at EOF and the previous
/// payload range is zero-wiped so the old bytes leave no trace.</para>
/// <para><b>Remove</b>: zero-wipes the payload range, then removes the
/// 12-byte slot from the directory by compacting subsequent slots
/// forward. Surviving payload offsets do not move, so their bytes are
/// byte-identical at the same absolute offsets.</para>
/// </remarks>
public static class AppleSingleInPlaceModifier {

  /// <summary>
  /// Appends a brand-new entry id at the end of the directory.
  /// </summary>
  public static void AddEntry(Stream archive, uint entryId, byte[] data) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(data);

    var entries = ReadEntries(archive);
    if (entries.Count == ushort.MaxValue)
      throw new InvalidOperationException("AppleSingle: directory full (65535 entries).");

    var allBytes = ReadAllBytes(archive);

    // Read the full payloads of existing entries before we mutate any bytes.
    var existingPayloads = new List<(uint Id, byte[] Bytes)>(entries.Count);
    foreach (var e in entries)
      existingPayloads.Add((e.EntryId, allBytes.AsSpan((int)e.DataOffset, e.DataLength).ToArray()));

    // Compute the new layout: data area now starts 12 bytes later.
    var oldDataStart = 26 + 12 * entries.Count;
    var newDataStart = oldDataStart + 12;

    // Find the lowest existing payload offset (usually == oldDataStart) so we
    // can compute the shift correctly even if the writer left a gap.
    var lowestOldOffset = entries.Count > 0 ? entries.Min(e => e.DataOffset) : oldDataStart;
    var highestOldEnd = entries.Count > 0 ? entries.Max(e => e.DataOffset + e.DataLength) : oldDataStart;

    // Resize buffer to fit the shifted payloads + the new payload at the end.
    var newSize = (int)(highestOldEnd + 12 + data.Length);
    Array.Resize(ref allBytes, newSize);

    // Rewrite existing payloads at their new (shifted) offsets and patch the
    // directory entries to point at the new offsets.
    for (var i = 0; i < entries.Count; i++) {
      var e = entries[i];
      var newOff = e.DataOffset + 12;
      existingPayloads[i].Bytes.CopyTo(allBytes.AsSpan((int)newOff));
      var slotBase = 26 + 12 * i;
      BinaryPrimitives.WriteUInt32BigEndian(allBytes.AsSpan(slotBase), e.EntryId);
      BinaryPrimitives.WriteUInt32BigEndian(allBytes.AsSpan(slotBase + 4), (uint)newOff);
      BinaryPrimitives.WriteUInt32BigEndian(allBytes.AsSpan(slotBase + 8), (uint)e.DataLength);
    }

    // Append the new payload at the end.
    var newPayloadOffset = (int)(highestOldEnd + 12);
    data.CopyTo(allBytes.AsSpan(newPayloadOffset));

    // Write the new directory slot just past the existing ones (the old data start).
    var newSlotBase = 26 + 12 * entries.Count;
    BinaryPrimitives.WriteUInt32BigEndian(allBytes.AsSpan(newSlotBase), entryId);
    BinaryPrimitives.WriteUInt32BigEndian(allBytes.AsSpan(newSlotBase + 4), (uint)newPayloadOffset);
    BinaryPrimitives.WriteUInt32BigEndian(allBytes.AsSpan(newSlotBase + 8), (uint)data.Length);

    // Bump the entry count.
    BinaryPrimitives.WriteUInt16BigEndian(allBytes.AsSpan(24), (ushort)(entries.Count + 1));

    archive.SetLength(0);
    archive.Position = 0;
    archive.Write(allBytes, 0, newSize);
  }

  /// <summary>
  /// Replaces or adds the given entry id with new payload bytes. When the
  /// id already exists the previous payload range is zero-wiped first.
  /// </summary>
  public static void ReplaceEntry(Stream archive, uint entryId, byte[] data) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(data);

    var entries = ReadEntries(archive);
    var existing = entries.FindIndex(e => e.EntryId == entryId);
    if (existing < 0) {
      AddEntry(archive, entryId, data);
      return;
    }

    var slot = entries[existing];
    if (data.Length == slot.DataLength) {
      // Same-size fast path: just overwrite the payload bytes in place.
      archive.Position = slot.DataOffset;
      archive.Write(data, 0, data.Length);
      return;
    }

    // Different size: zero-wipe the old payload, append the new payload at EOF, repoint the slot.
    ZeroRange(archive, slot.DataOffset, slot.DataLength);
    archive.Position = archive.Length;
    var newOffset = archive.Position;
    archive.Write(data, 0, data.Length);

    archive.Position = 26 + 12 * existing + 4;
    Span<byte> dirBuf = stackalloc byte[8];
    BinaryPrimitives.WriteUInt32BigEndian(dirBuf, (uint)newOffset);
    BinaryPrimitives.WriteUInt32BigEndian(dirBuf[4..], (uint)data.Length);
    archive.Write(dirBuf);
  }

  /// <summary>
  /// Removes the entry with the given id. The payload range is zero-wiped
  /// and the 12-byte directory slot is compacted out by shifting trailing
  /// slots forward.
  /// </summary>
  public static bool RemoveEntry(Stream archive, uint entryId) {
    ArgumentNullException.ThrowIfNull(archive);

    var entries = ReadEntries(archive);
    var idx = entries.FindIndex(e => e.EntryId == entryId);
    if (idx < 0) return false;

    var slot = entries[idx];
    ZeroRange(archive, slot.DataOffset, slot.DataLength);

    // Shift directory slots [idx+1 .. end) forward by one slot (12 bytes).
    var directoryEnd = 26 + 12 * entries.Count;
    var tailCount = entries.Count - 1 - idx;
    if (tailCount > 0) {
      var tailBytes = tailCount * 12;
      var tailBuf = new byte[tailBytes];
      archive.Position = 26 + 12 * (idx + 1);
      archive.ReadExactly(tailBuf);
      archive.Position = 26 + 12 * idx;
      archive.Write(tailBuf, 0, tailBytes);
    }
    // Wipe the trailing 12 bytes (now stale).
    archive.Position = directoryEnd - 12;
    archive.Write(new byte[12], 0, 12);

    // Decrement entry count.
    archive.Position = 24;
    Span<byte> cnt = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(cnt, (ushort)(entries.Count - 1));
    archive.Write(cnt);

    return true;
  }

  // ── Helpers ───────────────────────────────────────────────────────

  private record EntryDirEntry(uint EntryId, long DataOffset, int DataLength);

  private static List<EntryDirEntry> ReadEntries(Stream archive) {
    archive.Position = 0;
    if (archive.Length < 26) throw new InvalidDataException("AppleSingle: archive shorter than 26-byte header.");
    Span<byte> hdr = stackalloc byte[26];
    archive.ReadExactly(hdr);
    var magic = BinaryPrimitives.ReadUInt32BigEndian(hdr);
    if (magic != AppleSingleReader.MagicSingle && magic != AppleSingleReader.MagicDouble)
      throw new InvalidDataException($"AppleSingle: bad magic 0x{magic:X8}");
    var numEntries = BinaryPrimitives.ReadUInt16BigEndian(hdr[24..]);
    var entries = new List<EntryDirEntry>(numEntries);
    Span<byte> slot = stackalloc byte[12];
    for (var i = 0; i < numEntries; i++) {
      archive.Position = 26 + 12 * i;
      archive.ReadExactly(slot);
      var id = BinaryPrimitives.ReadUInt32BigEndian(slot);
      var off = BinaryPrimitives.ReadUInt32BigEndian(slot[4..]);
      var len = BinaryPrimitives.ReadUInt32BigEndian(slot[8..]);
      entries.Add(new EntryDirEntry(id, off, (int)len));
    }
    return entries;
  }

  private static byte[] ReadAllBytes(Stream archive) {
    archive.Position = 0;
    var buf = new byte[archive.Length];
    archive.ReadExactly(buf);
    return buf;
  }

  private static void ZeroRange(Stream archive, long offset, int length) {
    if (length <= 0) return;
    archive.Position = offset;
    var zeros = new byte[Math.Min(length, 8192)];
    var remaining = length;
    while (remaining > 0) {
      var chunk = Math.Min(zeros.Length, remaining);
      archive.Write(zeros, 0, chunk);
      remaining -= chunk;
    }
  }
}
