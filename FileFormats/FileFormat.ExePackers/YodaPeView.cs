#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.ExePackers;

/// <summary>
/// The handful of PE fields the Yoda stub walkers need: the image base, the
/// entry point (and where to write a restored one back), and a section table
/// that keeps both the virtual address and the raw 8-byte name.
/// </summary>
/// <remarks>
/// The shared <see cref="PackerScanner"/> section views drop the virtual address
/// and pad the name to a string; the stub's own walker compares the first four
/// raw name bytes and addresses sections by virtual address, so both are needed
/// verbatim here.
/// </remarks>
internal sealed class YodaPeView {

  internal readonly record struct Section(
    string Name,
    byte[] RawName,
    uint VirtualSize,
    uint VirtualAddress,
    uint RawSize,
    uint RawOffset,
    uint Characteristics);

  public required ulong ImageBase { get; init; }
  public required uint EntryPoint { get; init; }
  public required int EntryPointFieldOffset { get; init; }
  public required IReadOnlyList<Section> Sections { get; init; }

  public Section? FindStubSection() {
    foreach (var section in this.Sections)
      if (section.Name is "yC" or ".yC")
        return section;
    return null;
  }

  public static YodaPeView Parse(ReadOnlySpan<byte> image) {
    if (image.Length < 0x40 || image[0] != 'M' || image[1] != 'Z')
      throw new InvalidDataException("Not an MZ image.");

    var peOffset = BinaryPrimitives.ReadInt32LittleEndian(image[0x3C..]);
    if (peOffset < 0 || peOffset + 0x18 > image.Length)
      throw new InvalidDataException("PE header offset is out of range.");
    if (BinaryPrimitives.ReadUInt32LittleEndian(image[peOffset..]) != 0x0000_4550)
      throw new InvalidDataException("Missing PE signature.");

    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image[(peOffset + 6)..]);
    var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image[(peOffset + 20)..]);
    var optional = peOffset + 24;
    if (optional + 0x20 > image.Length)
      throw new InvalidDataException("Optional header is truncated.");

    var magic = BinaryPrimitives.ReadUInt16LittleEndian(image[optional..]);
    var entryPointField = optional + 16;
    var entryPoint = BinaryPrimitives.ReadUInt32LittleEndian(image[entryPointField..]);
    var imageBase = magic == 0x20B
      ? BinaryPrimitives.ReadUInt64LittleEndian(image[(optional + 24)..])
      : BinaryPrimitives.ReadUInt32LittleEndian(image[(optional + 28)..]);

    var table = optional + optionalSize;
    var sections = new List<Section>(sectionCount);
    for (var i = 0; i < sectionCount; ++i) {
      var at = table + 40 * i;
      if (at + 40 > image.Length)
        break;
      var rawName = image.Slice(at, 8).ToArray();
      sections.Add(new(
        Encoding.ASCII.GetString(rawName).TrimEnd('\0'),
        rawName,
        BinaryPrimitives.ReadUInt32LittleEndian(image[(at + 8)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(image[(at + 12)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(image[(at + 16)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(image[(at + 20)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(image[(at + 36)..])));
    }

    return new() {
      ImageBase = imageBase,
      EntryPoint = entryPoint,
      EntryPointFieldOffset = entryPointField,
      Sections = sections,
    };
  }
}
