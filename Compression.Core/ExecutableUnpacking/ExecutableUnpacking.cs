#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace Compression.Core.ExecutableUnpacking;

/// <summary>
/// Specifies executable container kind values.
/// </summary>
public enum ExecutableContainerKind {
  /// <summary>
  /// Specifies the pe option.
  /// </summary>
Pe,
  /// <summary>
  /// Specifies the elf option.
  /// </summary>
Elf,
  /// <summary>
  /// Specifies the mach o option.
  /// </summary>
MachO,
  /// <summary>
  /// Specifies the fat mach o option.
  /// </summary>
FatMachO,
  /// <summary>
  /// Specifies the dos mz option.
  /// </summary>
DosMz,
  /// <summary>
  /// Specifies the dos com option.
  /// </summary>
DosCom,
  /// <summary>
  /// Specifies the linear executable option.
  /// </summary>
LinearExecutable,
  /// <summary>
  /// Specifies an unknown or unrecognized value.
  /// </summary>
Unknown,
}

/// <summary>
/// Specifies cpu architecture values.
/// </summary>
public enum CpuArchitecture {
  /// <summary>
  /// Specifies the x 86 option.
  /// </summary>
X86,
  /// <summary>
  /// Specifies the x 64 option.
  /// </summary>
X64,
  /// <summary>
  /// Specifies the arm 32 option.
  /// </summary>
Arm32,
  /// <summary>
  /// Specifies the arm 64 option.
  /// </summary>
Arm64,
  /// <summary>
  /// Specifies the power pc 32 option.
  /// </summary>
PowerPc32,
  /// <summary>
  /// Specifies the power pc 64 option.
  /// </summary>
PowerPc64,
  /// <summary>
  /// Specifies the mips 32 le option.
  /// </summary>
Mips32Le,
  /// <summary>
  /// Specifies the mips 32 be option.
  /// </summary>
Mips32Be,
  /// <summary>
  /// Specifies the mips 64 le option.
  /// </summary>
Mips64Le,
  /// <summary>
  /// Specifies the mips 64 be option.
  /// </summary>
Mips64Be,
  /// <summary>
  /// Specifies an unknown or unrecognized value.
  /// </summary>
Unknown,
}

/// <summary>
/// Specifies executable region flags values.
/// </summary>
[Flags]
public enum ExecutableRegionFlags {
  /// <summary>
  /// Specifies that no option is selected.
  /// </summary>
None = 0,
  /// <summary>
  /// Specifies the read option.
  /// </summary>
Read = 1 << 0,
  /// <summary>
  /// Specifies the write option.
  /// </summary>
Write = 1 << 1,
  /// <summary>
  /// Specifies the execute option.
  /// </summary>
Execute = 1 << 2,
  /// <summary>
  /// Specifies the bss option.
  /// </summary>
Bss = 1 << 3,
}

/// <summary>
/// Specifies executable unpack capabilities values.
/// </summary>
[Flags]
public enum ExecutableUnpackCapabilities {
  /// <summary>
  /// Specifies that no option is selected.
  /// </summary>
None = 0,
  /// <summary>
  /// Specifies the can detect option.
  /// </summary>
CanDetect = 1 << 0,
  /// <summary>
  /// Specifies the can locate payload option.
  /// </summary>
CanLocatePayload = 1 << 1,
  /// <summary>
  /// Specifies the can decompress payload option.
  /// </summary>
CanDecompressPayload = 1 << 2,
  /// <summary>
  /// Specifies the can build memory image option.
  /// </summary>
CanBuildMemoryImage = 1 << 3,
  /// <summary>
  /// Specifies the can rebuild executable option.
  /// </summary>
CanRebuildExecutable = 1 << 4,
  /// <summary>
  /// Specifies the can produce runnable executable option.
  /// </summary>
CanProduceRunnableExecutable = 1 << 5,
  /// <summary>
  /// Specifies the supports pe option.
  /// </summary>
SupportsPe = 1 << 8,
  /// <summary>
  /// Specifies the supports elf option.
  /// </summary>
SupportsElf = 1 << 9,
  /// <summary>
  /// Specifies the supports mach o option.
  /// </summary>
SupportsMachO = 1 << 10,
  /// <summary>
  /// Specifies the supports x 86 option.
  /// </summary>
SupportsX86 = 1 << 16,
  /// <summary>
  /// Specifies the supports x 64 option.
  /// </summary>
SupportsX64 = 1 << 17,
  /// <summary>
  /// Specifies the supports arm 32 option.
  /// </summary>
SupportsArm32 = 1 << 18,
  /// <summary>
  /// Specifies the supports arm 64 option.
  /// </summary>
SupportsArm64 = 1 << 19,
}

