#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Gpfs;

/// <summary>
/// Structural detector for IBM Storage Scale / GPFS NSD v2 disks.
/// NSD v2 uses a GPT with a single GPFS partition; unlike a fixed offset-zero
/// magic this remains valid even when the GPT entry array is relocated.
/// </summary>
public sealed class GpfsDetectionSource : IFormatDetectionSource {
  private const int SectorSize = 512;
  private const int GptHeaderOffset = SectorSize;
  private const int MinimumGptHeaderSize = 92;
  private const int MinimumGptEntrySize = 128;
  private static ReadOnlySpan<byte> GptSignature => "EFI PART"u8;

  /// <inheritdoc />
  public IEnumerable<FormatDetectionSignature> Signatures => Array.Empty<FormatDetectionSignature>();

  /// <inheritdoc />
  public int HeaderProbeLength => 34 * SectorSize;

  /// <inheritdoc />
  public FormatHeaderMatch? DetectHeader(ReadOnlySpan<byte> header)
    => TryFindGpfsPartitionType(header)
      ? new FormatHeaderMatch("Gpfs", "IBM Storage Scale / GPFS", FormatCategory.Archive, ".gpfs", 0.99)
      : null;

  internal static bool TryFindGpfsPartitionType(ReadOnlySpan<byte> header) {
    if (header.Length < GptHeaderOffset + MinimumGptHeaderSize)
      return false;

    var gpt = header[GptHeaderOffset..];
    if (!gpt[..8].SequenceEqual(GptSignature))
      return false;

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

    var availableEntries = (header.Length - entriesOffset) / (int)entrySize;
    var entriesToInspect = Math.Min((long)entryCount, availableEntries);
    for (var i = 0L; i < entriesToInspect; ++i) {
      var offset = checked(entriesOffset + (int)(i * entrySize));
      if (header.Slice(offset, 16).SequenceEqual(GpfsReader.GpfsPartitionTypeGuidBytes))
        return true;
    }

    return false;
  }
}
