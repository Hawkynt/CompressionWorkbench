#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.TahoeLafs;

/// <summary>The storage-server share-container family carried by a Tahoe-LAFS share file.</summary>
public enum TahoeLafsShareKind {
  Immutable,
  Mutable,
}

/// <summary>
/// Parsed physical layout of one Tahoe-LAFS storage-server share container.
/// The encrypted/erasure-coded share payload remains opaque; only the outer
/// storage container is interpreted here.
/// </summary>
internal readonly record struct TahoeLafsContainerLayout(
  TahoeLafsShareKind Kind,
  uint Version,
  int DataOffset,
  long DataLength,
  long LeaseOffset,
  uint LeaseCount,
  uint ExtraLeaseCount,
  long UsedEnd,
  long FileLength,
  uint? LegacyDataLength) {

  public long DataEnd => this.DataOffset + this.DataLength;

  public long CompactLength => this.Kind == TahoeLafsShareKind.Mutable
    ? this.DataEnd + (this.UsedEnd - this.LeaseOffset)
    : this.FileLength;

  public long FreeSpace => this.Kind == TahoeLafsShareKind.Mutable
    ? Math.Max(0, this.LeaseOffset - this.DataEnd) + Math.Max(0, this.FileLength - this.UsedEnd)
    : 0;
}

/// <summary>
/// Tahoe-LAFS storage-container framing. These are storage-server share files,
/// not the immutable/mutable share payload formats themselves.
///
/// Layout facts are derived independently from the Tahoe-LAFS storage-server
/// implementation and mutable-format documentation. Versions 1 and 2 of the
/// 12-byte container are both immutable-share containers; mutable shares use a
/// separate 32-byte magic and a different header.
/// </summary>
internal static class TahoeLafsContainer {
  internal const int ImmutableHeaderSize = 12;
  internal const int ImmutableLeaseSize = 72;

  internal const int MutableHeaderSize = 100;
  internal const int MutableLeaseSize = 92;
  internal const int MutableFixedLeaseCount = 4;
  internal const int MutableDataOffset = MutableHeaderSize + MutableFixedLeaseCount * MutableLeaseSize; // 468
  internal const int MutableDataLengthOffset = 84;
  internal const int MutableExtraLeaseOffsetOffset = 92;

  internal static ReadOnlySpan<byte> MutableV1Magic => [
    0x54, 0x61, 0x68, 0x6F, 0x65, 0x20, 0x6D, 0x75,
    0x74, 0x61, 0x62, 0x6C, 0x65, 0x20, 0x63, 0x6F,
    0x6E, 0x74, 0x61, 0x69, 0x6E, 0x65, 0x72, 0x20,
    0x76, 0x31, 0x0A, 0x75, 0x09, 0x44, 0x03, 0x8E,
  ];

  internal static ReadOnlySpan<byte> MutableV2Magic => [
    0x54, 0x61, 0x68, 0x6F, 0x65, 0x20, 0x6D, 0x75,
    0x74, 0x61, 0x62, 0x6C, 0x65, 0x20, 0x63, 0x6F,
    0x6E, 0x74, 0x61, 0x69, 0x6E, 0x65, 0x72, 0x20,
    0x76, 0x32, 0x0A, 0xC3, 0x55, 0x21, 0x99, 0x25,
  ];

  internal static TahoeLafsContainerLayout Parse(ReadOnlySpan<byte> data) {
    if (data.Length < 4)
      throw new InvalidDataException("TahoeLafs: file too small for a share-container header.");

    var prefix = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
    if (prefix is 1 or 2)
      return ParseImmutable(data, prefix);

    if (data.Length >= MutableV1Magic.Length && data[..MutableV1Magic.Length].SequenceEqual(MutableV1Magic))
      return ParseMutable(data, 1);

    if (data.Length >= MutableV2Magic.Length && data[..MutableV2Magic.Length].SequenceEqual(MutableV2Magic))
      return ParseMutable(data, 2);

    throw new InvalidDataException("TahoeLafs: unrecognized immutable or mutable share-container header.");
  }

  private static TahoeLafsContainerLayout ParseImmutable(ReadOnlySpan<byte> data, uint version) {
    if (data.Length < ImmutableHeaderSize)
      throw new InvalidDataException("TahoeLafs: immutable share is too small for its 12-byte header.");

    var legacyDataLength = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
    var leaseCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));
    var leaseBytes = (long)leaseCount * ImmutableLeaseSize;
    if (leaseBytes > data.Length - ImmutableHeaderSize)
      throw new InvalidDataException("TahoeLafs: immutable lease count exceeds the share-container length.");

    var leaseOffset = data.Length - leaseBytes;
    var dataLength = leaseOffset - ImmutableHeaderSize;
    return new(
      TahoeLafsShareKind.Immutable,
      version,
      ImmutableHeaderSize,
      dataLength,
      leaseOffset,
      leaseCount,
      0,
      data.Length,
      data.Length,
      legacyDataLength);
  }

  private static TahoeLafsContainerLayout ParseMutable(ReadOnlySpan<byte> data, uint version) {
    if (data.Length < MutableDataOffset + sizeof(uint))
      throw new InvalidDataException("TahoeLafs: mutable share is too small for its fixed header and lease table.");

    var dataLength = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(MutableDataLengthOffset, 8));
    var extraLeaseOffset = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(MutableExtraLeaseOffsetOffset, 8));

    if (dataLength > (ulong)(data.Length - MutableDataOffset))
      throw new InvalidDataException("TahoeLafs: mutable data length exceeds the share-container length.");

    var dataEnd = (ulong)MutableDataOffset + dataLength;
    if (extraLeaseOffset < dataEnd || extraLeaseOffset > (ulong)(data.Length - sizeof(uint)))
      throw new InvalidDataException("TahoeLafs: mutable extra-lease offset overlaps data or lies outside the container.");

    var extraLeaseOffsetInt = checked((int)extraLeaseOffset);
    var extraLeaseCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(extraLeaseOffsetInt, sizeof(uint)));
    var extraLeaseBytes = sizeof(uint) + (ulong)extraLeaseCount * MutableLeaseSize;
    if (extraLeaseBytes > (ulong)data.Length - extraLeaseOffset)
      throw new InvalidDataException("TahoeLafs: mutable extra-lease table exceeds the share-container length.");

    var activeLeases = CountMutableLeases(data, extraLeaseOffsetInt, extraLeaseCount);
    var usedEnd = checked((long)(extraLeaseOffset + extraLeaseBytes));

    return new(
      TahoeLafsShareKind.Mutable,
      version,
      MutableDataOffset,
      checked((long)dataLength),
      checked((long)extraLeaseOffset),
      activeLeases,
      extraLeaseCount,
      usedEnd,
      data.Length,
      null);
  }

  private static uint CountMutableLeases(ReadOnlySpan<byte> data, int extraLeaseOffset, uint extraLeaseCount) {
    uint result = 0;
    for (var i = 0; i < MutableFixedLeaseCount; ++i) {
      var offset = MutableHeaderSize + i * MutableLeaseSize;
      if (BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, sizeof(uint))) != 0)
        ++result;
    }

    var extraOffset = extraLeaseOffset + sizeof(uint);
    for (uint i = 0; i < extraLeaseCount; ++i) {
      var offset = checked(extraOffset + (int)(i * MutableLeaseSize));
      if (BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, sizeof(uint))) != 0)
        ++result;
    }

    return result;
  }
}