/// <summary>
/// Specifies executable unpack level values.
/// </summary>
public enum ExecutableUnpackLevel {
  /// <summary>
  /// Specifies the detection only option.
  /// </summary>
DetectionOnly = 0,
  /// <summary>
  /// Specifies the payload located option.
  /// </summary>
PayloadLocated = 1,
  /// <summary>
  /// Specifies the payload decompressed option.
  /// </summary>
PayloadDecompressed = 2,
  /// <summary>
  /// Specifies the runtime memory image option.
  /// </summary>
RuntimeMemoryImage = 3,
  /// <summary>
  /// Specifies the rebuilt executable option.
  /// </summary>
RebuiltExecutable = 4,
  /// <summary>
  /// Specifies the runnable rebuilt executable option.
  /// </summary>
RunnableRebuiltExecutable = 5,
}

/// <summary>
/// Specifies executable diagnostic code values.
/// </summary>
public enum ExecutableDiagnosticCode {
  /// <summary>
  /// Specifies the not packed executable option.
  /// </summary>
NotPackedExecutable,
  /// <summary>
  /// Specifies the unsupported container option.
  /// </summary>
UnsupportedContainer,
  /// <summary>
  /// Specifies the unsupported architecture option.
  /// </summary>
UnsupportedArchitecture,
  /// <summary>
  /// Specifies the unsupported packer version option.
  /// </summary>
UnsupportedPackerVersion,
  /// <summary>
  /// Specifies the payload not found option.
  /// </summary>
PayloadNotFound,
  /// <summary>
  /// Specifies the unsupported compression method option.
  /// </summary>
UnsupportedCompressionMethod,
  /// <summary>
  /// Specifies the decompression failed option.
  /// </summary>
DecompressionFailed,
  /// <summary>
  /// Specifies the transform not reversible option.
  /// </summary>
TransformNotReversible,
  /// <summary>
  /// Specifies the memory image build failed option.
  /// </summary>
MemoryImageBuildFailed,
  /// <summary>
  /// Specifies the executable rebuild failed option.
  /// </summary>
ExecutableRebuildFailed,
  /// <summary>
  /// Specifies the runnable rebuild not guaranteed option.
  /// </summary>
RunnableRebuildNotGuaranteed,
}

/// <summary>
/// Represents an executable diagnostic.
/// </summary>
public sealed record ExecutableDiagnostic(ExecutableDiagnosticCode Code, string Message, bool IsError = false);

/// <summary>
/// Represents an executable import.
/// </summary>
public sealed record ExecutableImport(string ModuleName, string? SymbolName, ulong Address);

/// <summary>
/// Represents an executable relocation.
/// </summary>
public sealed record ExecutableRelocation(ulong Address, string Type);

/// <summary>
/// Represents an executable region.
/// </summary>
public sealed record ExecutableRegion(
  string Name,
  ulong FileOffset,
  ulong FileSize,
  ulong VirtualAddress,
  ulong VirtualSize,
  ExecutableRegionFlags Flags,
  byte[]? FileBytes,
  byte[]? MemoryBytes
);

/// <summary>
/// Represents an executable image info.
/// </summary>
public sealed record ExecutableImageInfo(
  ExecutableContainerKind Container,
  CpuArchitecture Architecture,
  ulong PreferredBaseAddress,
  ulong EntryPoint,
  IReadOnlyList<ExecutableRegion> Regions,
  IReadOnlyList<ExecutableImport> Imports,
  IReadOnlyList<ExecutableRelocation> Relocations,
  IReadOnlyList<ExecutableDiagnostic> Diagnostics
);

/// <summary>
/// Represents a detection result.
/// </summary>
public sealed record DetectionResult(bool IsMatch, string PackerId, double Confidence, IReadOnlyList<ExecutableDiagnostic> Diagnostics);

/// <summary>
/// Represents a packed executable.
/// </summary>
public sealed record PackedExecutable(
  string PackerId,
  byte[] OriginalImage,
  DetectionResult Detection,
  ExecutableImageInfo? ImageInfo,
  ExecutableUnpackCapabilities Capabilities,
  IReadOnlyDictionary<string, string> Metadata
);

/// <summary>
/// Specifies options for unpack.
/// </summary>
public sealed record UnpackOptions(
  bool StrictRebuild = false,
  bool BestEffort = true,
  long MaximumInputSize = 256L * 1024 * 1024,
  long MaximumDecompressedSize = 512L * 1024 * 1024,
  int MaximumRegionCount = 4096,
  ulong MaximumVirtualAddressSpan = 512UL * 1024 * 1024
);

/// <summary>
/// Represents an unpack artifact.
/// </summary>
public sealed record UnpackArtifact(string Name, byte[] Data, string Method = "stored");

/// <summary>
/// Represents an unpack result.
/// </summary>
public sealed record UnpackResult(
  ExecutableUnpackLevel Level,
  ExecutableUnpackCapabilities Capabilities,
  IReadOnlyList<UnpackArtifact> Artifacts,
  IReadOnlyList<ExecutableDiagnostic> Diagnostics
);

/// <summary>
/// Defines the contract for i executable packer handler.
/// </summary>
public interface IExecutablePackerHandler {
  string Id { get; }
  string DisplayName { get; }
  ExecutableUnpackCapabilities Capabilities { get; }

  DetectionResult Detect(ReadOnlySpan<byte> image);
  PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection);
  UnpackResult Unpack(PackedExecutable packed, UnpackOptions options);
}

