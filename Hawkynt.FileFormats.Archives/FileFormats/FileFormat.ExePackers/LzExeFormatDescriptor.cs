#pragma warning disable CS1591
namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for LZEXE-packed DOS executables (Fabrice Bellard, 1989): lists the
/// restored program as <c>unpacked.exe</c>. Versions 0.90, 0.91 and 1.00a are restored by
/// <see cref="LzExeUnpacker"/>; the image and relocations equal what UNP 4.11 restores.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://en.wikipedia.org/wiki/LZEXE</c> — format and tool history</description></item>
///   <item><description><c>https://github.com/jsummers/deark</c> — modules/lzexe.c, the converted decoder</description></item>
/// </list>
/// </summary>
public sealed class LzExeFormatDescriptor : DosPackedExeDescriptor {
  public override string Id => "LzExe";
  public override string DisplayName => "LZEXE (DOS exe)";
  public override string Description =>
    "LZEXE (Fabrice Bellard, 1989) DOS exe compressor, versions 0.90, 0.91 and 1.00a: lists " +
    "the restored program (image, relocations and entry registers as LZEXE recorded them).";
  protected override IDosExeUnpacker Unpacker => LzExeUnpacker.Instance;
  protected override string MetadataSection => "lzexe";

  /// <summary>The "LZ91"/"LZ09" mark LZEXE leaves at offset 0x1C.</summary>
  protected override string? FindMarker(ReadOnlySpan<byte> file) =>
    PackerScanner.IndexOfBounded(file, "LZ91"u8, 0x400) >= 0 ? "LZ91"
    : PackerScanner.IndexOfBounded(file, "LZ09"u8, 0x400) >= 0 ? "LZ09"
    : null;
}
