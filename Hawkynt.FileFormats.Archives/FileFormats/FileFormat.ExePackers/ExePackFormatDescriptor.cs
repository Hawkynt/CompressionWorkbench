#pragma warning disable CS1591
namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for DOS executables packed by Microsoft EXEPACK (EXEPACK.EXE or
/// LINK /EXEPACK): lists the restored program as <c>unpacked.exe</c>. Restoring is done by
/// <see cref="ExePackUnpacker"/>; on files from EXEPACK 4.06 the result equals UNP 4.11's apart
/// from the minimum allocation, which keeps the packed program's total memory.
///
/// References:
/// <list type="bullet">
///   <item><description><c>http://fileformats.archiveteam.org/wiki/EXEPACK</c> — format overview</description></item>
///   <item><description><c>https://github.com/jsummers/deark</c> — modules/exepack.c, the converted decoder</description></item>
/// </list>
/// </summary>
public sealed class ExePackFormatDescriptor : DosPackedExeDescriptor {
  public override string Id => "ExePack";
  public override string DisplayName => "EXEPACK (DOS exe)";
  public override string Description =>
    "Microsoft EXEPACK-packed DOS executable (EXEPACK.EXE 3.x/4.x, LINK /EXEPACK): lists the " +
    "restored program.";
  protected override IDosExeUnpacker Unpacker => ExePackUnpacker.Instance;
  protected override string MetadataSection => "exepack";
}