/// <summary>
/// Defines the contract for i executable container parser.
/// </summary>
public interface IExecutableContainerParser {
  ExecutableContainerKind Kind { get; }
  bool CanParse(ReadOnlySpan<byte> image);
  ExecutableImageInfo Parse(ReadOnlySpan<byte> image);
}

/// <summary>
/// Defines the contract for i packer transform.
/// </summary>
public interface IPackerTransform {
  string Id { get; }
  bool CanReverse(PackedExecutable packed);
  TransformResult Reverse(byte[] decompressedPayload, PackedExecutable packed);
}

/// <summary>
/// Represents a transform result.
/// </summary>
public sealed record TransformResult(byte[] Payload, IReadOnlyList<ExecutableDiagnostic> Diagnostics);

/// <summary>
/// Represents an executable container parsers.
/// </summary>
public static class ExecutableContainerParsers {
  /// <summary>
  /// Provides the pe value.
  /// </summary>
public static readonly IExecutableContainerParser Pe = new PeParser();
  /// <summary>
  /// Provides the elf value.
  /// </summary>
public static readonly IExecutableContainerParser Elf = new ElfParser();
  /// <summary>
  /// Provides the mach o value.
  /// </summary>
public static readonly IExecutableContainerParser MachO = new MachOParser();
  /// <summary>
  /// Provides the fat mach o value.
  /// </summary>
public static readonly IExecutableContainerParser FatMachO = new FatMachOParser();

  /// <summary>
  /// Parses the best effort from the supplied data.
  /// </summary>
public static ExecutableImageInfo ParseBestEffort(ReadOnlySpan<byte> image) {
    foreach (var parser in new[] { Pe, Elf, FatMachO, MachO })
      if (parser.CanParse(image))
        return parser.Parse(image);

    return new(
      ExecutableContainerKind.Unknown,
      CpuArchitecture.Unknown,
      0,
      0,
      [],
      [],
      [],
      [new(ExecutableDiagnosticCode.UnsupportedContainer, "No supported executable container header was recognized.", true)]);
  }
}

/// <summary>
/// Represents a pe parser.
/// </summary>
public sealed class PeParser : IExecutableContainerParser {
  /// <summary>
  /// Gets the kind.
  /// </summary>
public ExecutableContainerKind Kind => ExecutableContainerKind.Pe;

