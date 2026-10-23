using System.Buffers.Binary;

namespace FileSystem.Zfs;

/// <summary>One data virtual address: a top-level vdev, an offset after its 4 MiB start, a gang flag.</summary>
internal readonly record struct ZfsDva(uint Vdev, long Offset, uint AllocatedSectors, bool Gang, bool IsEmpty) {
  public static ZfsDva Read(ReadOnlySpan<byte> src) {
    var w0 = BinaryPrimitives.ReadUInt64LittleEndian(src);
    var w1 = BinaryPrimitives.ReadUInt64LittleEndian(src[8..]);
    return new((uint)(w0 >> 32), (long)(w1 & 0x7FFFFFFFFFFFFFFFUL) << ZfsConstants.SectorShift,
      (uint)(w0 & 0xFFFFFF), (w1 >> 63) != 0, w0 == 0 && w1 == 0);
  }
}

/// <summary>
/// A parsed <c>blkptr_t</c> (layout in <c>docs/ZFS-ON-DISK.md</c>), keeping the raw 128
/// bytes for embedded payloads and gang children.
/// </summary>
internal sealed class ZfsBlockPointer {
  private readonly byte[] _raw;

  private ZfsBlockPointer(byte[] raw) {
    this._raw = raw;
    var prop = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(0x30));
    this.IsEmbedded = ((prop >> 39) & 1) != 0;
    this.Compression = (byte)((prop >> 32) & 0x7F);
    this.Type = (byte)((prop >> 48) & 0xFF);
    this.Level = (int)((prop >> 56) & 0x1F);
    this.IsLittleEndian = (prop >> 63) != 0;
    this.LogicalBirth = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(0x50));
    if (this.IsEmbedded) {
      this.LogicalSize = (int)(prop & 0x1FFFFFF) + 1;
      this.PhysicalSize = (int)((prop >> 25) & 0x7F) + 1;
      this.EmbeddedType = (byte)((prop >> 40) & 0xFF);
      this.Dvas = [];
      return;
    }

    this.LogicalSize = (int)((prop & 0xFFFF) + 1) << ZfsConstants.SectorShift;
    this.PhysicalSize = (int)(((prop >> 16) & 0xFFFF) + 1) << ZfsConstants.SectorShift;
    this.Checksum = (byte)((prop >> 40) & 0xFF);
    this.IsEncrypted = ((prop >> 61) & 1) != 0;
    this.Dvas = [ZfsDva.Read(raw.AsSpan(0x00)), ZfsDva.Read(raw.AsSpan(0x10)), ZfsDva.Read(raw.AsSpan(0x20))];
    this.Fill = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(0x58));
  }

  public static ZfsBlockPointer Read(ReadOnlySpan<byte> src) {
    if (src.Length < ZfsConstants.BlockPointerSize)
      throw new InvalidDataException("ZFS: block pointer truncated.");
    return new(src[..ZfsConstants.BlockPointerSize].ToArray());
  }

  public bool IsEmbedded { get; }
  public byte EmbeddedType { get; }
  public int LogicalSize { get; }
  public int PhysicalSize { get; }
  public byte Compression { get; }
  public byte Checksum { get; }
  public byte Type { get; }
  public int Level { get; }
  public bool IsEncrypted { get; }
  public bool IsLittleEndian { get; }
  public ulong LogicalBirth { get; }
  public ulong Fill { get; }
  public ZfsDva[] Dvas { get; }

  /// <summary>A pointer that is not embedded and whose first DVA is empty names no block: it reads as zeros.</summary>
  public bool IsHole => !this.IsEmbedded && this.Dvas[0].IsEmpty;

  public ReadOnlySpan<byte> StoredChecksum => this._raw.AsSpan(0x60, 32);

  /// <summary>
  /// The payload of an embedded pointer: words 0-5, 7-9 and 11-15, skipping the
  /// properties word and the logical birth.
  /// </summary>
  public byte[] EmbeddedPayload() {
    var payload = new byte[this.PhysicalSize];
    var raw = this._raw.AsSpan();
    var taken = 0;
    foreach (var (from, length) in (ReadOnlySpan<(int, int)>)[(0x00, 48), (0x38, 24), (0x58, 40)]) {
      var n = Math.Min(length, payload.Length - taken);
      if (n <= 0) break;
      raw.Slice(from, n).CopyTo(payload.AsSpan(taken));
      taken += n;
    }
    if (taken < payload.Length)
      throw new InvalidDataException("ZFS: embedded block pointer declares more than 112 payload bytes.");
    return payload;
  }
}
