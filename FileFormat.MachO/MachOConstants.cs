#pragma warning disable CS1591
namespace FileFormat.MachO;

/// <summary>
/// Magic numbers and load-command identifiers used by the Mach-O format.
/// </summary>
internal static class MachOConstants {

  // Fat (universal) binary magic numbers.
  public const uint FatMagic    = 0xCAFEBABEu; // big-endian, 32-bit records
  public const uint FatCigam    = 0xBEBAFECAu; // byte-swapped
  public const uint FatMagic64  = 0xCAFEBABFu; // big-endian, 64-bit records
  public const uint FatCigam64  = 0xBFBAFECAu; // byte-swapped

  // Single-slice Mach-O magic numbers.
  public const uint MhMagic    = 0xFEEDFACEu; // 32-bit, host-endian
  public const uint MhCigam    = 0xCEFAEDFEu; // 32-bit, swapped
  public const uint MhMagic64  = 0xFEEDFACFu; // 64-bit, host-endian
  public const uint MhCigam64  = 0xCFFAEDFEu; // 64-bit, swapped

  // Load commands we care about.
  public const uint LcSegment        = 0x01;
  public const uint LcSymtab         = 0x02;
  public const uint LcUuid           = 0x1B;
  public const uint LcCodeSignature  = 0x1D;
  public const uint LcSegment64      = 0x19;

  // CPU types (subset; high bit 0x01000000 = 64-bit ABI).
  public const int CpuTypeX86    = 7;
  public const int CpuTypeX8664  = 0x01000007;
  public const int CpuTypeArm    = 12;
  public const int CpuTypeArm64  = 0x0100000C;
  public const int CpuTypePpc    = 18;
  public const int CpuTypePpc64  = 0x01000012;

  public static string CpuTypeName(int cpuType) => cpuType switch {
    CpuTypeX86    => "x86",
    CpuTypeX8664  => "x86_64",
    CpuTypeArm    => "arm",
    CpuTypeArm64  => "arm64",
    CpuTypePpc    => "ppc",
    CpuTypePpc64  => "ppc64",
    _ => $"cpu{cpuType:X8}"
  };
}