  /// <summary>
  /// Performs the can parse operation.
  /// </summary>
public bool CanParse(ReadOnlySpan<byte> image) =>
    image.Length >= 0x40 && image[0] == 'M' && image[1] == 'Z' && TryGetPeOffsets(image, out _, out _, out _, out _);

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
public ExecutableImageInfo Parse(ReadOnlySpan<byte> image) {
    var diagnostics = new List<ExecutableDiagnostic>();
    if (!TryGetPeOffsets(image, out var peOffset, out var coffOffset, out var optionalOffset, out var sectionTableOffset))
      return Invalid(ExecutableContainerKind.Pe, "PE header is truncated or missing.");

    var machine = BinaryPrimitives.ReadUInt16LittleEndian(image[(coffOffset + 0)..]);
    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image[(coffOffset + 2)..]);
    var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image[(coffOffset + 16)..]);
    if (sectionCount > 4096 || sectionTableOffset + sectionCount * 40 > image.Length)
      return Invalid(ExecutableContainerKind.Pe, "PE section table extends past EOF.");

    var magic = optionalSize >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(image[optionalOffset..]) : (ushort)0;
    var isPe32Plus = magic == 0x20B;
    var architecture = machine switch {
      0x014C => CpuArchitecture.X86,
      0x8664 => CpuArchitecture.X64,
      0x01C0 or 0x01C2 or 0x01C4 => CpuArchitecture.Arm32,
      0xAA64 => CpuArchitecture.Arm64,
      _ => CpuArchitecture.Unknown,
    };

    var entry = optionalSize >= 20 ? BinaryPrimitives.ReadUInt32LittleEndian(image[(optionalOffset + 16)..]) : 0u;
    ulong imageBase = 0;
    if (optionalSize >= (isPe32Plus ? 32 : 32))
      imageBase = isPe32Plus
        ? BinaryPrimitives.ReadUInt64LittleEndian(image[(optionalOffset + 24)..])
        : BinaryPrimitives.ReadUInt32LittleEndian(image[(optionalOffset + 28)..]);

    var regions = new List<ExecutableRegion>(sectionCount);
    for (var i = 0; i < sectionCount; i++) {
      var off = sectionTableOffset + i * 40;
      var name = ReadFixedAscii(image.Slice(off, 8));
      var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(image[(off + 8)..]);
      var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(image[(off + 12)..]);
      var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(image[(off + 16)..]);
      var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(image[(off + 20)..]);
      var characteristics = BinaryPrimitives.ReadUInt32LittleEndian(image[(off + 36)..]);
      byte[]? fileBytes = null;
      if (rawSize > 0 && rawOffset <= image.Length && rawSize <= image.Length - rawOffset)
        fileBytes = image.Slice((int)rawOffset, (int)rawSize).ToArray();
      else if (rawSize > 0)
        diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedContainer, $"PE section '{name}' raw data extends past EOF."));

      var flags = PeSectionFlags(characteristics);
      if (rawSize == 0 && virtualSize > 0) flags |= ExecutableRegionFlags.Bss;
      regions.Add(new(
        string.IsNullOrEmpty(name) ? $"section_{i:000}" : name,
        rawOffset,
        rawSize,
        virtualAddress,
        virtualSize,
        flags,
        fileBytes,
        fileBytes));
    }

    if (architecture == CpuArchitecture.Unknown)
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedArchitecture, $"Unsupported or unknown PE machine 0x{machine:X4}."));

    _ = peOffset;
    return new(ExecutableContainerKind.Pe, architecture, imageBase, entry, regions, [], [], diagnostics);
  }

  private static ExecutableImageInfo Invalid(ExecutableContainerKind kind, string message) =>
    new(kind, CpuArchitecture.Unknown, 0, 0, [], [], [], [new(ExecutableDiagnosticCode.UnsupportedContainer, message, true)]);

  private static bool TryGetPeOffsets(ReadOnlySpan<byte> image, out int peOffset, out int coffOffset, out int optionalOffset, out int sectionTableOffset) {
    peOffset = coffOffset = optionalOffset = sectionTableOffset = 0;
    if (image.Length < 0x40) return false;
    var eLfanew = BinaryPrimitives.ReadUInt32LittleEndian(image[0x3C..]);
    if (eLfanew > int.MaxValue || eLfanew + 24 > image.Length) return false;
    peOffset = (int)eLfanew;
    if (image[peOffset] != 'P' || image[peOffset + 1] != 'E' || image[peOffset + 2] != 0 || image[peOffset + 3] != 0)
      return false;
    coffOffset = peOffset + 4;
    var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image[(coffOffset + 16)..]);
    optionalOffset = coffOffset + 20;
    sectionTableOffset = optionalOffset + optionalSize;
    return sectionTableOffset <= image.Length;
  }

  private static string ReadFixedAscii(ReadOnlySpan<byte> bytes) {
    var end = bytes.IndexOf((byte)0);
    if (end < 0) end = bytes.Length;
    return Encoding.ASCII.GetString(bytes[..end]);
  }

  private static ExecutableRegionFlags PeSectionFlags(uint characteristics) {
    var flags = ExecutableRegionFlags.None;
    if ((characteristics & 0x40000000u) != 0) flags |= ExecutableRegionFlags.Read;
    if ((characteristics & 0x80000000u) != 0) flags |= ExecutableRegionFlags.Write;
    if ((characteristics & 0x20000000u) != 0) flags |= ExecutableRegionFlags.Execute;
    return flags;
  }
}

/// <summary>
/// Represents an elf parser.
/// </summary>
public sealed class ElfParser : IExecutableContainerParser {
  /// <summary>
  /// Gets the kind.
  /// </summary>
public ExecutableContainerKind Kind => ExecutableContainerKind.Elf;

