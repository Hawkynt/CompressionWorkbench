#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Dictionary.Aplib;

namespace FileFormat.ExePackers;

/// <summary>
/// Reader for the block container FSG ("Fast, Small, Good", bart/xt) writes into
/// its packed PE. Derived from the ~160-byte stub the packer emits and confirmed
/// against the public PackingData corpus.
///
/// FSG compresses each original section into its own bare aPLib stream and
/// concatenates them; there is no length field anywhere, the streams are
/// delimited only by aPLib's own end-of-stream marker. The stub opens with three
/// immediates that name everything needed to walk them:
///
///   mov ebx, imm32   destination table, parked just past the section headers
///   mov edi, imm32   destination of the first block, as a virtual address
///   mov esi, imm32   first aPLib stream
///
/// After each stream the stub reads one 16-bit word from the table:
///
///   1        the next dword is the destination virtual address (table advances 6)
///   2        no more blocks
///   other    the destination page is word - 2, truncated to 16 bits
///
/// Encoding destinations as page numbers is why FSG only ever targets
/// section-aligned addresses; the escape hatch for word 1 exists because the
/// last block is a patch inside an already-written section, not a section of its
/// own. The page number is the full virtual address shifted down, so it only
/// survives 16 bits for images based at or below 0x0FFFF000 - for anything
/// higher it wraps, and the RVA has to be recovered by subtracting the image
/// base's own page number with the same wrap.
///
/// Two of the three stub immediates are template constants FSG never rewrites:
/// the destination table and the first destination are always emitted against
/// an image base of 0x400000 whatever the packed file declares. Only the source
/// pointer and the table's page numbers follow the real base.
/// </summary>
internal static class FsgImage {
  /// <summary>
  /// The stub body that follows the three immediates: <c>push ebx</c>, the call
  /// that parks the getbit routine's address on the stack, the getbit routine
  /// itself (<c>add dl,dl / jne / mov dl,[esi] / inc esi / adc dl,dl / ret</c>),
  /// then <c>cld / mov dl,0x80 / movsb</c> and the aPLib token loop.
  /// </summary>
  private static ReadOnlySpan<byte> StubSignature => [
    0x53, 0xE8, 0x0A, 0x00, 0x00, 0x00, 0x02, 0xD2, 0x75, 0x05, 0x8A, 0x16, 0x46, 0x12, 0xD2, 0xC3,
    0xFC, 0xB2, 0x80, 0xA4, 0x6A, 0x02, 0x5B, 0xFF, 0x14, 0x24,
  ];

  private const byte LoadEbxOpcode = 0xBB;
  private const byte LoadEdiOpcode = 0xBF;
  private const byte LoadEsiOpcode = 0xBE;

  private const ushort TableAbsoluteDestination = 1;
  private const ushort TableEnd = 2;
  private const int MaxBlocks = 256;

  /// <summary>Image base the stub's own constants are written against, whatever the packed file declares.</summary>
  private const uint StubTemplateImageBase = 0x00400000;

  internal readonly record struct FsgBlock(uint Rva, byte[] Data);

  private static bool EndWith(List<FsgBlock> found, out IReadOnlyList<FsgBlock> blocks) {
    blocks = found;
    return found.Count > 0;
  }

  /// <summary>
  /// Walks the block list starting from the entry-point stub. Returns
  /// <see langword="false"/> for images whose stub is not the shape this reader
  /// models, so the caller can fall back to the generic aPLib scan.
  /// </summary>
  public static bool TryRead(ReadOnlySpan<byte> image, long maximumDecompressedSize, out IReadOnlyList<FsgBlock> blocks) {
    blocks = [];
    if (!FsgHeaders.TryParse(image, out var pe))
      return false;
    if (!TryMapRva(pe, image.Length, pe.EntryPointRva, out var stub))
      return false;
    if (stub + 15 + StubSignature.Length > image.Length)
      return false;
    if (image[stub] != LoadEbxOpcode || image[stub + 5] != LoadEdiOpcode || image[stub + 10] != LoadEsiOpcode)
      return false;
    if (!image.Slice(stub + 15, StubSignature.Length).SequenceEqual(StubSignature))
      return false;

    var tableVa = BinaryPrimitives.ReadUInt32LittleEndian(image[(stub + 1)..]);
    var firstDestinationVa = BinaryPrimitives.ReadUInt32LittleEndian(image[(stub + 6)..]);
    var sourceVa = BinaryPrimitives.ReadUInt32LittleEndian(image[(stub + 11)..]);
    if (tableVa < StubTemplateImageBase || firstDestinationVa < StubTemplateImageBase || sourceVa < pe.ImageBase)
      return false;
    if (!TryMapRva(pe, image.Length, tableVa - StubTemplateImageBase, out var table))
      return false;
    if (!TryMapRva(pe, image.Length, (uint)(sourceVa - pe.ImageBase), out var source))
      return false;

    var destination = firstDestinationVa - StubTemplateImageBase;
    if (destination >= pe.SizeOfImage)
      return false;

    var cap = (int)Math.Min(maximumDecompressedSize <= 0 ? int.MaxValue : maximumDecompressedSize, Math.Max(pe.SizeOfImage, 0x10000u));
    var found = new List<FsgBlock>();
    while (found.Count < MaxBlocks) {
      byte[] data;
      int consumed;
      try {
        data = AplibBuildingBlock.DecompressRaw(image[source..], cap, out var endMarkerHit, out consumed);
        if (!endMarkerHit)
          // A stream that runs past the end of a truncated sample still leaves
          // the blocks before it intact; only a bad first block means the
          // layout was never FSG's to begin with.
          return EndWith(found, out blocks);
      } catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException) {
        return EndWith(found, out blocks);
      }

      found.Add(new(destination, data));
      source += consumed;
      if (source >= image.Length || table + 2 > image.Length)
        break;

      var word = BinaryPrimitives.ReadUInt16LittleEndian(image[table..]);
      if (word == TableEnd)
        break;

      if (word == TableAbsoluteDestination) {
        if (table + 6 > image.Length)
          break;
        var absolute = BinaryPrimitives.ReadUInt32LittleEndian(image[(table + 2)..]);
        if (absolute < pe.ImageBase)
          break;
        destination = absolute - pe.ImageBase;
        table += 6;
      } else {
        destination = (uint)(((word - 2 - (int)(pe.ImageBase >> 12)) & 0xFFFF) << 12);
        table += 2;
      }

      if (destination >= pe.SizeOfImage)
        break;
    }

