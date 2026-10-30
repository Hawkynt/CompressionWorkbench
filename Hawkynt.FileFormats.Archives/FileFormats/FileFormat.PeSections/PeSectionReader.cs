using System.Buffers.Binary;
using System.Text;

namespace FileFormat.PeSections;

/// <summary>What a <see cref="PeSectionItem"/> is.</summary>
public enum PeSectionKind {
  /// <summary>A section of the section table.</summary>
  Section,
  /// <summary>The attribute-certificate table (Authenticode), addressed by file offset.</summary>
  Certificate,
  /// <summary>Debug data that lies outside every section (a debug directory entry's raw data).</summary>
  Debug,
  /// <summary>The COFF symbol table plus its string table.</summary>
  CoffSymbols,
  /// <summary>Bytes past the 4 KiB header area that no section or directory claims.</summary>
  Gap,
  /// <summary>Bytes after the end of the image (installer payloads, appended archives).</summary>
  Overlay,
}

/// <summary>One region of a PE or TE image as the section view exposes it.</summary>
/// <param name="Name">Entry name: the section name with <c>/</c> and <c>\</c> replaced by <c>_</c>, or a synthetic name.</param>
/// <param name="Offset">File offset of the first byte.</param>
/// <param name="Size">Bytes extracted: the section's raw size, cut down to its virtual size or directory size when smaller.</param>
/// <param name="PhysicalSize">Raw (file-aligned) size.</param>
/// <param name="VirtualSize">Virtual size from the section header (0 for synthetic regions).</param>
/// <param name="VirtualAddress">RVA of a real section; 0 otherwise.</param>
/// <param name="Characteristics">Section characteristics; 0 for synthetic regions.</param>
/// <param name="Kind">What the region is.</param>
public sealed record PeSectionItem(
  string Name, long Offset, long Size, long PhysicalSize, uint VirtualSize, uint VirtualAddress, uint Characteristics, PeSectionKind Kind);

/// <summary>
/// Splits a Windows PE/COFF image into its sections, its certificate table, out-of-section debug data,
/// the COFF symbol table, unclaimed gaps and the trailing overlay — the section-level view 7-Zip's PE
/// handler gives, without its resource-tree expansion (resources have their own descriptor).
/// </summary>
/// <remarks>
/// Layout per the Microsoft PE/COFF specification. The extraction rules (a section extracts
/// <c>min(VirtualSize, SizeOfRawData)</c> bytes, <c>.reloc</c>/<c>.pdata</c> stop at their data
/// directory's size, debug data outside the sections and the 4 KiB-aligned gaps become their own
/// items, and the image ends at the furthest of them) are converted from 7-Zip's
/// <c>CPP/7zip/Archive/PeHandler.cpp</c> (Igor Pavlov, LGPL-2.1-or-later).
/// </remarks>
public sealed class PeSectionReader {

  private const int CoffHeaderSize = 20;
  private const int SectionHeaderSize = 40;
  private const int OptionalHeader32Min = 96;
  private const int OptionalHeader64Min = 112;
  private const int MaxDirectories = 16;
  private const int DirException = 3, DirCertificate = 4, DirBaseReloc = 5, DirDebug = 6;
  private const uint GapAlignment = 1u << 12;

  /// <summary>COFF machine type.</summary>
  public ushort Machine { get; }

  /// <summary>Whether the optional header is PE32+.</summary>
  public bool Is64Bit { get; }

  /// <summary>Optional-header subsystem.</summary>
  public ushort Subsystem { get; }

  /// <summary>COFF time stamp.</summary>
  public uint TimeDateStamp { get; }

  /// <summary>File offset of the <c>PE\0\0</c> signature.</summary>
  public uint PeHeaderOffset { get; }

  /// <summary>Where the image ends: the furthest byte any section, directory or symbol table claims.</summary>
  public long ImageSize { get; }

  /// <summary>Items in 7-Zip's order: sections, certificate, debug, symbols, gaps, then the overlay.</summary>
  public IReadOnlyList<PeSectionItem> Items { get; }