  /// <summary>
  /// Performs the can parse operation.
  /// </summary>
public bool CanParse(ReadOnlySpan<byte> image) =>
    image.Length >= 0x34 && image[0] == 0x7F && image[1] == 'E' && image[2] == 'L' && image[3] == 'F';

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
public ExecutableImageInfo Parse(ReadOnlySpan<byte> image) {
    if (!CanParse(image))
      return new(ExecutableContainerKind.Elf, CpuArchitecture.Unknown, 0, 0, [], [], [], [new(ExecutableDiagnosticCode.UnsupportedContainer, "Not an ELF image.", true)]);

    var is64 = image[4] == 2;
    var le = image[5] == 1;
    var machine = ReadU16(image, 18, le);
    var architecture = machine switch {
      3 => CpuArchitecture.X86,
      62 => CpuArchitecture.X64,
      40 => CpuArchitecture.Arm32,
      183 => CpuArchitecture.Arm64,
      20 => CpuArchitecture.PowerPc32,
      21 => CpuArchitecture.PowerPc64,
      8 when le => CpuArchitecture.Mips32Le,
      8 => CpuArchitecture.Mips32Be,
      _ => CpuArchitecture.Unknown,
    };

    var entry = is64 ? ReadU64(image, 0x18, le) : ReadU32(image, 0x18, le);
    var phoff = is64 ? ReadU64(image, 0x20, le) : ReadU32(image, 0x1C, le);
    var phentsize = is64 ? ReadU16(image, 0x36, le) : ReadU16(image, 0x2A, le);
    var phnum = is64 ? ReadU16(image, 0x38, le) : ReadU16(image, 0x2C, le);
    var regions = new List<ExecutableRegion>();
    var diagnostics = new List<ExecutableDiagnostic>();

    if (phoff > 0 && phentsize > 0 && phnum < 4096 && phoff + (ulong)phentsize * phnum <= (ulong)image.Length)
      for (var i = 0; i < phnum; i++) {
        var off = (int)(phoff + (ulong)i * phentsize);
        var type = ReadU32(image, off, le);
        if (type != 1) continue;

        ulong flagsRaw;
        ulong fileOffset;
        ulong virtualAddress;
        ulong fileSize;
        ulong memorySize;
        if (is64) {
          flagsRaw = ReadU32(image, off + 4, le);
          fileOffset = ReadU64(image, off + 8, le);
          virtualAddress = ReadU64(image, off + 16, le);
          fileSize = ReadU64(image, off + 32, le);
          memorySize = ReadU64(image, off + 40, le);
        } else {
          fileOffset = ReadU32(image, off + 4, le);
          virtualAddress = ReadU32(image, off + 8, le);
          fileSize = ReadU32(image, off + 16, le);
          memorySize = ReadU32(image, off + 20, le);
          flagsRaw = ReadU32(image, off + 24, le);
        }

        byte[]? fileBytes = null;
        if (fileSize > 0 && fileOffset <= (ulong)image.Length && fileSize <= (ulong)image.Length - fileOffset)
          fileBytes = image.Slice((int)fileOffset, (int)fileSize).ToArray();
        regions.Add(new($"PT_LOAD_{regions.Count:000}", fileOffset, fileSize, virtualAddress, memorySize, ElfFlags((uint)flagsRaw), fileBytes, fileBytes));
      }
    else
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedContainer, "ELF program header table is absent or invalid."));

    if (architecture == CpuArchitecture.Unknown)
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedArchitecture, $"Unsupported or unknown ELF machine {machine}."));

    return new(ExecutableContainerKind.Elf, architecture, 0, entry, regions, [], [], diagnostics);
  }

  private static ExecutableRegionFlags ElfFlags(uint flags) {
    var result = ExecutableRegionFlags.None;
    if ((flags & 4) != 0) result |= ExecutableRegionFlags.Read;
    if ((flags & 2) != 0) result |= ExecutableRegionFlags.Write;
    if ((flags & 1) != 0) result |= ExecutableRegionFlags.Execute;
    return result;
  }

  internal static ushort ReadU16(ReadOnlySpan<byte> b, int off, bool le) =>
    le ? BinaryPrimitives.ReadUInt16LittleEndian(b[off..]) : BinaryPrimitives.ReadUInt16BigEndian(b[off..]);

  internal static uint ReadU32(ReadOnlySpan<byte> b, int off, bool le) =>
    le ? BinaryPrimitives.ReadUInt32LittleEndian(b[off..]) : BinaryPrimitives.ReadUInt32BigEndian(b[off..]);

  internal static ulong ReadU64(ReadOnlySpan<byte> b, int off, bool le) =>
    le ? BinaryPrimitives.ReadUInt64LittleEndian(b[off..]) : BinaryPrimitives.ReadUInt64BigEndian(b[off..]);
}

/// <summary>
/// Represents a mach o parser.
/// </summary>
public sealed class MachOParser : IExecutableContainerParser {
  /// <summary>
  /// Gets the kind.
  /// </summary>
public ExecutableContainerKind Kind => ExecutableContainerKind.MachO;