    return EndWith(found, out blocks);
  }

  /// <summary>
  /// Flattens the blocks into the section that receives them — FSG leaves that
  /// section with no raw data at all — so the result is the mapped original
  /// image body rather than a pile of fragments.
  /// </summary>
  public static byte[] Assemble(ReadOnlySpan<byte> image, IReadOnlyList<FsgBlock> blocks) {
    if (!FsgHeaders.TryParse(image, out var pe) || blocks.Count == 0)
      return [];

    var target = pe.Sections
      .Where(s => s.RawSize == 0 && s.VirtualSize > 0)
      .OrderByDescending(s => s.VirtualSize)
      .FirstOrDefault();

    // FSG occasionally aims the first block one page below the section that
    // receives it, so the buffer spans whichever comes first and stretches to
    // cover every block rather than silently dropping one.
    var lowest = blocks.Min(b => b.Rva);
    var highest = blocks.Max(b => b.Rva + (uint)b.Data.Length);
    var baseRva = target.VirtualSize > 0 ? Math.Min(target.VirtualAddress, lowest) : lowest;
    var size = Math.Max(target.VirtualSize > 0 ? target.VirtualAddress + target.VirtualSize : 0, highest) - baseRva;
    if (size == 0 || size > int.MaxValue)
      return [];

    var buffer = new byte[size];
    var wrote = false;
    foreach (var block in blocks) {
      if (block.Rva < baseRva)
        continue;
      var offset = block.Rva - baseRva;
      if (offset + (uint)block.Data.Length > size)
        continue;
      block.Data.CopyTo(buffer, (int)offset);
      wrote = true;
    }

    return wrote ? buffer : [];
  }

  /// <summary>
  /// Maps an RVA to a file offset. FSG parks its destination table inside the PE
  /// headers, where RVA and file offset coincide, so headers are mapped
  /// identically and only section bodies go through the section table.
  /// </summary>
  private static bool TryMapRva(in FsgHeaders pe, int imageLength, uint rva, out int offset) {
    if (pe.TryRvaToOffset(rva, out offset))
      return offset < imageLength;

    var firstSection = pe.Sections.Count > 0 ? pe.Sections.Min(s => s.VirtualAddress) : 0u;
    if (rva < firstSection && rva < imageLength) {
      offset = (int)rva;
      return true;
    }

    offset = 0;
    return false;
  }
}

/// <summary>
/// The handful of PE header fields FSG addresses itself with. The shared
/// <see cref="PackerScanner"/> exposes section raw ranges but not virtual
/// addresses, and FSG speaks exclusively in virtual addresses.
/// </summary>
internal readonly record struct FsgSection(uint VirtualAddress, uint VirtualSize, uint RawSize, uint RawOffset);

internal readonly record struct FsgHeaders(uint ImageBase, uint SizeOfImage, uint EntryPointRva, IReadOnlyList<FsgSection> Sections) {
  public static bool TryParse(ReadOnlySpan<byte> image, out FsgHeaders headers) {
    headers = default;
    if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
      return false;

    var peOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(image[0x3C..]);
    if (peOffset < 0 || peOffset + 24 > image.Length)
      return false;
    if (BinaryPrimitives.ReadUInt32LittleEndian(image[peOffset..]) != 0x00004550)
      return false;

    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image[(peOffset + 6)..]);
    var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image[(peOffset + 20)..]);
    var optional = peOffset + 24;
    if (optional + 60 > image.Length || BinaryPrimitives.ReadUInt16LittleEndian(image[optional..]) != 0x10B)
      return false;

    var tableOffset = optional + optionalSize;
    if (sectionCount == 0 || tableOffset + sectionCount * 40 > image.Length)
      return false;

    var sections = new List<FsgSection>(sectionCount);
    for (var i = 0; i < sectionCount; ++i) {
      var offset = tableOffset + i * 40;
      sections.Add(new(
        BinaryPrimitives.ReadUInt32LittleEndian(image[(offset + 12)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(image[(offset + 8)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(image[(offset + 16)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(image[(offset + 20)..])));
    }

    headers = new(
      BinaryPrimitives.ReadUInt32LittleEndian(image[(optional + 28)..]),
      BinaryPrimitives.ReadUInt32LittleEndian(image[(optional + 56)..]),
      BinaryPrimitives.ReadUInt32LittleEndian(image[(optional + 16)..]),
      sections);
    return true;
  }

  public bool TryRvaToOffset(uint rva, out int offset) {
    foreach (var section in this.Sections) {
      if (section.RawSize == 0 || rva < section.VirtualAddress || rva >= section.VirtualAddress + section.RawSize)
        continue;
      offset = (int)(section.RawOffset + (rva - section.VirtualAddress));
      return true;
    }

    offset = 0;
    return false;
  }
}
