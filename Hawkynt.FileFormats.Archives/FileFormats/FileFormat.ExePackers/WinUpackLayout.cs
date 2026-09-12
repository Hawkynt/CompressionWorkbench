#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.ExePackers;

/// <summary>
/// Everything the WinUpack / Upack loader stub needs in order to unpack, read
/// back out of the packed image the same way the stub reads it.
/// </summary>
/// <param name="PayloadOffset">File offset of the first byte of the range-coded payload.</param>
/// <param name="ImageVirtualAddress">Address the decompressed image is written to.</param>
/// <param name="ImageSize">Number of bytes the stub decompresses.</param>
/// <param name="FilterBase">Bias the call/jump filter adds to every stored target.</param>
/// <param name="FilterCount">Number of call/jump operands the filter rewrites.</param>
/// <param name="FilterTag">Marker byte that flags a rewritten operand.</param>
internal readonly record struct WinUpackLayout(
  int PayloadOffset,
  uint ImageVirtualAddress,
  int ImageSize,
  uint FilterBase,
  uint FilterCount,
  byte FilterTag);

/// <summary>
/// Locates the Upack parameter block inside a packed PE.
/// </summary>
/// <remarks>
/// <para>
/// Upack ships two container shapes, and both keep their parameters in fields a
/// PE loader never reads, so nothing has to be spent on a real header.
/// </para>
/// <para>
/// The "compressed header" shape folds the PE headers into the DOS stub
/// (<c>e_lfanew</c> points inside the MZ header) and hides the parameters in the
/// spare fields of the three section headers — <c>PointerToRelocations</c>,
/// <c>NumberOfRelocations</c> and two section names. The stub addresses them at
/// fixed offsets from the section table, which is how they are read here.
/// </para>
/// <para>
/// The plain-header shape keeps a conventional PE header and puts a relocatable
/// parameter table directly behind the section table. Its first dword is the
/// table's own load address, which the stub subtracts from its actual location to
/// discover the image-base delta — a self-reference precise enough to be the
/// signature for this shape.
/// </para>
/// <para>
/// The filter marker byte is chosen per file by the packer, so it is read from
/// the one place it is stated verbatim: the compare in the stub's filter loop
/// (<c>mov eax,[edi]; cmp al,tag; jne</c>), which occurs exactly once per image.
/// </para>
/// </remarks>
internal static class WinUpackLayoutReader {
  private static ReadOnlySpan<byte> FilterCompareOpcodes => [0x8B, 0x07, 0x3C];

  public static bool TryRead(byte[] image, long maximumImageSize, out WinUpackLayout layout) {
    layout = default;
    if (!PackerScanner.IsPe(image) || image.Length < 0x40)
      return false;

    var peOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C));
    if (peOffset < 0 || peOffset + 0x78 > image.Length)
      return false;

    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(peOffset + 6));
    var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(peOffset + 20));
    var optionalOffset = peOffset + 24;
    if (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(optionalOffset)) != 0x10B)
      return false;

    var imageBase = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optionalOffset + 28));
    var sectionTable = optionalOffset + optionalSize;
    if (sectionCount is 0 or > 16 || sectionTable < 0 || sectionTable + sectionCount * 40 > image.Length)
      return false;

    var sectionTableEnd = sectionTable + sectionCount * 40;
    uint payloadVirtualAddress, filterCount, imageVirtualAddress, imageEnd, filterBase;
    if (sectionTableEnd + 0x58 <= image.Length &&
        BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sectionTableEnd)) == imageBase + (uint)sectionTableEnd) {
      var table = sectionTableEnd + 0x0C;
      if (table + 0x54 > image.Length)
        return false;
      imageVirtualAddress = Read(image, table + 0x04);
      filterBase = Read(image, table + 0x1C);
      imageEnd = Read(image, table + 0x2C);
      filterCount = Read(image, table + 0x48);
      payloadVirtualAddress = Read(image, table + 0x50);
    } else if (sectionCount >= 3 && sectionTable + 0x58 <= image.Length) {
      payloadVirtualAddress = Read(image, sectionTable + 0x18);
      filterCount = Read(image, sectionTable + 0x20);
      imageVirtualAddress = Read(image, sectionTable + 0x28);
      imageEnd = Read(image, sectionTable + 0x50);
      filterBase = Read(image, sectionTable + 0x54);
    } else
      return false;

    // Both shapes bias the filter by the image start minus four; anything else
    // means the fields were read out of a header that is not Upack's.
    if (filterBase != imageVirtualAddress - 4 || imageEnd <= imageVirtualAddress)
      return false;

    var size = imageEnd - imageVirtualAddress;
    if (size > maximumImageSize || size > int.MaxValue)
      return false;

    if (!TryMapToFile(image, peOffset, optionalOffset, optionalSize, sectionCount, imageBase, payloadVirtualAddress, out var payloadOffset))
      return false;
    if (!TryReadFilterTag(image, out var tag))
      return false;

    layout = new(payloadOffset, imageVirtualAddress, (int)size, filterBase, filterCount, tag);
    return true;
  }

  private static uint Read(byte[] image, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset));

  private static bool TryMapToFile(
    byte[] image,
    int peOffset,
    int optionalOffset,
    int optionalSize,
    int sectionCount,
    uint imageBase,
    uint virtualAddress,
    out int fileOffset) {
    fileOffset = 0;
    if (virtualAddress < imageBase)
      return false;

    var rva = virtualAddress - imageBase;
    var sectionTable = optionalOffset + optionalSize;
    for (var i = 0; i < sectionCount; ++i) {
      var entry = sectionTable + i * 40;
      var virtualSize = Read(image, entry + 8);
      var sectionRva = Read(image, entry + 12);
      var rawSize = Read(image, entry + 16);
      var rawOffset = Read(image, entry + 20);
      if (rawSize == 0 || rva < sectionRva || rva >= sectionRva + Math.Max(virtualSize, rawSize))
        continue;

      // The loader rounds PointerToRawData down to a 512-byte boundary, and
      // Upack's compressed-header shape relies on exactly that.
      var start = (rawOffset & ~0x1FFu) + (rva - sectionRva);
      if (start >= (uint)image.Length)
        return false;
      fileOffset = (int)start;
      return true;
    }

    return false;
  }

  private static bool TryReadFilterTag(byte[] image, out byte tag) {
    tag = 0;
    var found = false;
    var span = image.AsSpan();
    for (var i = 0; i + 5 <= span.Length; ++i) {
      var next = span[i..].IndexOf(FilterCompareOpcodes);
      if (next < 0)
        break;
      i += next;
      if (i + 5 > span.Length)
        break;
      if (span[i + 4] != 0x75)
        continue;
      if (found && span[i + 3] != tag)
        return false;
      tag = span[i + 3];
      found = true;
    }

    return found;
  }
}