  /// <summary>
  /// Performs the can parse operation.
  /// </summary>
public bool CanParse(ReadOnlySpan<byte> image) {
    if (image.Length < 4) return false;
    var le = BinaryPrimitives.ReadUInt32LittleEndian(image);
    var be = BinaryPrimitives.ReadUInt32BigEndian(image);
    return le is 0xFEEDFACEu or 0xFEEDFACFu || be is 0xFEEDFACEu or 0xFEEDFACFu;
  }

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
public ExecutableImageInfo Parse(ReadOnlySpan<byte> image) {
    if (!CanParse(image))
      return new(ExecutableContainerKind.MachO, CpuArchitecture.Unknown, 0, 0, [], [], [], [new(ExecutableDiagnosticCode.UnsupportedContainer, "Not a Mach-O image.", true)]);

    var leMagic = BinaryPrimitives.ReadUInt32LittleEndian(image);
    var is64 = leMagic == 0xFEEDFACFu || BinaryPrimitives.ReadUInt32BigEndian(image) == 0xFEEDFACFu;
    var le = leMagic is 0xFEEDFACEu or 0xFEEDFACFu;
    var headerSize = is64 ? 32 : 28;
    if (image.Length < headerSize)
      return new(ExecutableContainerKind.MachO, CpuArchitecture.Unknown, 0, 0, [], [], [], [new(ExecutableDiagnosticCode.UnsupportedContainer, "Mach-O header is truncated.", true)]);

    var cpuType = (int)ElfParser.ReadU32(image, 4, le);
    var architecture = MachOCpu.Architecture(cpuType);
    var ncmds = ElfParser.ReadU32(image, 16, le);
    var sizeofcmds = ElfParser.ReadU32(image, 20, le);
    var regions = new List<ExecutableRegion>();
    var diagnostics = new List<ExecutableDiagnostic>();
    var commandsEnd = headerSize + (long)sizeofcmds;
    if (ncmds > 0x10000 || commandsEnd > image.Length)
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedContainer, "Mach-O load commands extend past EOF.", true));
    else {
      var cursor = headerSize;
      for (var i = 0; i < ncmds && cursor + 8 <= commandsEnd; i++) {
        var cmd = ElfParser.ReadU32(image, cursor, le);
        var cmdSize = ElfParser.ReadU32(image, cursor + 4, le);
        if (cmdSize < 8 || cursor + cmdSize > commandsEnd) break;
        if (cmd is 1 or 0x19) {
          var segment64 = cmd == 0x19;
          var name = ReadFixedAscii(image.Slice(cursor + 8, 16));
          ulong vmaddr;
          ulong vmsize;
          ulong fileoff;
          ulong filesize;
          uint initprot;
          if (segment64) {
            vmaddr = ElfParser.ReadU64(image, cursor + 24, le);
            vmsize = ElfParser.ReadU64(image, cursor + 32, le);
            fileoff = ElfParser.ReadU64(image, cursor + 40, le);
            filesize = ElfParser.ReadU64(image, cursor + 48, le);
            initprot = ElfParser.ReadU32(image, cursor + 60, le);
          } else {
            vmaddr = ElfParser.ReadU32(image, cursor + 24, le);
            vmsize = ElfParser.ReadU32(image, cursor + 28, le);
            fileoff = ElfParser.ReadU32(image, cursor + 32, le);
            filesize = ElfParser.ReadU32(image, cursor + 36, le);
            initprot = ElfParser.ReadU32(image, cursor + 44, le);
          }
          byte[]? fileBytes = null;
          if (filesize > 0 && fileoff <= (ulong)image.Length && filesize <= (ulong)image.Length - fileoff)
            fileBytes = image.Slice((int)fileoff, (int)filesize).ToArray();
          regions.Add(new(string.IsNullOrEmpty(name) ? $"segment_{regions.Count:000}" : name, fileoff, filesize, vmaddr, vmsize, MachProtFlags(initprot), fileBytes, fileBytes));
        }
        cursor += (int)cmdSize;
      }
    }
    if (architecture == CpuArchitecture.Unknown)
      diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedArchitecture, $"Unsupported or unknown Mach-O CPU type {cpuType}."));
    return new(ExecutableContainerKind.MachO, architecture, 0, 0, regions, [], [], diagnostics);
  }

  private static string ReadFixedAscii(ReadOnlySpan<byte> bytes) {
    var end = bytes.IndexOf((byte)0);
    if (end < 0) end = bytes.Length;
    return Encoding.ASCII.GetString(bytes[..end]);
  }

  private static ExecutableRegionFlags MachProtFlags(uint prot) {
    var result = ExecutableRegionFlags.None;
    if ((prot & 1) != 0) result |= ExecutableRegionFlags.Read;
    if ((prot & 2) != 0) result |= ExecutableRegionFlags.Write;
    if ((prot & 4) != 0) result |= ExecutableRegionFlags.Execute;
    return result;
  }
}

/// <summary>
/// Represents a fat mach o parser.
/// </summary>
public sealed class FatMachOParser : IExecutableContainerParser {
  /// <summary>
  /// Gets the kind.
  /// </summary>
public ExecutableContainerKind Kind => ExecutableContainerKind.FatMachO;

  /// <summary>
  /// Performs the can parse operation.
  /// </summary>
public bool CanParse(ReadOnlySpan<byte> image) {
    if (image.Length < 8) return false;
    var magic = BinaryPrimitives.ReadUInt32BigEndian(image);
    return magic is 0xCAFEBABEu or 0xBEBAFECAu or 0xCAFEBABFu or 0xBFBAFECAu;
  }

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
public ExecutableImageInfo Parse(ReadOnlySpan<byte> image) {
    if (!CanParse(image))
      return new(ExecutableContainerKind.FatMachO, CpuArchitecture.Unknown, 0, 0, [], [], [], [new(ExecutableDiagnosticCode.UnsupportedContainer, "Not a fat Mach-O image.", true)]);

    var is64 = BinaryPrimitives.ReadUInt32BigEndian(image) is 0xCAFEBABFu or 0xBFBAFECAu;
    var nfat = BinaryPrimitives.ReadUInt32BigEndian(image[4..]);
    var recSize = is64 ? 32 : 20;
    var end = 8 + (long)nfat * recSize;
    if (nfat > 4096 || end > image.Length)
      return new(ExecutableContainerKind.FatMachO, CpuArchitecture.Unknown, 0, 0, [], [], [], [new(ExecutableDiagnosticCode.UnsupportedContainer, "Fat Mach-O slice table extends past EOF.", true)]);

    var regions = new List<ExecutableRegion>((int)nfat);
    for (var i = 0; i < nfat; i++) {
      var off = 8 + i * recSize;
      var cpu = BinaryPrimitives.ReadInt32BigEndian(image[off..]);
      ulong sliceOffset;
      ulong sliceSize;
      if (is64) {
        sliceOffset = BinaryPrimitives.ReadUInt64BigEndian(image[(off + 8)..]);
        sliceSize = BinaryPrimitives.ReadUInt64BigEndian(image[(off + 16)..]);
      } else {
        sliceOffset = BinaryPrimitives.ReadUInt32BigEndian(image[(off + 8)..]);
        sliceSize = BinaryPrimitives.ReadUInt32BigEndian(image[(off + 12)..]);
      }
      byte[]? fileBytes = null;
      if (sliceOffset <= (ulong)image.Length && sliceSize <= (ulong)image.Length - sliceOffset)
        fileBytes = image.Slice((int)sliceOffset, (int)sliceSize).ToArray();
      regions.Add(new($"slice_{CpuName(cpu)}", sliceOffset, sliceSize, 0, sliceSize, ExecutableRegionFlags.Read | ExecutableRegionFlags.Execute, fileBytes, fileBytes));
    }

    return new(ExecutableContainerKind.FatMachO, CpuArchitecture.Unknown, 0, 0, regions, [], [], []);
  }