  /// <summary>Parses the image held in <paramref name="image"/>.</summary>
  /// <exception cref="InvalidDataException">The bytes are not a PE image or its tables run off the file.</exception>
  public PeSectionReader(ReadOnlySpan<byte> image) {
    if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
      throw new InvalidDataException("Not a PE image (missing MZ header).");
    var pe = BinaryPrimitives.ReadUInt32LittleEndian(image[0x3C..]);
    if (pe < 0x40 || pe > 0x1000)
      throw new InvalidDataException($"e_lfanew 0x{pe:X} is not a PE header offset.");
    if (pe + 4L + CoffHeaderSize > image.Length || BinaryPrimitives.ReadUInt32LittleEndian(image[(int)pe..]) != 0x00004550)
      throw new InvalidDataException("Not a PE image (missing PE signature).");
    this.PeHeaderOffset = pe;

    var coff = image[(int)(pe + 4)..];
    this.Machine = BinaryPrimitives.ReadUInt16LittleEndian(coff);
    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(coff[2..]);
    this.TimeDateStamp = BinaryPrimitives.ReadUInt32LittleEndian(coff[4..]);
    var symbolTable = BinaryPrimitives.ReadUInt32LittleEndian(coff[8..]);
    var symbolCount = BinaryPrimitives.ReadUInt32LittleEndian(coff[12..]);
    var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(coff[16..]);
    if (optionalSize < OptionalHeader32Min)
      throw new InvalidDataException("PE optional header is too small.");

    var optStart = pe + 4 + CoffHeaderSize;
    var tableEnd = (long)optStart + optionalSize + (long)sectionCount * SectionHeaderSize;
    if (tableEnd > image.Length)
      throw new InvalidDataException("PE section table runs past the end of the file.");
    var opt = image.Slice((int)optStart, optionalSize);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(opt);
    if (magic is not (0x10B or 0x20B))
      throw new InvalidDataException($"Unknown PE optional-header magic 0x{magic:X}.");
    this.Is64Bit = magic == 0x20B;
    if (this.Is64Bit && optionalSize < OptionalHeader64Min)
      throw new InvalidDataException("PE32+ optional header is too small.");
    this.Subsystem = BinaryPrimitives.ReadUInt16LittleEndian(opt[68..]);
    var dirPos = this.Is64Bit ? 108 : 92;
    var dirCount = BinaryPrimitives.ReadUInt32LittleEndian(opt[dirPos..]);
    dirPos += 4;
    if (dirCount > 1 << 13 || dirPos + 8L * dirCount > optionalSize)
      throw new InvalidDataException("PE data directories overrun the optional header.");
    var dirs = new (uint Va, uint Size)[MaxDirectories];
    for (var i = 0; i < Math.Min(dirCount, MaxDirectories); ++i)
      dirs[i] = (BinaryPrimitives.ReadUInt32LittleEndian(opt[(dirPos + i * 8)..]), BinaryPrimitives.ReadUInt32LittleEndian(opt[(dirPos + i * 8 + 4)..]));

    var items = new List<PeSectionItem>(sectionCount + 4);
    var total = tableEnd;
    var table = image[(int)(optStart + optionalSize)..];
    for (var i = 0; i < sectionCount; ++i) {
      var h = table.Slice(i * SectionHeaderSize, SectionHeaderSize);
      var name = SectionName(h[..8]);
      var vsize = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]);
      var va = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
      var psize = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
      var pa = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(h[36..]);
      long extract = vsize != 0 && vsize < psize ? vsize : psize;
      var dir = name switch { ".reloc" => DirBaseReloc, ".pdata" => DirException, _ => -1 };
      if (dir >= 0 && dirs[dir].Va == va && dirs[dir].Size <= psize)
        extract = dirs[dir].Size;
      items.Add(new PeSectionItem(name, pa, extract, psize, vsize, va, flags, PeSectionKind.Section));
      total = Math.Max(total, (long)pa + psize);
    }

    var debugBeyondSections = false;
    if (dirs[DirDebug].Size != 0)
      foreach (var debug in DebugItems(image, items, dirs[DirDebug], ref total)) {
        items.Add(debug);
        debugBeyondSections = true;
      }

    if (dirs[DirCertificate].Size != 0) {
      var (pa, size) = dirs[DirCertificate];
      items.Add(new PeSectionItem("CERTIFICATE", pa, size, size, 0, 0, 0, PeSectionKind.Certificate));
      total = Math.Max(total, (long)pa + size);
    }

    if (debugBeyondSections)
      total = ExtendOverDebugPadding(image, total);

    if (symbolCount > 0 && symbolTable >= optStart) {
      if (symbolCount >= 1 << 24)
        throw new InvalidDataException("Implausible COFF symbol count.");
      var size = (long)symbolCount * 18;
      if (symbolTable + size + 4 > image.Length)
        throw new InvalidDataException("COFF symbol table runs past the end of the file.");
      var strings = BinaryPrimitives.ReadUInt32LittleEndian(image[(int)(symbolTable + size)..]);
      if (strings >= 1 << 28)
        throw new InvalidDataException("Implausible COFF string-table size.");
      size += strings;
      items.Add(new PeSectionItem("COFF_SYMBOLS", symbolTable, size, size, 0, 0, 0, PeSectionKind.CoffSymbols));
      total = Math.Max(total, symbolTable + size);
    }

    // Unclaimed stretches past the 4 KiB header area become [0], [1], ... in file order.
    var limit = (long)GapAlignment;
    var gap = 0;
    foreach (var s in items.OrderBy(s => s.Offset).ThenBy(s => s.Size).ToList()) {
      if (s.Offset > limit) {
        items.Add(new PeSectionItem($"[{gap++}]", limit, s.Offset - limit, s.Offset - limit, 0, 0, 0, PeSectionKind.Gap));
        limit = s.Offset;
      }
      limit = Math.Max(limit, s.Offset + s.PhysicalSize);
    }

    foreach (var s in items)
      if (s.Offset + s.Size > image.Length)
        throw new InvalidDataException($"PE region '{s.Name}' runs past the end of the file.");

    total = Math.Min(total, image.Length);
    if (total < image.Length)
      items.Add(new PeSectionItem("[overlay]", total, image.Length - total, image.Length - total, 0, 0, 0, PeSectionKind.Overlay));
    this.ImageSize = total;
    this.Items = items;
  }

  /// <summary>Debug directory entries whose raw data ends beyond everything seen so far.</summary>
  private static List<PeSectionItem> DebugItems(ReadOnlySpan<byte> image, List<PeSectionItem> sections, (uint Va, uint Size) link, ref long total) {
    const int entrySize = 28;
    var result = new List<PeSectionItem>();
    var count = link.Size / entrySize;
    if (count > 16)
      throw new InvalidDataException("Implausible PE debug directory size.");
    if (count * entrySize != link.Size && count > 1)
      count = 1; // EFI images may declare entry 0 plus inline data.
    var host = sections.FirstOrDefault(s => s.VirtualAddress <= link.Va && (long)link.Va + link.Size <= (long)s.VirtualAddress + s.PhysicalSize);
    if (host == null)
      return result;
    var pa = host.Offset + (link.Va - host.VirtualAddress);
    if (pa + link.Size > image.Length)
      throw new InvalidDataException("PE debug directory runs past the end of the file.");
    for (var i = 0; i < count; ++i) {
      var e = image.Slice((int)(pa + i * entrySize), entrySize);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
      var va = BinaryPrimitives.ReadUInt32LittleEndian(e[20..]);
      var raw = BinaryPrimitives.ReadUInt32LittleEndian(e[24..]);
      if (size == 0 || (long)raw + size <= total)
        continue;
      total = (long)raw + size;
      result.Add(new PeSectionItem($".debug{i}", raw, size, size, size, va, 0, PeSectionKind.Debug));
    }
    return result;
  }

  /// <summary>
  /// Debug data appended after the sections is usually followed by zero padding up to the next 4 KiB
  /// (or 512-byte) boundary; that padding still belongs to the image.
  /// </summary>
  private static long ExtendOverDebugPadding(ReadOnlySpan<byte> image, long total) {
    var alignPos = total & (GapAlignment - 1);
    if (alignPos == 0 || total >= image.Length)
      return total;
    var size = GapAlignment - alignPos;
    var processed = (int)Math.Min(size, image.Length - total);
    var tail = image.Slice((int)total, processed);
    var k = tail.IndexOfAnyExcept((byte)0);
    if (k < 0) k = processed;
    if (processed < size && processed < 100)
      return total + processed;
    if (((total + k) & 0x1FF) == 0 || processed < size)
      return total + k;
    return total;
  }

  private static string SectionName(ReadOnlySpan<byte> raw) {
    var end = raw.IndexOf((byte)0);
    var name = Encoding.Latin1.GetString(end < 0 ? raw : raw[..end]);
    return name.Replace('/', '_').Replace('\\', '_');
  }
}
