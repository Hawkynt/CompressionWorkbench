#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.JpegArchive;

/// <summary>
/// Serializes a <see cref="TiffImage"/> back to a byte array. Layout order:
///   1. 8-byte TIFF header (byte-order + magic + IFD0 offset).
///   2. IFD0 table (count + entries + next-offset slot).
///   3. IFD1 table, if any.
///   4. Sub-IFD tables (EXIF, GPS, Interop) in the order they appear in IFD0.SubIfds.
///   5. Out-of-line values (any value > 4 bytes) are appended and their
///      offsets back-patched into the entry slot.
/// The writer does NOT preserve byte-for-byte identity with the original
/// TIFF area — it re-emits everything in a canonical order. The point is
/// that tag values and sub-IFD relationships round-trip, which is what
/// downstream consumers care about.
/// </summary>
public static class TiffWriter {
  private const int TiffHeaderSize = 8;

  public static byte[] Serialize(TiffImage image) {
    var ctx = new WriterContext(image.LittleEndian);

    // Reserve the TIFF header; filled at the end.
    ctx.Skip(TiffHeaderSize);

    var ifd0Offset = ctx.Position;
    var ifd0Slot = ctx.ReserveIfdTable(image.Ifd0.Entries.Count);

    var ifd1Offset = 0;
    IfdTableSlot? ifd1Slot = null;
    if (image.Ifd0.Next is { } ifd1) {
      ifd1Offset = ctx.Position;
      ifd1Slot = ctx.ReserveIfdTable(ifd1.Entries.Count);
    }
    ctx.PatchIfdNextOffset(ifd0Slot, ifd1Offset);
    ctx.PatchIfdCount(ifd0Slot, image.Ifd0.Entries.Count);
    if (ifd1Slot is not null && image.Ifd0.Next is { } chainedIfd1) {
      ctx.PatchIfdNextOffset(ifd1Slot, 0);
      ctx.PatchIfdCount(ifd1Slot, chainedIfd1.Entries.Count);
    }

    // Sub-IFDs (EXIF / GPS / Interop) come after IFD1.
    var subIfdPlacements = new Dictionary<ushort, (int Offset, IfdTableSlot Slot, TiffIfd SubIfd)>();
    foreach (var (pointerTag, subIfd) in image.Ifd0.SubIfds) {
      var offset = ctx.Position;
      var slot = ctx.ReserveIfdTable(subIfd.Entries.Count);
      ctx.PatchIfdNextOffset(slot, 0);
      ctx.PatchIfdCount(slot, subIfd.Entries.Count);
      subIfdPlacements[pointerTag] = (offset, slot, subIfd);
    }

    // Populate IFD0 entries (rewrites sub-IFD pointers on the fly).
    PopulateIfd(ctx, image.Ifd0, ifd0Slot, subIfdPlacements);
    if (ifd1Slot is not null && image.Ifd0.Next is { } ifd1Data)
      PopulateIfd(ctx, ifd1Data, ifd1Slot, emptyMap: true);
    foreach (var (_, info) in subIfdPlacements)
      PopulateIfd(ctx, info.SubIfd, info.Slot, emptyMap: true);

    // Write the TIFF header at offset 0.
    var buffer = ctx.ToArray();
    buffer[0] = image.LittleEndian ? (byte)'I' : (byte)'M';
    buffer[1] = image.LittleEndian ? (byte)'I' : (byte)'M';
    WriteU16(buffer, 2, 0x002A, image.LittleEndian);
    WriteU32(buffer, 4, (uint)ifd0Offset, image.LittleEndian);

    return buffer;
  }