  private static string CpuName(int cpu) => MachOCpu.Name(cpu);
}

internal static class MachOCpu {
  private const int Abi64 = 0x01000000;
  private const int TypeMask = 0x00FFFFFF;

  public static CpuArchitecture Architecture(int cpu) {
    var is64 = (cpu & Abi64) != 0;
    return (cpu & TypeMask, is64) switch {
      (7, false) => CpuArchitecture.X86,
      (7, true) => CpuArchitecture.X64,
      (12, false) => CpuArchitecture.Arm32,
      (12, true) => CpuArchitecture.Arm64,
      (18, false) => CpuArchitecture.PowerPc32,
      (18, true) => CpuArchitecture.PowerPc64,
      _ => CpuArchitecture.Unknown,
    };
  }

  public static string Name(int cpu) {
    var is64 = (cpu & Abi64) != 0;
    return (cpu & TypeMask, is64) switch {
      (7, false) => "x86",
      (7, true) => "x86_64",
      (12, false) => "arm",
      (12, true) => "arm64",
      (18, false) => "ppc",
      (18, true) => "ppc64",
      _ => $"cpu_{cpu:X8}",
    };
  }
}

/// <summary>
/// Represents an executable memory image builder.
/// </summary>
public static class ExecutableMemoryImageBuilder {
  /// <summary>
  /// Performs the build operation.
  /// </summary>
public static (byte[]? Image, IReadOnlyList<ExecutableRegion> Regions, IReadOnlyList<ExecutableDiagnostic> Diagnostics) Build(
    ExecutableImageInfo info,
    byte[]? replacementPayload = null,
    string? replacementTargetRegionName = null,
    UnpackOptions? options = null) {
    options ??= new();
    var diagnostics = new List<ExecutableDiagnostic>();
    if (info.Regions.Count == 0)
      return (null, [], [new(ExecutableDiagnosticCode.MemoryImageBuildFailed, "No executable regions are available to map.", true)]);
    if (info.Regions.Count > options.MaximumRegionCount)
      return (null, [], [new(ExecutableDiagnosticCode.MemoryImageBuildFailed, "Executable region count exceeds configured limit.", true)]);

    var mapped = new List<ExecutableRegion>(info.Regions.Count);
    var low = info.Regions.Where(r => r.VirtualSize > 0).Min(r => r.VirtualAddress);
    var high = info.Regions.Where(r => r.VirtualSize > 0).Max(r => CheckedEnd(r.VirtualAddress, r.VirtualSize));
    if (high <= low || high - low > options.MaximumVirtualAddressSpan)
      return (null, [], [new(ExecutableDiagnosticCode.MemoryImageBuildFailed, "Executable virtual address span exceeds configured limit.", true)]);

    var image = new byte[high - low];
    foreach (var region in info.Regions) {
      var memorySize = (int)Math.Min(region.VirtualSize, (ulong)int.MaxValue);
      var memoryBytes = new byte[memorySize];
      var source = region.MemoryBytes ?? region.FileBytes ?? [];
      if (replacementPayload != null && IsReplacementTarget(region, replacementTargetRegionName))
        source = replacementPayload;
      source.AsSpan(0, Math.Min(source.Length, memoryBytes.Length)).CopyTo(memoryBytes);
      var dest = (int)(region.VirtualAddress - low);
      memoryBytes.AsSpan(0, Math.Min(memoryBytes.Length, image.Length - dest)).CopyTo(image.AsSpan(dest));
      mapped.Add(region with { MemoryBytes = memoryBytes });
    }

    return (image, mapped, diagnostics);
  }

  private static bool IsReplacementTarget(ExecutableRegion region, string? targetName) =>
    targetName == null
      ? region.Flags.HasFlag(ExecutableRegionFlags.Bss) || region.FileSize == 0
      : string.Equals(region.Name, targetName, StringComparison.OrdinalIgnoreCase);

  private static ulong CheckedEnd(ulong start, ulong size) {
    try {
      checked { return start + size; }
    } catch (OverflowException) {
      return ulong.MaxValue;
    }
  }
}

