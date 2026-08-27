#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FileSystem.Refs;

/// <summary>
/// ReFS metadata checksum primitives.  Page-reference checksums cover the whole
/// referenced metadata page.  SUPB/CHKP self descriptors instead cover exactly
/// one allocation cluster with the self descriptor zeroed.
/// </summary>
internal static class RefsChecksum {
  private const ulong Crc64Polynomial = 0x9A6C9329AC4BC9B5UL;
  private const ulong Crc64SentinelInput = 0xABBAFFFFABBAFFFEUL;
  private const ulong Crc64SentinelStored = 0xABBAFFFFABBAFFFFUL;
  private const uint Crc32CPolynomial = 0x82F63B78U;

  public static uint Crc32C(ReadOnlySpan<byte> data) {
    uint crc = uint.MaxValue;
    foreach (var value in data) {
      crc ^= value;
      for (var bit = 0; bit < 8; ++bit)
        crc = (crc >> 1) ^ ((crc & 1) != 0 ? Crc32CPolynomial : 0U);
    }
    return ~crc;
  }

  public static ulong Crc64(ReadOnlySpan<byte> data) {
    ulong crc = ulong.MaxValue;
    foreach (var value in data) {
      crc ^= value;
      for (var bit = 0; bit < 8; ++bit)
        crc = (crc >> 1) ^ ((crc & 1) != 0 ? Crc64Polynomial : 0UL);
    }
    crc = ~crc;
    return crc == Crc64SentinelInput ? Crc64SentinelStored : crc;
  }

  /// <summary>Refreshes the digest embedded in a normal page reference.</summary>
  public static void RefreshPageReference(Span<byte> reference, ReadOnlySpan<byte> referencedPage) {
    if (reference.Length < 0x28) throw new InvalidDataException("ReFS page reference is too short.");
    var type = reference[0x22];
    var digestLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(reference[0x24..0x28]));
    if (type == 0 || digestLength == 0) return;
    if (0x28 + digestLength > reference.Length)
      throw new InvalidDataException("ReFS page-reference checksum lies outside the descriptor.");

    var destination = reference.Slice(0x28, digestLength);
    switch (type) {
      case 1 when digestLength >= 4:
        destination.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Crc32C(referencedPage));
        break;
      case 2 when digestLength >= 8:
        destination.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(destination, Crc64(referencedPage));
        break;
      case 4 when digestLength >= 32:
        destination.Clear();
        SHA256.HashData(referencedPage, destination[..32]);
        break;
      default:
        throw new NotSupportedException($"Unsupported ReFS metadata checksum type {type} / length {digestLength}.");
    }
  }

  /// <summary>
  /// Refreshes a SUPB/CHKP self checksum.  The descriptor itself is zero while
  /// hashing, matching ComputeOrVerifySelfChecksumBlock in refs.sys.
  /// </summary>
  public static void RefreshSelfChecksum(Span<byte> page, int clusterSize, int descriptorOffset, int descriptorLength) {
    if (clusterSize <= 0 || page.Length < clusterSize)
      throw new InvalidDataException("ReFS self-checksum block is shorter than one cluster.");
    if (descriptorOffset < 0 || descriptorLength < 0 || descriptorOffset + descriptorLength > clusterSize)
      throw new InvalidDataException("ReFS self-checksum descriptor lies outside its checksum cluster.");
    if (descriptorLength < 0x28) throw new InvalidDataException("ReFS self-checksum descriptor is too short.");

    var descriptor = page.Slice(descriptorOffset, descriptorLength);
    var type = descriptor[0x22];
    var digestLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor[0x24..0x28]));
    if (type == 0 || digestLength == 0) return;
    if (0x28 + digestLength > descriptor.Length)
      throw new InvalidDataException("ReFS self-checksum digest lies outside its descriptor.");

    var scratch = page[..clusterSize].ToArray();
    scratch.AsSpan(descriptorOffset, descriptorLength).Clear();
    var destination = descriptor.Slice(0x28, digestLength);
    switch (type) {
      case 1 when digestLength >= 4:
        destination.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Crc32C(scratch));
        break;
      case 2 when digestLength >= 8:
        destination.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(destination, Crc64(scratch));
        break;
      case 4 when digestLength >= 32:
        destination.Clear();
        SHA256.HashData(scratch, destination[..32]);
        break;
      default:
        throw new NotSupportedException($"Unsupported ReFS self-checksum type {type} / length {digestLength}.");
    }
  }
}
