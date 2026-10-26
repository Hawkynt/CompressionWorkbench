using System.Buffers.Binary;
using System.Text;
using FileFormat.PeSections;

namespace FileFormat.Te;

/// <summary>
/// Reads a Terse Executable (TE) image — the stripped-header PE variant UEFI PEI modules use.
/// </summary>
/// <remarks>
/// Layout per the UEFI Platform Initialization specification, Vol. 1, "TE Image" (EFI_TE_IMAGE_HEADER):
/// <code>
/// 0  "VZ"                       8  AddressOfEntryPoint u32   24 DataDirectory[0] base relocations (VA, Size)
/// 2  Machine u16               12  BaseOfCode u32            32 DataDirectory[1] debug (VA, Size)
/// 4  NumberOfSections u8       16  ImageBase u64             40 section headers (PE layout, 40 bytes each)
/// 5  Subsystem u8
/// 6  StrippedSize u16
/// </code>
/// The PE headers that were stripped are <c>StrippedSize</c> bytes long, so a section's file offset is
/// <c>PointerToRawData − StrippedSize + 40</c>. Validation and extraction sizes are converted from
/// 7-Zip's TE handler in <c>CPP/7zip/Archive/PeHandler.cpp</c> (Igor Pavlov, LGPL-2.1-or-later): at most
/// 32 sections, a known machine and subsystem, directories under 256 MiB, and each section extracting
/// <c>min(VirtualSize, SizeOfRawData)</c> bytes.
/// </remarks>
public sealed class TeReader {

  /// <summary>Size of <c>EFI_TE_IMAGE_HEADER</c>.</summary>
  public const int HeaderSize = 40;

  private const int SectionHeaderSize = 40;
  private const int MaxSections = 32;

  /// <summary>Machine types 7-Zip's PE/TE handlers accept (IMAGE_FILE_MACHINE_*).</summary>
  internal static readonly IReadOnlyDictionary<ushort, string> Machines = new Dictionary<ushort, string> {
    [0x014C] = "x86", [0x014D] = "I860", [0x0162] = "MIPS-R3000", [0x0166] = "MIPS-R4000", [0x0168] = "MIPS-R10000",
    [0x0169] = "MIPS-V2", [0x0184] = "Alpha", [0x01A2] = "SH3", [0x01A3] = "SH3-DSP", [0x01A4] = "SH3E", [0x01A6] = "SH4",
    [0x01A8] = "SH5", [0x01C0] = "ARM", [0x01C2] = "ARM-Thumb", [0x01C4] = "ARM-NT", [0x01D3] = "AM33", [0x01F0] = "PPC",
    [0x01F1] = "PPC-FP", [0x01F2] = "PPC-BE", [0x0200] = "IA-64", [0x0266] = "MIPS-16", [0x0284] = "Alpha-64",
    [0x0366] = "MIPS-FPU", [0x0466] = "MIPS-FPU16", [0x0520] = "TriCore", [0x0CEF] = "CEF", [0x0EBC] = "EFI",
    [0x5032] = "RISCV32", [0x5064] = "RISCV64", [0x6232] = "LOONGARCH32", [0x6264] = "LOONGARCH64", [0x8664] = "x64",
    [0x9041] = "M32R", [0xA641] = "ARM64EC", [0xA64E] = "ARM64X", [0xAA64] = "ARM64", [0xC0EE] = "CEE",
  };

  /// <summary>Subsystem names by value (IMAGE_SUBSYSTEM_*); null marks a value 7-Zip rejects.</summary>
  internal static readonly string?[] Subsystems = [
    "Unknown", "Native", "Windows GUI", "Windows CUI", null, "OS2", null, "Posix", "Win9x", "Windows CE",
    "EFI", "EFI Boot", "EFI Runtime", "EFI ROM", "XBOX", null, "Windows Boot", "XBOX Catalog",
  ];

  /// <summary>COFF machine type.</summary>
  public ushort Machine { get; }

  /// <summary>Machine name.</summary>
  public string MachineName => Machines[this.Machine];

  /// <summary>Subsystem.</summary>
  public byte Subsystem { get; }

  /// <summary>Subsystem name.</summary>
  public string SubsystemName => Subsystems[this.Subsystem]!;

  /// <summary>Bytes of PE headers the TE header replaced.</summary>
  public ushort StrippedSize { get; }

