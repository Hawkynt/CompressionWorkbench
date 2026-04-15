using System.Buffers.Binary;
using System.Text;

namespace Compression.Core.DiskImage;

/// <summary>
/// Parses GPT (GUID Partition Table) headers and partition entries.
/// GPT header is at LBA 1 (offset 512 for 512-byte sectors), with partition entries starting at LBA 2+.
/// </summary>
public static class GptParser {

  /// <summary>GPT signature: "EFI PART".</summary>
  private static ReadOnlySpan<byte> Signature => "EFI PART"u8;

  /// <summary>Standard sector size.</summary>
  private const int SectorSize = 512;

  /// <summary>GPT header offset (LBA 1).</summary>
  private const int HeaderOffset = SectorSize;

  /// <summary>Minimum GPT header size.</summary>
  private const int MinHeaderSize = 92;

  /// <summary>Size of each GPT partition entry (minimum).</summary>
  private const int MinEntrySize = 128;

  /// <summary>
  /// Checks whether the given data contains a valid GPT header at LBA 1.
  /// </summary>
  /// <param name="data">At least 1024 bytes of disk image data starting at LBA 0.</param>
  /// <returns><c>true</c> if a valid GPT signature is found at LBA 1.</returns>
  public static bool IsGpt(ReadOnlySpan<byte> data)
    => data.Length >= HeaderOffset + MinHeaderSize
       && data.Slice(HeaderOffset, 8).SequenceEqual(Signature);

  /// <summary>
  /// Parses all partitions from a GPT disk image.
  /// </summary>
  /// <param name="diskData">The full disk image as a readable stream.</param>
  /// <returns>A list of partition entries.</returns>
  public static List<PartitionEntry> Parse(Stream diskData) {
    // Read GPT header at LBA 1.
    var header = new byte[SectorSize];
    diskData.Position = HeaderOffset;
    diskData.ReadExactly(header);

    if (!header.AsSpan(0, 8).SequenceEqual(Signature))
      throw new InvalidDataException("Invalid GPT: missing 'EFI PART' signature at LBA 1.");

    var revision = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
    var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
    var partitionEntryLba = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72));
    var numberOfEntries = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80));
    var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));

    if (entrySize < MinEntrySize)
      throw new InvalidDataException($"GPT entry size {entrySize} is smaller than minimum {MinEntrySize}.");

    // Read all partition entries.
    var totalSize = (long)numberOfEntries * entrySize;
    var entryData = new byte[totalSize];
    diskData.Position = (long)partitionEntryLba * SectorSize;
    diskData.ReadExactly(entryData);

    var result = new List<PartitionEntry>();
    var index = 0;

    for (var i = 0; i < (int)numberOfEntries; i++) {
      var offset = (int)(i * entrySize);
      var entry = entryData.AsSpan(offset, (int)entrySize);

      // Read type GUID (mixed endian: first 3 components LE, last 2 BE).
      var typeGuid = ReadMixedEndianGuid(entry);
      if (typeGuid == Guid.Empty)
        continue; // Unused entry.

      // Read unique GUID.
      var uniqueGuid = ReadMixedEndianGuid(entry[16..]);

      var firstLba = BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]);
      var lastLba = BinaryPrimitives.ReadUInt64LittleEndian(entry[40..]);
      var attributes = BinaryPrimitives.ReadUInt64LittleEndian(entry[48..]);

      // Read UTF-16LE name (up to 36 characters = 72 bytes, starting at offset 56).
      var nameLen = Math.Min(72, (int)entrySize - 56);
      var name = Encoding.Unicode.GetString(entry.Slice(56, nameLen)).TrimEnd('\0');

      var sizeBytes = (long)(lastLba - firstLba + 1) * SectorSize;

      result.Add(new() {
        Index = index++,
        StartOffset = (long)firstLba * SectorSize,
        Size = sizeBytes,
        TypeName = PartitionTypeDatabase.GetGptTypeName(typeGuid),
        IsActive = (attributes & 0x04) != 0, // Legacy BIOS bootable attribute.
        TypeCode = typeGuid.ToString("D").ToUpperInvariant(),
        Name = name,
        Source = "GPT"
      });
    }

    return result;
  }

  /// <summary>
  /// Reads a GUID in GPT mixed-endian format: first 3 components are little-endian,
  /// last 2 are big-endian (stored as-is).
  /// </summary>
  private static Guid ReadMixedEndianGuid(ReadOnlySpan<byte> data) {
    var a = BinaryPrimitives.ReadInt32LittleEndian(data);
    var b = BinaryPrimitives.ReadInt16LittleEndian(data[4..]);
    var c = BinaryPrimitives.ReadInt16LittleEndian(data[6..]);
    return new Guid(a, b, c, data[8], data[9], data[10], data[11], data[12], data[13], data[14], data[15]);
  }
}
