#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Afio;

/// <summary>
/// Writer for afio's portable-ASCII (<c>odc</c>) member format: a 76-byte all-octal ASCII header,
/// the NUL-terminated name, then the payload, with no alignment padding anywhere. The archive ends
/// with a zero-length member named <c>TRAILER!!!</c>.
/// </summary>
/// <remarks>
/// <para>
/// The header is eleven fixed-width octal fields — magic(6) <c>070707</c>, dev(6), ino(6), mode(6),
/// uid(6), gid(6), nlink(6), rdev(6), mtime(11), namesize(6), filesize(11) — and
/// <c>namesize</c> counts the trailing NUL. Ownership and timestamps are written as zero: afio
/// stores them, but nothing in this library reads them back, and inventing values would put
/// unearned detail into the archive.
/// </para>
/// <para>
/// Members are stored, never compressed. afio's per-file gzip extension records the original size
/// after the name, and this package's reader does not parse that record — it recognises a
/// compressed member by sniffing the <c>1F 8B</c> signature at the start of the payload instead.
/// Emitting a gzip member without the size record would produce an archive real afio misreads, so
/// the writer does not emit one. For the same reason it refuses to <em>store</em> a payload that
/// itself begins with the gzip signature: the reader would inflate it on the way out and hand back
/// bytes that are not the ones that went in.
/// </para>
/// </remarks>
public static class AfioWriter {

  private const int HeaderSize = 76;
  private const string Magic = "070707";
  private const string Trailer = "TRAILER!!!";

  /// <summary>Regular file, mode <c>0100644</c>.</summary>
  public const uint RegularFileMode = 0x81A4;

  /// <summary>Directory, mode <c>0040755</c>.</summary>
  public const uint DirectoryMode = 0x41ED;

  /// <summary>The largest payload an 11-digit octal <c>filesize</c> field can address.</summary>
  private const long MaxFileSize = (1L << 33) - 1; // 0o37777777777

  /// <summary>The largest name length a 6-digit octal <c>namesize</c> field can address.</summary>
  private const int MaxNameSize = (1 << 18) - 1; // 0o777777

  /// <summary>Writes a regular-file member.</summary>
  public static void WriteFile(Stream output, string name, ReadOnlySpan<byte> data) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentException.ThrowIfNullOrEmpty(name);
    if (LooksLikeGzip(data))
      throw new NotSupportedException(
        $"afio: '{name}' starts with the gzip signature. A stored member holding a gzip stream is "
        + "indistinguishable from afio's compressed-member extension, and would be inflated on "
        + "extraction rather than returned as written.");

    WriteHeader(output, name, data.Length, RegularFileMode);
    output.Write(data);
  }

  /// <summary>Writes a directory member. Directories carry no payload.</summary>
  public static void WriteDirectory(Stream output, string name) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentException.ThrowIfNullOrEmpty(name);
    WriteHeader(output, name.TrimEnd('/'), 0, DirectoryMode);
  }

  /// <summary>Writes the <c>TRAILER!!!</c> member that terminates the archive.</summary>
  public static void WriteTrailer(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    WriteHeader(output, Trailer, 0, 0);
  }

  private static bool LooksLikeGzip(ReadOnlySpan<byte> data)
    => data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B;

  private static void WriteHeader(Stream output, string name, long fileSize, uint mode) {
    if (fileSize > MaxFileSize)
      throw new NotSupportedException(
        $"afio: '{name}' is {fileSize} bytes, past the {MaxFileSize} an 11-digit octal filesize field holds.");

    var nameBytes = Encoding.ASCII.GetBytes(name);
    var nameSize = nameBytes.Length + 1; // the NUL is counted
    if (nameSize > MaxNameSize)
      throw new NotSupportedException(
        $"afio: the name '{name}' is past the {MaxNameSize} a 6-digit octal namesize field holds.");

    var header = new StringBuilder(HeaderSize);
    header.Append(Magic);
    header.Append(Octal(0, 6));         // dev
    header.Append(Octal(0, 6));         // ino
    header.Append(Octal(mode, 6));      // mode
    header.Append(Octal(0, 6));         // uid
    header.Append(Octal(0, 6));         // gid
    header.Append(Octal(1, 6));         // nlink
    header.Append(Octal(0, 6));         // rdev
    header.Append(Octal(0, 11));        // mtime
    header.Append(Octal(nameSize, 6));  // namesize, including the NUL
    header.Append(Octal(fileSize, 11)); // filesize

    var text = header.ToString();
    if (text.Length != HeaderSize)
      throw new InvalidOperationException($"afio: built a {text.Length}-byte header, expected {HeaderSize}.");

    output.Write(Encoding.ASCII.GetBytes(text));
    output.Write(nameBytes);
    output.WriteByte(0);
  }

  private static string Octal(long value, int width)
    => System.Convert.ToString(value, 8).PadLeft(width, '0');
}
