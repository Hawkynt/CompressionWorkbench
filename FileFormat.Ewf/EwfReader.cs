#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Ewf;

/// <summary>
/// Reader for EnCase Expert Witness Format (EWF) forensic images — the
/// .e01/.ewf/.l01 family used by EnCase and libewf. Walks the section chain
/// starting at offset 13 (just past the 8-byte signature + 1-byte fields_start
/// + 2-byte segment + 2-byte fields_end) and surfaces each section's raw bytes
/// along with parsed metadata. Full sector decompression is deferred.
/// </summary>
/// <remarks>
/// Layout per libewf documentation:
/// <code>
///   Offset 0-7    Signature "EVF\x09\x0D\x0A\xFF\x00" (or "LVF..." for .l01)
///   Offset 8      fields_start     (0x01)
///   Offset 9-10   segment_number   (u16 LE)
///   Offset 11-12  fields_end       (0x0000)
///   Offset 13..   Section descriptors (76 bytes each):
///                   0-15  type (ASCII, NUL-padded)
///                   16-23 next_section_offset (u64 LE, absolute)
///                   24-31 section_size        (u64 LE)
///                   32-71 padding (zero)
///                   72-75 Adler-32 checksum
///                 followed by section_size - 76 bytes of payload.
///   The final section in a segment has type "next" or "done"; "next" links
///   to the following segment, "done" closes the image.
/// </code>
/// </remarks>
public sealed class EwfReader {

  public static readonly byte[] EvfSignature = [0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00]; // "EVF\t\r\n\xFF\x00"
  public static readonly byte[] LvfSignature = [0x4C, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00]; // "LVF\t\r\n\xFF\x00"

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
    bool IsLogical,             // true for LVF (.l01), false for EVF (.e01/.ewf)
    ushort SegmentNumber,
    List<Section> Sections,
    long TotalFileSize);

  public static EwfImage Read(ReadOnlySpan<byte> data) {
    if (data.Length < FileHeaderSize)
      throw new InvalidDataException("EWF: file shorter than 13-byte header.");

    var isLogical = data[..8].SequenceEqual(LvfSignature);
    if (!isLogical && !data[..8].SequenceEqual(EvfSignature))
      throw new InvalidDataException("EWF: invalid file signature (expected EVF or LVF magic at offset 0).");

    var fieldsStart = data[8];
    if (fieldsStart != 0x01)
      throw new InvalidDataException($"EWF: unexpected fields_start byte 0x{fieldsStart:X2} (expected 0x01).");

    var segment = BinaryPrimitives.ReadUInt16LittleEndian(data[9..]);
    var fieldsEnd = BinaryPrimitives.ReadUInt16LittleEndian(data[11..]);
    if (fieldsEnd != 0x0000)
      throw new InvalidDataException($"EWF: unexpected fields_end 0x{fieldsEnd:X4} (expected 0x0000).");

    var sections = new List<Section>();
    long cursor = FileHeaderSize;
    var guard = 0;
    while (cursor + SectionDescriptorSize <= data.Length) {
      guard++;
      if (guard > 4096) // runaway guard — real EWF segments have dozens of sections
        throw new InvalidDataException("EWF: section-chain guard tripped.");

      var desc = data.Slice((int)cursor, SectionDescriptorSize);
      var type = ReadAsciiType(desc[..16]);
      var nextOffset = BinaryPrimitives.ReadUInt64LittleEndian(desc[16..]);
      var sectionSize = BinaryPrimitives.ReadUInt64LittleEndian(desc[24..]);
      var checksum = BinaryPrimitives.ReadUInt32LittleEndian(desc[72..]);

      // section_size includes the 76-byte descriptor; payload is whatever follows.
      var payloadLen = sectionSize >= SectionDescriptorSize
        ? (int)Math.Min(sectionSize - SectionDescriptorSize, (ulong)(data.Length - cursor - SectionDescriptorSize))
        : 0;
      var payload = payloadLen > 0
        ? data.Slice((int)(cursor + SectionDescriptorSize), payloadLen).ToArray()
        : [];

      sections.Add(new Section(type, cursor, nextOffset, sectionSize, checksum, payload));

      // Terminal sections: "done" closes; "next" closes the current segment (caller would open the next segment file).
      if (type is "done" or "next") break;

      // Step to next section: prefer next_section_offset when it moves forward.
      if (nextOffset > (ulong)cursor && nextOffset < (ulong)data.Length) {
        cursor = (long)nextOffset;
      } else if (sectionSize >= SectionDescriptorSize) {
        cursor += (long)sectionSize;
      } else {
        // Malformed — walk by descriptor only to make progress.
        cursor += SectionDescriptorSize;
      }
    }

    return new EwfImage(
      IsLogical: isLogical,
      SegmentNumber: segment,
      Sections: sections,
      TotalFileSize: data.Length);
  }

  private static string ReadAsciiType(ReadOnlySpan<byte> raw) {
    // Trim trailing NULs; fall back to hex if ASCII isn't printable.
    var end = raw.Length;
    while (end > 0 && raw[end - 1] == 0) end--;
    var slice = raw[..end];
    for (var i = 0; i < slice.Length; i++) {
      if (slice[i] < 0x20 || slice[i] > 0x7E)
        return Convert.ToHexString(raw);
    }
    return Encoding.ASCII.GetString(slice);
  }
}
