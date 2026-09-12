#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Gpfs;

/// <summary>
/// Structural detector for IBM Storage Scale / GPFS NSD v2 disks.
/// NSD v2 uses a GPT with a single GPFS partition; unlike an offset-zero magic,
/// the partition type remains a stable discriminator within the GPT envelope.
/// </summary>
public sealed class GpfsDetectionSource : IFormatDetectionSource {
  private const int SectorSize = 512;
  private const int GptHeaderOffset = SectorSize;
  private const int MinimumGptHeaderSize = 92;
  private const int MinimumGptEntrySize = 128;
  internal const int ProbeLength = 34 * SectorSize;
  private static ReadOnlySpan<byte> GptSignature => "EFI PART"u8;

  /// <inheritdoc />
  public IEnumerable<FormatDetectionSignature> Signatures => Array.Empty<FormatDetectionSignature>();

  /// <inheritdoc />
  public int HeaderProbeLength => ProbeLength;

  /// <inheritdoc />
  public FormatHeaderMatch? DetectHeader(ReadOnlySpan<byte> header)
    => TryFindGpfsPartitionType(header)
      ? new FormatHeaderMatch("Gpfs", "IBM Storage Scale / GPFS", FormatCategory.Archive, ".gpfs", 0.99)
      : null;

  internal static bool HasGptHeader(ReadOnlySpan<byte> header)
    => header.Length >= GptHeaderOffset + MinimumGptHeaderSize
       && header.Slice(GptHeaderOffset, GptSignature.Length).SequenceEqual(GptSignature);

  internal static bool TryFindGpfsPartitionType(ReadOnlySpan<byte> header)
    => TryReadGpfsPartition(header, requireCompleteEntryTable: false, out _, out _, out _);

  internal static bool TryReadGpfsPartition(
      ReadOnlySpan<byte> header,
      bool requireCompleteEntryTable,
      out long partitionOffset,
      out long partitionSize,
      out string partitionName) {
    partitionOffset = 0;
    partitionSize = 0;
    partitionName = string.Empty;

    if (!HasGptHeader(header))
      return false;

    var gpt = header[GptHeaderOffset..];
    var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(gpt[12..]);
    if (headerSize is < MinimumGptHeaderSize or > SectorSize)
      return false;

    var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(gpt[72..]);
    var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(gpt[80..]);
    var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(gpt[84..]);
    if (entryCount == 0 || entrySize < MinimumGptEntrySize || (entrySize & 7) != 0 || entrySize > int.MaxValue)
      return false;
    if (entriesLba > (ulong)int.MaxValue / SectorSize)
      return false;

    var entriesOffset = checked((int)(entriesLba * SectorSize));
    if (entriesOffset < 0 || entriesOffset > header.Length - 16)
      return false;

    var entrySizeInt = (int)entrySize;
    var availableEntries = (header.Length - entriesOffset) / entrySizeInt;
    if (requireCompleteEntryTable && (ulong)availableEntries < entryCount)
      return false;

    var entriesToInspect = Math.Min((long)entryCount, availableEntries);
    for (var i = 0L; i < entriesToInspect; ++i) {
      var offset = checked(entriesOffset + (int)(i * entrySize));
      var entry = header.Slice(offset, entrySizeInt);
      if (!entry[..16].SequenceEqual(GpfsReader.GpfsPartitionTypeGuidBytes))
        continue;

      var firstLba = BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]);
      var lastLba = BinaryPrimitives.ReadUInt64LittleEndian(entry[40..]);
      if (lastLba < firstLba)
        return false;

      ulong sectorCount;
      try {
        sectorCount = checked(lastLba - firstLba + 1);
      } catch (OverflowException) {
        return false;
      }

      if (firstLba > (ulong)long.MaxValue / SectorSize || sectorCount > (ulong)long.MaxValue / SectorSize)
        return false;

      partitionOffset = checked((long)firstLba * SectorSize);
      partitionSize = checked((long)sectorCount * SectorSize);
      var nameBytes = Math.Min(72, entrySizeInt - 56);
      partitionName = Encoding.Unicode.GetString(entry.Slice(56, nameBytes)).TrimEnd('\0');
      return true;
    }

    return false;
  }
}
