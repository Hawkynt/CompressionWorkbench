#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using FileFormat.Zlib;

namespace FileFormat.Ewf;

/// <summary>
/// Reader for EnCase Expert Witness Format (EWF) forensic images — the
/// .e01/.ewf/.l01 family used by EnCase and libewf. Besides the raw section
/// chain it reconstructs the logical media carried by the common single-segment
/// EVF layout emitted by <see cref="EwfWriter"/>.
/// </summary>
public sealed class EwfReader {

  public static readonly byte[] EvfSignature = [0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00];
  public static readonly byte[] LvfSignature = [0x4C, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00];

  public const int FileHeaderSize = 13;
  public const int SectionDescriptorSize = 76;

  public sealed record Section(
    string Type,
    long DescriptorOffset,
    ulong NextSectionOffset,
    ulong SectionSize,
    uint Checksum,
    byte[] Payload);

  public sealed record EwfImage(
    bool IsLogical,
    ushort SegmentNumber,
    List<Section> Sections,
    long TotalFileSize);

  /// <summary>Reads one EWF segment and its section chain.</summary>
  public static EwfImage Read(ReadOnlySpan<byte> data) {
    if (data.Length < FileHeaderSize)
      throw new InvalidDataException("EWF: file shorter than 13-byte header.");

    var isLogical = data[..8].SequenceEqual(LvfSignature);
    if (!isLogical && !data[..8].SequenceEqual(EvfSignature))
      throw new InvalidDataException("EWF: invalid file signature (expected EVF or LVF magic at offset 0).");

    if (data[8] != 0x01)
      throw new InvalidDataException($"EWF: unexpected fields_start byte 0x{data[8]:X2} (expected 0x01).");

    var segment = BinaryPrimitives.ReadUInt16LittleEndian(data[9..]);
    var fieldsEnd = BinaryPrimitives.ReadUInt16LittleEndian(data[11..]);
    if (fieldsEnd != 0)
      throw new InvalidDataException($"EWF: unexpected fields_end 0x{fieldsEnd:X4} (expected 0x0000).");

    var sections = new List<Section>();
    long cursor = FileHeaderSize;
    var guard = 0;
    while (cursor + SectionDescriptorSize <= data.Length) {
      if (++guard > 4096)
        throw new InvalidDataException("EWF: section-chain guard tripped.");

      var desc = data.Slice((int)cursor, SectionDescriptorSize);
      var type = ReadAsciiType(desc[..16]);
      var nextOffset = BinaryPrimitives.ReadUInt64LittleEndian(desc[16..]);
      var sectionSize = BinaryPrimitives.ReadUInt64LittleEndian(desc[24..]);
      var checksum = BinaryPrimitives.ReadUInt32LittleEndian(desc[72..]);

      var available = Math.Max(0L, data.Length - cursor - SectionDescriptorSize);
      var declaredPayload = sectionSize >= SectionDescriptorSize
        ? Math.Min((ulong)available, sectionSize - SectionDescriptorSize)
        : 0UL;
      if (declaredPayload > int.MaxValue)
        throw new NotSupportedException("EWF: one section exceeds the in-memory section-reader limit.");
      var payloadLen = (int)declaredPayload;
      var payload = payloadLen == 0
        ? []
        : data.Slice(checked((int)(cursor + SectionDescriptorSize)), payloadLen).ToArray();

      sections.Add(new Section(type, cursor, nextOffset, sectionSize, checksum, payload));
      if (type is "done" or "next") break;

      if (nextOffset > (ulong)cursor && nextOffset < (ulong)data.Length) {
        cursor = checked((long)nextOffset);
      } else if (sectionSize >= SectionDescriptorSize && sectionSize <= (ulong)(data.Length - cursor)) {
        cursor += checked((long)sectionSize);
      } else {
        cursor += SectionDescriptorSize;
      }
    }

    return new EwfImage(isLogical, segment, sections, data.Length);
  }

  /// <summary>
  /// Reconstructs the logical raw media from a single EVF segment. Stored chunks
  /// have their trailing Adler-32 removed and verified; compressed chunks are
  /// decoded as zlib according to the table-entry MSB.
  /// </summary>
  public static byte[] ExtractMedia(EwfImage image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.IsLogical)
      throw new NotSupportedException("EWF: LVF logical-evidence payload reconstruction is not implemented yet.");

    var volume = image.Sections.FirstOrDefault(s => s.Type is "volume" or "data")
      ?? throw new InvalidDataException("EWF: missing volume/data section.");
    var sectors = image.Sections.FirstOrDefault(s => s.Type == "sectors")
      ?? throw new InvalidDataException("EWF: missing sectors section.");
    var table = image.Sections.FirstOrDefault(s => s.Type is "table" or "table2")
      ?? throw new InvalidDataException("EWF: missing chunk table section.");

