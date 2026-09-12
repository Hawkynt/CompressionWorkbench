#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

/// <summary>
/// Verifies and updates the inline ReFS integrity-stream representation used on
/// 4 KiB cluster volumes. A checksummed data cluster is represented by a
/// one-cluster extent with flags 0x1c00d0 followed immediately by an eight-byte
/// element containing CRC32-C and a reserved dword.
/// </summary>
internal static class RefsIntegrityDataVerifier {
  internal const uint InlineIntegrityExtentFlags = 0x001C00D0;
  internal const int InlineIntegrityElementSize = 8;

  public static bool RequiresInlineVerification(RefsDataExtent extent)
    => extent.Flags == InlineIntegrityExtentFlags;

  public static uint ComputeInlineChecksum(
      RefsDataExtent extent,
      ReadOnlySpan<byte> cluster,
      int clusterSize) {
    ValidateInlineGeometry(extent, cluster.Length, clusterSize);
    return RefsChecksum.Crc32C(cluster);
  }

  /// <summary>
  /// Returns a copy of the owning $DATA value with the inline integrity element
  /// refreshed for the supplied cluster. The reserved dword is always zeroed;
  /// mutation never carries forward unknown bits into a newly generated entry.
  /// </summary>
  public static byte[] BuildUpdatedOwningValue(
      ReadOnlySpan<byte> owningRowValue,
      RefsDataExtent extent,
      ReadOnlySpan<byte> cluster,
      int clusterSize) {
    var result = owningRowValue.ToArray();
    StampInlineChecksum(result, extent, cluster, clusterSize);
    return result;
  }

  public static void StampInlineChecksum(
      Span<byte> owningRowValue,
      RefsDataExtent extent,
      ReadOnlySpan<byte> cluster,
      int clusterSize) {
    ValidateInlineGeometry(extent, cluster.Length, clusterSize);
    var checksumOffset = GetInlineChecksumOffset(extent, owningRowValue.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(
      owningRowValue.Slice(checksumOffset, 4),
      RefsChecksum.Crc32C(cluster));
    owningRowValue.Slice(checksumOffset + 4, 4).Clear();
  }

  public static void VerifyCluster(
      RefsMetadataReader metadata,
      RefsFileRecord file,
      RefsDataExtent extent,
      ReadOnlySpan<byte> cluster) {
    ArgumentNullException.ThrowIfNull(metadata);
    ArgumentNullException.ThrowIfNull(file);

    if (!RequiresInlineVerification(extent)) return;
    ValidateInlineGeometry(extent, cluster.Length, metadata.ClusterSize, file.Path);

    var rowValue = ResolveOwningRowValue(metadata, file);
    var checksumOffset = GetInlineChecksumOffset(extent, rowValue.Length, file.Path);
    var stored = BinaryPrimitives.ReadUInt32LittleEndian(rowValue.AsSpan(checksumOffset, 4));
    var reserved = BinaryPrimitives.ReadUInt32LittleEndian(rowValue.AsSpan(checksumOffset + 4, 4));
    if (reserved != 0)
      throw new InvalidDataException(
        $"ReFS inline integrity checksum element for '{file.Path}' has non-zero reserved bits 0x{reserved:X8}.");

    var computed = RefsChecksum.Crc32C(cluster);
    if (computed != stored)
      throw new InvalidDataException(
        $"ReFS integrity checksum mismatch for '{file.Path}' at file VCN 0x{extent.FileVcn:X}: " +
        $"stored 0x{stored:X8}, computed 0x{computed:X8}.");
  }

  internal static int GetInlineChecksumOffset(
      RefsDataExtent extent,
      int owningValueLength,
      string? path = null) {
    if (!RequiresInlineVerification(extent))
      throw new ArgumentException("ReFS extent is not an inline integrity extent.", nameof(extent));
    if (owningValueLength < 0) throw new ArgumentOutOfRangeException(nameof(owningValueLength));
    int checksumOffset;
    try { checksumOffset = checked(extent.ValueRelativeOffset + 24); }
    catch (OverflowException e) {
      throw new InvalidDataException(OutsideMessage(path), e);
    }
    if (extent.ValueRelativeOffset < 0
        || checksumOffset < 0
        || checksumOffset > owningValueLength - InlineIntegrityElementSize)
      throw new InvalidDataException(OutsideMessage(path));
    return checksumOffset;
  }

  private static void ValidateInlineGeometry(
      RefsDataExtent extent,
      int clusterLength,
      int clusterSize,
      string? path = null) {
    if (!RequiresInlineVerification(extent))
      throw new ArgumentException("ReFS extent is not an inline integrity extent.", nameof(extent));
    if (clusterSize != 4096)
      throw new NotSupportedException(
        $"ReFS inline integrity extent{PathSuffix(path)} appears on a {clusterSize:N0}-byte cluster volume; " +
        "the decoded 0x1C00D0 representation is defined for 4 KiB CRC32-C clusters only.");
    if (extent.ClusterCount != 1)
      throw new InvalidDataException(
        $"ReFS inline integrity extent{PathSuffix(path)} covers {extent.ClusterCount:N0} clusters; expected exactly one.");
    if (clusterLength != clusterSize)
      throw new ArgumentException("ReFS integrity processing requires the complete allocation cluster.", nameof(clusterLength));
  }

  private static string OutsideMessage(string? path)
    => $"ReFS inline integrity checksum{PathSuffix(path)} lies outside its owning $DATA value.";

  private static string PathSuffix(string? path)
    => string.IsNullOrEmpty(path) ? string.Empty : $" for '{path}'";

  private static byte[] ResolveOwningRowValue(RefsMetadataReader metadata, RefsFileRecord file) {
    if (file.Backing != null) return file.Backing.Row.Value;
    return new RefsWritableNamespace(metadata).FindDirectoryEntry(file.Path).Value;
  }
}
