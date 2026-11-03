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

  /// <summary>
  /// Provides the evf signature value.
  /// </summary>
  public static readonly byte[] EvfSignature = [0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00]; // "EVF\t\r\n\xFF\x00"
  /// <summary>
  /// Provides the lvf signature value.
  /// </summary>
  public static readonly byte[] LvfSignature = [0x4C, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00]; // "LVF\t\r\n\xFF\x00"

  /// <summary>
  /// Defines the file header size constant value.
  /// </summary>
  public const int FileHeaderSize = 13;
  /// <summary>
  /// Defines the section descriptor size constant value.
  /// </summary>
  public const int SectionDescriptorSize = 76;

  /// <summary>
  /// Represents a section.
  /// </summary>
  public sealed record Section(
    string Type,
    long DescriptorOffset,
    ulong NextSectionOffset,
    ulong SectionSize,
    uint Checksum,
    byte[] Payload);

  /// <summary>
  /// Represents an ewf image.
  /// </summary>
  public sealed record EwfImage(
    bool IsLogical,
    ushort SegmentNumber,
    List<Section> Sections,
    long TotalFileSize,
    byte[]? Raw = null) {
    /// <summary>Whether this segment's chain ends in "next": the media continues in further segment files.</summary>
    public bool ContinuesInNextSegment => this.Sections.Count > 0 && this.Sections[^1].Type == "next";
  }

  /// <summary>Reads one EWF segment and its section chain.</summary>
  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
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

    return new EwfImage(isLogical, segment, sections, data.Length, data.ToArray());
  }

  /// <summary>
  /// Reconstructs the raw media of a self-contained (single-segment) EVF image held in memory,
  /// through <see cref="EwfMediaStream"/>: every sectors/table group, stored chunks checked by
  /// their Adler-32, compressed chunks decoded as zlib.
  /// </summary>
  /// <exception cref="NotSupportedException">The image is logical (LVF) or continues in further segment files.</exception>
  public static byte[] ExtractMedia(EwfImage image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.IsLogical)
      throw new NotSupportedException("EWF: LVF logical-evidence payload reconstruction is not implemented yet.");
    if (image.Raw == null)
      throw new InvalidOperationException("EWF: the image was not read from its bytes.");
    using var media = EwfMediaStream.Open(new MemoryStream(image.Raw, writable: false), leaveOpen: false);
    if (media.Length > Array.MaxLength)
      throw new NotSupportedException("EWF: the media is larger than one array; read it through EwfMediaStream.");
    var result = new byte[media.Length];
    media.ReadExactly(result);
    return result;
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
