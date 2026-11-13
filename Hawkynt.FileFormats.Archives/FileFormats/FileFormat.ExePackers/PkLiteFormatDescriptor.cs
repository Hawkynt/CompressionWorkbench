#pragma warning disable CS1591
namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for PKLITE-packed DOS executables and COM files (PKWARE,
/// 1990-1996): lists the restored program as <c>unpacked.exe</c> or <c>unpacked.com</c>.
/// Restoring is done by <see cref="PkLiteUnpacker"/>; on files from PKLITE 1.15 and 2.01 the
/// result equals both <c>pklite -x</c> and UNP 4.11 byte for byte.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://en.wikipedia.org/wiki/PKLITE</c> — format and tool history</description></item>
///   <item><description><c>https://github.com/jsummers/deark</c> — modules/pklite.c, the converted decoder</description></item>
/// </list>
/// </summary>
public sealed class PkLiteFormatDescriptor : DosPackedExeDescriptor {
  public override string Id => "PkLite";
  public override string DisplayName => "PKLITE (DOS exe)";
  public override string Description =>
    "PKLITE-packed DOS executable or COM file (versions 1.00 to 2.01, including extra and large " +
    "compression): lists the restored program.";
  protected override IDosExeUnpacker Unpacker => PkLiteUnpacker.Instance;
  protected override string MetadataSection => "pklite";

  /// <summary>The copyright text the stub carries near the start of the file.</summary>
  protected override string? FindMarker(ReadOnlySpan<byte> file) =>
    PackerScanner.IndexOfBounded(file, "PKLITE Copr."u8, 0x400) >= 0 ? "PKLITE Copr."
    : PackerScanner.IndexOfBounded(file, "PKlite Copr."u8, 0x400) >= 0 ? "PKlite Copr."
    : null;
}
