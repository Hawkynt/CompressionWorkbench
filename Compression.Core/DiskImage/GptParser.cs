using System.Buffers.Binary;
using System.Text;

namespace Compression.Core.DiskImage;

/// <summary>
/// Parses GPT (GUID Partition Table) headers and partition entries.
/// </summary>
/// <remarks>
/// <para>
/// The header lives at LBA 1, so its byte offset depends on the disk's logical block size:
/// 512 on a classic or 512e drive, 4096 on a 4Kn drive. Neither the protective MBR nor the
/// header records the block size, so it is found the way the UEFI specification implies: the
/// block size is the one for which LBA 1 holds a header whose CRC32 matches and that names
/// itself as LBA 1 (UEFI 2.10, §5.3.2). Every LBA in the header and the entry array is then
/// counted in that block size.
/// </para>
/// <para>
/// When the primary header or its entry array fails its CRC, the backup header at the last
/// LBA is used instead, as §5.3.2 directs.
/// </para>
/// </remarks>
public static class GptParser {

  /// <summary>GPT signature: "EFI PART".</summary>
  private static ReadOnlySpan<byte> Signature => "EFI PART"u8;

  /// <summary>Logical block sizes probed for the header at LBA 1, in probe order.</summary>
  public static IReadOnlyList<int> SupportedSectorSizes { get; } = [512, 4096];

  /// <summary>Classic sector size, used when nothing else is known.</summary>
  private const int DefaultSectorSize = 512;

  /// <summary>Bytes of the image needed to see LBA 1 at every supported block size.</summary>
  public const int ProbeBytes = 4096 * 2;

  /// <summary>Minimum GPT header size.</summary>
  private const int MinHeaderSize = 92;

  /// <summary>Size of each GPT partition entry (minimum).</summary>
  private const int MinEntrySize = 128;

  /// <summary>Upper bound on the entry array the parser will read (UEFI allows any count; real tables hold 128).</summary>
  private const long MaxEntryArrayBytes = 16L * 1024 * 1024;

  /// <summary>Layout facts read from the header that was used to parse the table.</summary>
  /// <param name="SectorSize">Logical block size every LBA is counted in.</param>
  /// <param name="MyLba">The LBA the used header sits at (1 for the primary).</param>
  /// <param name="AlternateLba">The LBA of the other header.</param>
  /// <param name="FirstUsableLba">First LBA a partition may occupy.</param>
  /// <param name="LastUsableLba">Last LBA a partition may occupy.</param>
  /// <param name="DiskGuid">The disk GUID.</param>
  /// <param name="EntriesLba">Starting LBA of the entry array the used header points at.</param>
  /// <param name="EntryCount">Number of entries in the array.</param>
  /// <param name="EntrySize">Size of one entry in bytes.</param>
  /// <param name="FromBackup"><c>true</c> when the primary was unusable and the backup was read.</param>
  public sealed record HeaderInfo(
    int SectorSize, ulong MyLba, ulong AlternateLba, ulong FirstUsableLba, ulong LastUsableLba,
    Guid DiskGuid, ulong EntriesLba, uint EntryCount, uint EntrySize, bool FromBackup);

  /// <summary>
  /// Checks whether LBA 1 carries the "EFI PART" signature at any supported block size.
  /// </summary>
  /// <param name="data">The start of the image (at least 1024 bytes for 512-byte blocks, 8 KiB for 4 KiB blocks).</param>
  public static bool IsGpt(ReadOnlySpan<byte> data) {
    foreach (var size in SupportedSectorSizes)
      if (data.Length >= size + MinHeaderSize && data.Slice(size, 8).SequenceEqual(Signature))
        return true;
    return false;
  }

  /// <summary>
  /// Checks that LBA 1 holds a GPT header that is internally consistent, not merely the
  /// "EFI PART" signature, at any supported block size: see <see cref="HasValidHeader(ReadOnlySpan{byte}, int)"/>.
  /// </summary>
  /// <param name="data">The start of the image.</param>
  public static bool HasValidHeader(ReadOnlySpan<byte> data) => DetectSectorSize(data) > 0;

  /// <summary>
  /// The logical block size for which LBA 1 holds a valid GPT header, or 0 when none does.
  /// </summary>
  /// <param name="data">The start of the image; 8 KiB covers every supported block size.</param>
  public static int DetectSectorSize(ReadOnlySpan<byte> data) {
    foreach (var size in SupportedSectorSizes)
      if (HasValidHeader(data, size))
        return size;
    return 0;
  }

