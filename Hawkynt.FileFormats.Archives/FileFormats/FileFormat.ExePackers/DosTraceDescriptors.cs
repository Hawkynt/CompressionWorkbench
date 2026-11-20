#pragma warning disable CS1591
namespace FileFormat.ExePackers;

/// <summary>
/// aPACK-packed DOS executables (Joergen Ibsen, 1997-2009): lists the restored program, recovered
/// by running aPACK's stub in the built-in 8086 emulator (<see cref="DosTracer"/>).
/// Reference: <c>https://ibsensoftware.com/products_aPACK.html</c>.
/// </summary>
public sealed class ApackFormatDescriptor : DosPackedExeDescriptor {
  public override string Id => "Apack";
  public override string DisplayName => "aPACK (DOS exe)";
  public override string Description =>
    "aPACK-packed DOS executable (standard, -s and tiny stubs): lists the restored program, " +
    "recovered by running the stub in an 8086 emulator.";
  protected override IDosExeUnpacker Unpacker => ApackUnpacker.Instance;
  protected override string MetadataSection => "apack";
}

/// <summary>ExeLITE-packed DOS executables: lists the restored program (8086 emulation of the stub).</summary>
public sealed class ExeLiteFormatDescriptor : DosPackedExeDescriptor {
  public override string Id => "ExeLite";
  public override string DisplayName => "ExeLITE (DOS exe)";
  public override string Description =>
    "ExeLITE-packed DOS executable: lists the restored program, recovered by running the stub " +
    "in an 8086 emulator; the original header the stub keeps is restored with it.";
  protected override IDosExeUnpacker Unpacker => ExeLiteUnpacker.Instance;
  protected override string MetadataSection => "exelite";
}

/// <summary>TINYPROG-packed DOS executables (Tranzoa): lists the restored program (8086 emulation of the stub).</summary>
public sealed class TinyProgFormatDescriptor : DosPackedExeDescriptor {
  public override string Id => "TinyProg";
  public override string DisplayName => "TINYPROG (DOS exe)";
  public override string Description =>
    "TINYPROG-packed DOS executable: lists the restored program, recovered by running the stub " +
    "in an 8086 emulator.";
  protected override IDosExeUnpacker Unpacker => TinyProgUnpacker.Instance;
  protected override string MetadataSection => "tinyprog";
}

/// <summary>WWPACK-packed DOS executables: lists the restored program (8086 emulation of the stub).</summary>
public sealed class WwpackFormatDescriptor : DosPackedExeDescriptor {
  public override string Id => "Wwpack";
  public override string DisplayName => "WWPACK (DOS exe)";
  public override string Description =>
    "WWPACK-packed DOS executable (P command): lists the restored program, recovered by running " +
    "the stub in an 8086 emulator.";
  protected override IDosExeUnpacker Unpacker => WwpackUnpacker.Instance;
  protected override string MetadataSection => "wwpack";
}

/// <summary>PGMPAK-packed DOS executables (Todor Todorov): lists the restored program (8086 emulation of the stub).</summary>
public sealed class PgmpakFormatDescriptor : DosPackedExeDescriptor {
  public override string Id => "Pgmpak";
  public override string DisplayName => "PGMPAK (DOS exe)";
  public override string Description =>
    "PGMPAK-packed DOS executable: lists the restored program, recovered by running the stub in " +
    "an 8086 emulator.";
  protected override IDosExeUnpacker Unpacker => PgmpakUnpacker.Instance;
  protected override string MetadataSection => "pgmpak";
}

/// <summary>AXE-packed DOS executables (System Enhancement Associates): lists the restored program (8086 emulation of the stub).</summary>
public sealed class AxeFormatDescriptor : DosPackedExeDescriptor {
  public override string Id => "Axe";
  public override string DisplayName => "AXE (DOS exe)";
  public override string Description =>
    "AXE-packed DOS executable: lists the restored program, recovered by running the stub in an " +
    "8086 emulator; AXE starts the program with one word left on its stack (SP 2 below the " +
    "original), and so does the restored program.";
  protected override IDosExeUnpacker Unpacker => AxeUnpacker.Instance;
  protected override string MetadataSection => "axe";
}