  private static void PopulateIfd(
    WriterContext ctx,
    TiffIfd ifd,
    IfdTableSlot slot,
    Dictionary<ushort, (int Offset, IfdTableSlot Slot, TiffIfd SubIfd)>? subIfdPlacements = null,
    bool emptyMap = false
  ) {
    var map = emptyMap ? null : subIfdPlacements;

    for (var i = 0; i < ifd.Entries.Count; i++) {
      var entry = ifd.Entries[i];
      var entrySlotPos = slot.EntryPosition(i);

      // Rewrite sub-IFD pointer values to the freshly-computed offsets.
      var effectiveValue = entry.ValueBytes;
      if (map != null && map.TryGetValue(entry.Tag, out var subInfo)) {
        effectiveValue = new byte[4];
        WriteU32(effectiveValue, 0, (uint)subInfo.Offset, ctx.LittleEndian);
      }

      ctx.WriteU16At(entrySlotPos,     entry.Tag);
      ctx.WriteU16At(entrySlotPos + 2, (ushort)entry.Type);
      ctx.WriteU32At(entrySlotPos + 4, entry.Count);

      if (effectiveValue.Length <= 4) {
        // Inline: pad to 4 bytes.
        for (var b = 0; b < 4; b++) {
          ctx.WriteByteAt(entrySlotPos + 8 + b,
            b < effectiveValue.Length ? effectiveValue[b] : (byte)0);
        }
      } else {
        // Out-of-line. Pad to an even offset per TIFF convention.
        if (ctx.Position % 2 != 0)
          ctx.WriteByte(0);
        var valueOffset = ctx.Position;
        ctx.WriteBytes(effectiveValue);
        ctx.WriteU32At(entrySlotPos + 8, (uint)valueOffset);
      }
    }
  }

  /// <summary>Placeholder for an IFD table reserved in the buffer.</summary>
  private sealed class IfdTableSlot {
    public int StartPosition { get; init; }
    public int EntryCount { get; init; }

    public int EntryPosition(int index) => this.StartPosition + 2 + index * 12;
    public int NextOffsetPosition => this.StartPosition + 2 + this.EntryCount * 12;
  }

  /// <summary>
  /// Growable byte buffer that tracks its own position and lets callers
  /// patch earlier offsets directly. Replaces the previous List&lt;byte&gt;
  /// trick which returned stale copies from its backing array.
  /// </summary>
  private sealed class WriterContext {
    private byte[] _buffer = new byte[4096];
    private int _position;

    public WriterContext(bool littleEndian) => this.LittleEndian = littleEndian;

    public bool LittleEndian { get; }
    public int Position => this._position;

    public byte[] ToArray() {
      var result = new byte[this._position];
      Array.Copy(this._buffer, result, this._position);
      return result;
    }

    public void Skip(int count) {
      this.EnsureCapacity(this._position + count);
      // Bytes are already zero-initialised because we track position explicitly.
      this._position += count;
    }

    public void WriteByte(byte b) {
      this.EnsureCapacity(this._position + 1);
      this._buffer[this._position++] = b;
    }

    public void WriteBytes(byte[] bytes) {
      this.EnsureCapacity(this._position + bytes.Length);
      Array.Copy(bytes, 0, this._buffer, this._position, bytes.Length);
      this._position += bytes.Length;
    }

    public void WriteByteAt(int offset, byte b) => this._buffer[offset] = b;

    public void WriteU16At(int offset, ushort value) {
      if (this.LittleEndian) BinaryPrimitives.WriteUInt16LittleEndian(this._buffer.AsSpan(offset), value);
      else BinaryPrimitives.WriteUInt16BigEndian(this._buffer.AsSpan(offset), value);
    }

    public void WriteU32At(int offset, uint value) {
      if (this.LittleEndian) BinaryPrimitives.WriteUInt32LittleEndian(this._buffer.AsSpan(offset), value);
      else BinaryPrimitives.WriteUInt32BigEndian(this._buffer.AsSpan(offset), value);
    }

    public IfdTableSlot ReserveIfdTable(int entryCount) {
      var slot = new IfdTableSlot { StartPosition = this._position, EntryCount = entryCount };
      this.Skip(2 + entryCount * 12 + 4);
      return slot;
    }

    public void PatchIfdCount(IfdTableSlot slot, int entryCount)
      => this.WriteU16At(slot.StartPosition, (ushort)entryCount);

    public void PatchIfdNextOffset(IfdTableSlot slot, int nextOffset)
      => this.WriteU32At(slot.NextOffsetPosition, (uint)nextOffset);

    private void EnsureCapacity(int needed) {
      if (this._buffer.Length >= needed)
        return;

      var newSize = this._buffer.Length * 2;
      while (newSize < needed)
        newSize *= 2;

      var newBuffer = new byte[newSize];
      Array.Copy(this._buffer, newBuffer, this._position);
      this._buffer = newBuffer;
    }
  }

  private static void WriteU16(byte[] buffer, int offset, ushort value, bool littleEndian) {
    if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), value);
    else BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), value);
  }

  private static void WriteU32(byte[] buffer, int offset, uint value, bool littleEndian) {
    if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), value);
    else BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), value);
  }
}