  /// <summary>Entry point RVA.</summary>
  public uint AddressOfEntryPoint { get; }

  /// <summary>Base of code RVA.</summary>
  public uint BaseOfCode { get; }

  /// <summary>Preferred load address.</summary>
  public ulong ImageBase { get; }

  /// <summary>Base-relocation directory (VA, size).</summary>
  public (uint Va, uint Size) RelocationDirectory { get; }

  /// <summary>Debug directory (VA, size).</summary>
  public (uint Va, uint Size) DebugDirectory { get; }

  /// <summary>End of the furthest section: the image's physical size.</summary>
  public long ImageSize { get; }

  /// <summary>Sections in header order, offsets already converted to file offsets.</summary>
  public IReadOnlyList<PeSectionItem> Sections { get; }

  /// <summary>Parses <paramref name="image"/>.</summary>
  /// <exception cref="InvalidDataException">Not a TE image, or a section lies outside it.</exception>
  public TeReader(ReadOnlySpan<byte> image) {
    if (image.Length < HeaderSize || image[0] != (byte)'V' || image[1] != (byte)'Z')
      throw new InvalidDataException("Not a TE image (missing VZ signature).");
    this.Machine = BinaryPrimitives.ReadUInt16LittleEndian(image[2..]);
    var count = image[4];
    this.Subsystem = image[5];
    this.StrippedSize = BinaryPrimitives.ReadUInt16LittleEndian(image[6..]);
    this.AddressOfEntryPoint = BinaryPrimitives.ReadUInt32LittleEndian(image[8..]);
    this.BaseOfCode = BinaryPrimitives.ReadUInt32LittleEndian(image[12..]);
    this.ImageBase = BinaryPrimitives.ReadUInt64LittleEndian(image[16..]);
    this.RelocationDirectory = (BinaryPrimitives.ReadUInt32LittleEndian(image[24..]), BinaryPrimitives.ReadUInt32LittleEndian(image[28..]));
    this.DebugDirectory = (BinaryPrimitives.ReadUInt32LittleEndian(image[32..]), BinaryPrimitives.ReadUInt32LittleEndian(image[36..]));
    if (count > MaxSections)
      throw new InvalidDataException($"TE image declares {count} sections (at most {MaxSections}).");
    if (!Machines.ContainsKey(this.Machine))
      throw new InvalidDataException($"Unknown TE machine type 0x{this.Machine:X4}.");
    if (this.Subsystem >= Subsystems.Length || Subsystems[this.Subsystem] == null)
      throw new InvalidDataException($"Unknown TE subsystem {this.Subsystem}.");
    if (this.RelocationDirectory.Size >= 1u << 28 || this.DebugDirectory.Size >= 1u << 28)
      throw new InvalidDataException("Implausible TE data-directory size.");

    var headersEnd = HeaderSize + count * SectionHeaderSize;
    if (headersEnd > image.Length)
      throw new InvalidDataException("TE section table runs past the end of the file.");
    var sections = new List<PeSectionItem>(count);
    long total = headersEnd;
    for (var i = 0; i < count; ++i) {
      var h = image.Slice(HeaderSize + i * SectionHeaderSize, SectionHeaderSize);
      var end = h[..8].IndexOf((byte)0);
      var name = Encoding.Latin1.GetString(end < 0 ? h[..8] : h[..end]);
      var vsize = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]);
      var va = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
      var psize = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
      var rawPa = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(h[36..]);
      if (rawPa < this.StrippedSize)
        throw new InvalidDataException($"TE section '{name}' starts inside the stripped headers.");
      var pa = (long)rawPa - this.StrippedSize + HeaderSize;
      if (pa < headersEnd || pa > 1L << 30 || psize > 1u << 30)
        throw new InvalidDataException($"TE section '{name}' overlaps the headers or is implausibly large.");
      long extract = vsize != 0 && vsize < psize ? vsize : psize;
      if (pa + extract > image.Length)
        throw new InvalidDataException($"TE section '{name}' runs past the end of the file.");
      sections.Add(new PeSectionItem(name, pa, extract, psize, vsize, va, flags, PeSectionKind.Section));
      total = Math.Max(total, pa + psize);
    }
    this.Sections = sections;
    this.ImageSize = total;
  }
}