  /// <summary>
  /// Checks that LBA 1, counted in <paramref name="sectorSize"/>-byte blocks, holds a GPT header
  /// that is internally consistent: the header size lies in [92, block size], the header's own
  /// CRC32 matches (computed with the CRC field zeroed, UEFI 2.10 §5.3.2), it names itself as
  /// LBA 1 and its partition entries are 128 x 2^n bytes each, as the specification requires.
  /// </summary>
  public static bool HasValidHeader(ReadOnlySpan<byte> data, int sectorSize) {
    if (data.Length < sectorSize + MinHeaderSize) return false;
    var header = data.Slice(sectorSize, Math.Min(sectorSize, data.Length - sectorSize));
    return IsValidHeaderBlock(header, sectorSize, expectedMyLba: 1);
  }

  /// <summary>
  /// The partition table's layout facts, or <c>null</c> when the image carries no usable GPT.
  /// Uses the same header <see cref="Parse(Stream)"/> would.
  /// </summary>
  public static HeaderInfo? ReadHeader(Stream diskData) {
    try {
      return Locate(diskData, lenient: true)?.Info;
    } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) {
      return null;
    }
  }

  /// <summary>
  /// Parses all partitions from a GPT disk image, at whichever block size its header validates.
  /// </summary>
  /// <param name="diskData">The full disk image as a readable stream.</param>
  /// <returns>A list of partition entries.</returns>
  public static List<PartitionEntry> Parse(Stream diskData) => Parse(diskData, out _);

  /// <summary>
  /// Parses all partitions and reports the header that described them.
  /// </summary>
  /// <exception cref="InvalidDataException">No GPT header at LBA 1 at any supported block size.</exception>
  public static List<PartitionEntry> Parse(Stream diskData, out HeaderInfo info) {
    var located = Locate(diskData, lenient: true)
      ?? throw new InvalidDataException("Invalid GPT: missing 'EFI PART' signature at LBA 1.");
    info = located.Info;
    return ParseEntries(located.Entries, info);
  }

  private sealed record Located(HeaderInfo Info, byte[] Entries);

  /// <summary>
  /// Finds the header to trust: a primary whose header and entry-array CRCs both match; else
  /// the backup it (or the end of the disk) points at, with the same checks; else, when
  /// <paramref name="lenient"/>, a primary that carries only the signature — the reading older
  /// images written without CRCs rely on.
  /// </summary>
  private static Located? Locate(Stream disk, bool lenient) {
    var head = ReadAt(disk, 0, (int)Math.Min(ProbeBytes, disk.Length));
    foreach (var size in SupportedSectorSizes) {
      if (!HasValidHeader(head, size)) continue;
      var header = head.AsSpan(size, Math.Min(size, head.Length - size));
      if (TryReadEntries(disk, header, size, out var entries))
        return new Located(ToInfo(header, size, fromBackup: false), entries);

      // Entry array damaged: the backup describes the same table.
      if (TryLocateBackup(disk, size, BinaryPrimitives.ReadUInt64LittleEndian(header[32..])) is { } backup)
        return backup;
    }

    // No primary validates: look for a backup at the end of the disk.
    foreach (var size in SupportedSectorSizes) {
      if (!IsGpt(head) && !HasProtectiveEntry(head)) break;
      if (disk.Length < 2L * size) continue;
      if (TryLocateBackup(disk, size, (ulong)(disk.Length / size - 1)) is { } backup)
        return backup;
    }

    if (!lenient) return null;
    foreach (var size in SupportedSectorSizes) {
      if (head.Length < size + MinHeaderSize || !head.AsSpan(size, 8).SequenceEqual(Signature)) continue;
      var header = head.AsSpan(size, Math.Min(size, head.Length - size));
      var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header[84..]);
      if (entrySize < MinEntrySize)
        throw new InvalidDataException($"GPT entry size {entrySize} is smaller than minimum {MinEntrySize}.");
      if (!TryReadEntryBytes(disk, header, size, out var entries))
        throw new InvalidDataException("GPT entry array lies outside the image.");
      return new Located(ToInfo(header, size, fromBackup: false), entries);
    }
    return null;
  }

  private static Located? TryLocateBackup(Stream disk, int sectorSize, ulong backupLba) {
    var offset = (long)backupLba * sectorSize;
    if (backupLba < 2 || offset + sectorSize > disk.Length) return null;
    var block = ReadAt(disk, offset, sectorSize);
    if (block.Length < sectorSize || !IsValidHeaderBlock(block, sectorSize, backupLba)) return null;
    return TryReadEntries(disk, block, sectorSize, out var entries)
      ? new Located(ToInfo(block, sectorSize, fromBackup: true), entries)
      : null;
  }

  private static bool HasProtectiveEntry(ReadOnlySpan<byte> head) {
    if (head.Length < 512 || head[510] != 0x55 || head[511] != 0xAA) return false;
    for (var i = 0; i < 4; ++i)
      if (head[0x1BE + i * 16 + 4] == 0xEE) return true;
    return false;
  }

  private static bool IsValidHeaderBlock(ReadOnlySpan<byte> header, int sectorSize, ulong expectedMyLba) {
    if (header.Length < MinHeaderSize || !header[..8].SequenceEqual(Signature)) return false;
    var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
    if (headerSize < MinHeaderSize || headerSize > sectorSize || headerSize > header.Length) return false;

    Span<byte> copy = stackalloc byte[(int)headerSize];
    header[..(int)headerSize].CopyTo(copy);
    var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(copy[16..]);
    copy.Slice(16, 4).Clear();
    if (Compression.Core.Checksums.Crc32.Compute(copy) != storedCrc) return false;

    var myLba = BinaryPrimitives.ReadUInt64LittleEndian(header[24..]);
    var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header[84..]);
    var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
    return myLba == expectedMyLba
           && entrySize >= MinEntrySize && (entrySize & (entrySize - 1)) == 0
           && entryCount is > 0 and <= 65536;
  }

  private static bool TryReadEntries(Stream disk, ReadOnlySpan<byte> header, int sectorSize, out byte[] entries) {
    if (!TryReadEntryBytes(disk, header, sectorSize, out entries)) return false;
    var expected = BinaryPrimitives.ReadUInt32LittleEndian(header[88..]);
    return Compression.Core.Checksums.Crc32.Compute(entries) == expected;
  }

  private static bool TryReadEntryBytes(Stream disk, ReadOnlySpan<byte> header, int sectorSize, out byte[] entries) {
    entries = [];
    var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header[72..]);
    var count = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(header[84..]);
    var total = (long)count * size;
    if (total <= 0 || total > MaxEntryArrayBytes) return false;
    if (entriesLba > (ulong)(long.MaxValue / sectorSize)) return false;
    var offset = (long)entriesLba * sectorSize;
    if (offset < 0 || offset + total > disk.Length) return false;
    entries = ReadAt(disk, offset, (int)total);
    return entries.Length == total;
  }

  private static HeaderInfo ToInfo(ReadOnlySpan<byte> header, int sectorSize, bool fromBackup) => new(
    sectorSize,
    BinaryPrimitives.ReadUInt64LittleEndian(header[24..]),
    BinaryPrimitives.ReadUInt64LittleEndian(header[32..]),
    BinaryPrimitives.ReadUInt64LittleEndian(header[40..]),
    BinaryPrimitives.ReadUInt64LittleEndian(header[48..]),
    ReadMixedEndianGuid(header[56..]),
    BinaryPrimitives.ReadUInt64LittleEndian(header[72..]),
    BinaryPrimitives.ReadUInt32LittleEndian(header[80..]),
    BinaryPrimitives.ReadUInt32LittleEndian(header[84..]),
    fromBackup);

  private static List<PartitionEntry> ParseEntries(byte[] entryData, HeaderInfo info) {
    var result = new List<PartitionEntry>();
    var index = 0;
    var entrySize = (int)info.EntrySize;
    var sectorSize = info.SectorSize;

    for (var i = 0; i < (int)info.EntryCount; i++) {
      var entry = entryData.AsSpan(i * entrySize, entrySize);

      // Read type GUID (mixed endian: first 3 components LE, last 2 BE).
      var typeGuid = ReadMixedEndianGuid(entry);
      if (typeGuid == Guid.Empty)
        continue; // Unused entry.

      var firstLba = BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]);
      var lastLba = BinaryPrimitives.ReadUInt64LittleEndian(entry[40..]);
      var attributes = BinaryPrimitives.ReadUInt64LittleEndian(entry[48..]);
      if (lastLba < firstLba || firstLba > (ulong)(long.MaxValue / sectorSize) || lastLba > (ulong)(long.MaxValue / sectorSize))
        continue; // An inverted or impossible range describes no data.

      // Read UTF-16LE name (up to 36 characters = 72 bytes, starting at offset 56).
      var nameLen = Math.Min(72, entrySize - 56);
      var name = Encoding.Unicode.GetString(entry.Slice(56, nameLen)).TrimEnd('\0');

      result.Add(new() {
        Index = index++,
        StartOffset = (long)firstLba * sectorSize,
        Size = (long)(lastLba - firstLba + 1) * sectorSize,
        TypeName = PartitionTypeDatabase.GetGptTypeName(typeGuid),
        IsActive = (attributes & 0x04) != 0, // Legacy BIOS bootable attribute.
        TypeCode = typeGuid.ToString("D").ToUpperInvariant(),
        Name = name,
        Source = "GPT"
      });
    }

    return result;
  }

  private static byte[] ReadAt(Stream disk, long offset, int count) {
    if (count <= 0) return [];
    var buffer = new byte[count];
    disk.Position = offset;
    var read = disk.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
    return read == count ? buffer : buffer[..read];
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