    if (volume.Payload.Length < 20)
      throw new InvalidDataException("EWF: volume section is too short.");
    if (table.Payload.Length < 28)
      throw new InvalidDataException("EWF: table section is too short.");

    var volumeChunkCount = BinaryPrimitives.ReadUInt32LittleEndian(volume.Payload.AsSpan(4));
    var sectorsPerChunk = BinaryPrimitives.ReadUInt32LittleEndian(volume.Payload.AsSpan(8));
    var bytesPerSector = BinaryPrimitives.ReadUInt32LittleEndian(volume.Payload.AsSpan(12));
    var totalSectors = BinaryPrimitives.ReadUInt32LittleEndian(volume.Payload.AsSpan(16));
    var chunkCount = BinaryPrimitives.ReadUInt32LittleEndian(table.Payload.AsSpan(0));
    var baseOffset = BinaryPrimitives.ReadUInt64LittleEndian(table.Payload.AsSpan(8));

    if (volumeChunkCount != 0 && chunkCount != volumeChunkCount)
      throw new InvalidDataException($"EWF: volume/table chunk-count mismatch ({volumeChunkCount} vs {chunkCount}).");
    if (chunkCount > 0x01000000)
      throw new InvalidDataException($"EWF: implausible chunk count {chunkCount}.");
    if (bytesPerSector == 0 || bytesPerSector > 1024 * 1024 || sectorsPerChunk == 0)
      throw new InvalidDataException("EWF: invalid sector/chunk geometry.");

    var entriesBytes = checked((long)chunkCount * sizeof(uint));
    if (24L + entriesBytes + 4 > table.Payload.LongLength)
      throw new InvalidDataException("EWF: chunk table is truncated.");

    var offsets = new uint[checked((int)chunkCount)];
    for (var i = 0; i < offsets.Length; ++i)
      offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(table.Payload.AsSpan(24 + i * 4));

    using var media = new MemoryStream();
    var sectorsPayloadAbsolute = checked((ulong)sectors.DescriptorOffset + SectionDescriptorSize);
    var sectorsEndAbsolute = checked((ulong)sectors.DescriptorOffset +
      (sectors.SectionSize >= SectionDescriptorSize
        ? sectors.SectionSize
        : (ulong)(SectionDescriptorSize + sectors.Payload.Length)));

    for (var i = 0; i < offsets.Length; ++i) {
      var rawEntry = offsets[i];
      var compressed = (rawEntry & 0x80000000U) != 0;
      var absoluteStart = checked(baseOffset + (rawEntry & 0x7FFFFFFFU));
      var absoluteEnd = i + 1 < offsets.Length
        ? checked(baseOffset + (offsets[i + 1] & 0x7FFFFFFFU))
        : sectorsEndAbsolute;

      if (absoluteStart < sectorsPayloadAbsolute || absoluteEnd < absoluteStart || absoluteEnd > sectorsEndAbsolute)
        throw new InvalidDataException($"EWF: chunk {i} points outside the sectors section.");

      var payloadStart = checked((long)(absoluteStart - sectorsPayloadAbsolute));
      var payloadLength = checked((long)(absoluteEnd - absoluteStart));
      if (payloadStart < 0 || payloadLength < 0 || payloadStart + payloadLength > sectors.Payload.LongLength)
        throw new InvalidDataException($"EWF: chunk {i} range is invalid.");
      if (payloadLength > int.MaxValue)
        throw new NotSupportedException("EWF: one chunk exceeds the in-memory decoder limit.");

      var chunk = sectors.Payload.AsSpan((int)payloadStart, (int)payloadLength);
      if (compressed) {
        media.Write(ZlibStream.Decompress(chunk));
      } else {
        if (chunk.Length < 4)
          throw new InvalidDataException($"EWF: stored chunk {i} has no Adler-32 trailer.");
        var data = chunk[..^4];
        var expected = BinaryPrimitives.ReadUInt32LittleEndian(chunk[^4..]);
        var actual = Adler32.Compute(data);
        if (expected != actual)
          throw new InvalidDataException(
            $"EWF: stored chunk {i} Adler-32 mismatch (expected 0x{expected:X8}, got 0x{actual:X8}).");
        media.Write(data);
      }
    }

    var declaredLength = checked((long)totalSectors * bytesPerSector);
    if (declaredLength > media.Length)
      throw new InvalidDataException(
        $"EWF: decoded chunks are shorter than declared media ({media.Length} < {declaredLength}).");
    media.SetLength(declaredLength);
    return media.ToArray();
  }

  private static string ReadAsciiType(ReadOnlySpan<byte> raw) {
    var end = raw.Length;
    while (end > 0 && raw[end - 1] == 0) --end;
    var slice = raw[..end];
    for (var i = 0; i < slice.Length; ++i)
      if (slice[i] < 0x20 || slice[i] > 0x7E)
        return Convert.ToHexString(raw);
    return Encoding.ASCII.GetString(slice);
  }
}