/// <summary>
/// Represents a pe rebuilder.
/// </summary>
public static class PeRebuilder {
  /// <summary>
  /// Performs the rebuild synthetic operation.
  /// </summary>
public static byte[] RebuildSynthetic(ExecutableImageInfo original, byte[] payload) {
    const uint sectionAlignment = 0x1000;
    const uint fileAlignment = 0x200;
    const int peOffset = 0x80;
    const int optionalHeaderSize = 0xE0;
    const int sectionTableOffset = peOffset + 4 + 20 + optionalHeaderSize;
    const int headersSize = 0x200;
    var rawSize = Align((uint)Math.Max(payload.Length, 1), fileAlignment);
    var imageSize = Align(0x1000 + Math.Max((uint)payload.Length, 1), sectionAlignment);
    var result = new byte[headersSize + rawSize];

    result[0] = (byte)'M';
    result[1] = (byte)'Z';
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x3C), peOffset);
    result[peOffset] = (byte)'P';
    result[peOffset + 1] = (byte)'E';
    result[peOffset + 2] = 0;
    result[peOffset + 3] = 0;

    var machine = original.Architecture == CpuArchitecture.X64 ? (ushort)0x8664 : (ushort)0x014C;
    var is64 = original.Architecture == CpuArchitecture.X64;
    var coff = peOffset + 4;
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(coff), machine);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(coff + 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(coff + 16), optionalHeaderSize);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(coff + 18), 0x0102);

    var opt = coff + 20;
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(opt), is64 ? (ushort)0x20B : (ushort)0x10B);
    result[opt + 2] = 14;
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(opt + 4), rawSize);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(opt + 16), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(opt + 20), 0x1000);
    if (is64)
      BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(opt + 24), original.PreferredBaseAddress == 0 ? 0x140000000UL : original.PreferredBaseAddress);
    else {
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(opt + 24), 0x1000);
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(opt + 28), (uint)(original.PreferredBaseAddress == 0 ? 0x400000 : original.PreferredBaseAddress));
    }
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(opt + 32), sectionAlignment);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(opt + 36), fileAlignment);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(opt + 40), 6);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(opt + 48), 6);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(opt + 56), imageSize);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(opt + 60), headersSize);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(opt + 68), 3);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(opt + 70), is64 ? (ushort)0x8160 : (ushort)0x8140);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(opt + (is64 ? 108 : 92)), 16);

    Encoding.ASCII.GetBytes(".text\0\0\0").CopyTo(result.AsSpan(sectionTableOffset, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(sectionTableOffset + 8), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(sectionTableOffset + 12), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(sectionTableOffset + 16), rawSize);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(sectionTableOffset + 20), headersSize);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(sectionTableOffset + 36), 0x60000020);

    payload.CopyTo(result.AsSpan(headersSize));
    return result;
  }

  private static uint Align(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;
}

/// <summary>
/// Represents an executable diagnostics json.
/// </summary>
public static class ExecutableDiagnosticsJson {
  /// <summary>
  /// Performs the build operation.
  /// </summary>
public static byte[] Build(string packer, ExecutableImageInfo? imageInfo, UnpackResult result) {
    var sb = new StringBuilder();
    sb.Append("{\n");
    AppendJson(sb, "packer", packer, comma: true, indent: 2);
    AppendJson(sb, "container", imageInfo?.Container.ToString().ToLowerInvariant() ?? "unknown", comma: true, indent: 2);
    AppendJson(sb, "architecture", imageInfo?.Architecture.ToString().ToLowerInvariant() ?? "unknown", comma: true, indent: 2);
    AppendJson(sb, "capabilityLevel", result.Level.ToString(), comma: true, indent: 2);
    sb.Append("  \"canRebuildExecutable\": ").Append(result.Capabilities.HasFlag(ExecutableUnpackCapabilities.CanRebuildExecutable).ToString().ToLowerInvariant()).Append(",\n");
    sb.Append("  \"warnings\": [");
    var warnings = result.Diagnostics.Where(d => !d.IsError).ToList();
    for (var i = 0; i < warnings.Count; i++) {
      if (i > 0) sb.Append(", ");
      sb.Append('"').Append(Escape(warnings[i].Message)).Append('"');
    }
    sb.Append("],\n");
    sb.Append("  \"errors\": [");
    var errors = result.Diagnostics.Where(d => d.IsError).ToList();
    for (var i = 0; i < errors.Count; i++) {
      if (i > 0) sb.Append(", ");
      sb.Append('"').Append(Escape(errors[i].Message)).Append('"');
    }
    sb.Append("],\n");
    sb.Append("  \"outputs\": [");
    for (var i = 0; i < result.Artifacts.Count; i++) {
      if (i > 0) sb.Append(", ");
      sb.Append('"').Append(Escape(result.Artifacts[i].Name)).Append('"');
    }
    sb.Append("]\n}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static void AppendJson(StringBuilder sb, string name, string value, bool comma, int indent) {
    sb.Append(' ', indent).Append('"').Append(name).Append("\": \"").Append(Escape(value)).Append('"');
    if (comma) sb.Append(',');
    sb.Append('\n');
  }

  private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
