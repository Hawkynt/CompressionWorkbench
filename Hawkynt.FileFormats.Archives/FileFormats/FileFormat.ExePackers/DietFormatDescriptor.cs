#pragma warning disable CS1591
namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for DOS executables and COM files packed by DIET (Teddy Matsumoto,
/// 1991-1992): lists the restored program as <c>unpacked.exe</c> or <c>unpacked.com</c>.
/// Restoring is done by <see cref="DietUnpacker"/>; on files from DIET 1.45f the result equals
/// UNP 4.11's byte for byte.
///
/// References:
/// <list type="bullet">
///   <item><description><c>http://fileformats.archiveteam.org/wiki/DIET</c> — format overview</description></item>
///   <item><description><c>https://github.com/jsummers/deark</c> — modules/diet.c, the converted decoder</description></item>
/// </list>
/// </summary>
public sealed class DietFormatDescriptor : DosPackedExeDescriptor {
  public override string Id => "Diet";
  public override string DisplayName => "DIET (DOS exe)";
  public override string Description =>
    "DIET-packed DOS executable or COM file (versions 1.00 to 1.45f): lists the restored program.";
  protected override IDosExeUnpacker Unpacker => DietUnpacker.Instance;
  protected override string MetadataSection => "diet";
}
