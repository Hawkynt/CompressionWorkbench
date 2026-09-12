using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FileFormat.PyInstaller;

/// <summary>
/// Reads a PyInstaller CArchive — the package that a "onefile" build appends to
/// the bootloader PE executable (or a bare <c>.pkg</c> CArchive). It locates the
/// <c>MEI</c> magic cookie near end-of-file, parses the table of contents, and
/// exposes each entry's data (zlib-inflated when flagged). Entries carrying an
/// embedded PYZ (<c>PYZ\0</c>) archive can additionally enumerate their module
/// names.
/// </summary>
/// <remarks>
/// The layout is a matter of public record in the PyInstaller project. The cookie
/// (PyInstaller 4+ / v6 layout) is the C struct <c>!8sIIii64s</c>:
/// 8-byte magic, u32 package length, u32 TOC offset, i32 TOC length, i32 Python
/// version, and a 64-byte Python shared-library name. All multi-byte integers are
/// big-endian. TOC/data offsets are relative to the start of the CArchive, which
/// is <c>cookiePos + cookieSize - packageLength</c>.
/// </remarks>
public sealed class PyInstallerReader {

  /// <summary>The 8-byte magic cookie that marks a PyInstaller CArchive.</summary>
  public static readonly byte[] MagicCookie =
    [(byte)'M', (byte)'E', (byte)'I', 0x0C, 0x0B, 0x0A, 0x0B, 0x0E];

  /// <summary>Total cookie size in bytes: 8 magic + 4×4 header fields + 64-byte lib name.</summary>
  public const int CookieSize = 8 + 16 + 64;

  private readonly Stream _stream;
  private readonly long _archiveStart;
  private readonly long _tocOffset;
  private readonly int _tocLength;

  /// <summary>Gets the Python version encoded in the cookie (e.g. 313 for 3.13).</summary>
  public int PythonVersion { get; }

  /// <summary>Gets the embedded Python shared-library name (e.g. "python313.dll").</summary>
  public string PythonLibraryName { get; }

  /// <summary>Opens a reader over a seekable stream containing a PyInstaller CArchive.</summary>
  /// <exception cref="InvalidDataException">No MEI cookie was found or the header is corrupt.</exception>
  public PyInstallerReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek)
      throw new ArgumentException("PyInstaller archives require a seekable stream.", nameof(stream));

    this._stream = stream;

    var cookiePos = FindCookie(stream);
    if (cookiePos < 0)
      throw new InvalidDataException("No PyInstaller MEI cookie found.");

    Span<byte> hdr = stackalloc byte[CookieSize];
    ReadExactAt(stream, cookiePos, hdr);

    var packageLength = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(8, 4));
    var tocPos = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(12, 4));
    var tocLen = BinaryPrimitives.ReadInt32BigEndian(hdr.Slice(16, 4));
    this.PythonVersion = BinaryPrimitives.ReadInt32BigEndian(hdr.Slice(20, 4));
    this.PythonLibraryName = Encoding.ASCII.GetString(hdr.Slice(24, 64)).TrimEnd('\0');

    this._archiveStart = cookiePos + CookieSize - packageLength;
    if (this._archiveStart < 0 || tocLen < 0)
      throw new InvalidDataException("Corrupt PyInstaller cookie (package length exceeds file).");

    this._tocOffset = this._archiveStart + tocPos;
    this._tocLength = tocLen;
    if (this._tocOffset < 0 || this._tocOffset + tocLen > stream.Length)
      throw new InvalidDataException("Corrupt PyInstaller cookie (TOC out of bounds).");
  }

  /// <summary>
  /// Scans backward from end-of-file for the last occurrence of the MEI cookie
  /// (a onefile build may append an Authenticode signature after it). Returns the
  /// byte offset of the cookie, or -1 when the file contains no CArchive.
  /// </summary>
  public static long FindCookie(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var magic = MagicCookie;
    var length = stream.Length;
    if (length < magic.Length)
      return -1;

    const int chunk = 1 << 16;
    var overlap = magic.Length - 1;
    var readStart = Math.Max(0, length - chunk);
    var buffer = new byte[chunk + overlap];

    while (true) {
      var end = Math.Min(length, readStart + chunk + overlap);
      var count = (int)(end - readStart);
      ReadExactAt(stream, readStart, buffer.AsSpan(0, count));

      var idx = buffer.AsSpan(0, count).LastIndexOf(magic);
      if (idx >= 0)
        return readStart + idx;

      if (readStart == 0)
        return -1;

      readStart = Math.Max(0, readStart - chunk);
    }
  }

  /// <summary>Parses and returns every entry in the table of contents.</summary>
  public IReadOnlyList<PyInstallerEntry> ReadToc() {
    var toc = new byte[this._tocLength];
    ReadExactAt(this._stream, this._tocOffset, toc);

    var entries = new List<PyInstallerEntry>();
    var p = 0;
    while (p + 18 <= toc.Length) {
      var span = toc.AsSpan(p);
      var entryLen = (int)BinaryPrimitives.ReadUInt32BigEndian(span);
      if (entryLen < 18 || p + entryLen > toc.Length)
        break;

      var dataPos = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
      var dataLen = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4));
      var uncomprLen = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(12, 4));
      var cflag = span[16];
      var typecode = (char)span[17];

      var nameBytes = span.Slice(18, entryLen - 18);
      var nul = nameBytes.IndexOf((byte)0);
      if (nul >= 0)
        nameBytes = nameBytes[..nul];
      var name = Encoding.UTF8.GetString(nameBytes);

      entries.Add(new PyInstallerEntry(
        name, typecode, cflag != 0,
        this._archiveStart + dataPos, dataLen, uncomprLen));

      p += entryLen;
    }
    return entries;
  }

  /// <summary>Returns the entry data, zlib-inflated when the entry is compressed.</summary>
  public byte[] GetData(PyInstallerEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var raw = new byte[entry.CompressedLength];
    ReadExactAt(this._stream, entry.DataOffset, raw);
    if (!entry.IsCompressed)
      return raw;

    using var src = new MemoryStream(raw, writable: false);
    using var zs = new ZLibStream(src, CompressionMode.Decompress);
    using var outp = new MemoryStream(entry.UncompressedLength > 0 ? (int)entry.UncompressedLength : 0);
    zs.CopyTo(outp);
    return outp.ToArray();
  }

  /// <summary>
  /// Enumerates the module names inside a PYZ entry (type code 'z'/'Z'). Returns an
  /// empty list for non-PYZ entries or when the embedded TOC cannot be parsed.
  /// </summary>
  public IReadOnlyList<string> GetPyzModuleNames(PyInstallerEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.TypeCode is not ('z' or 'Z'))
      return [];

    try {
      var blob = this.GetData(entry);
      return ReadPyzModuleNames(blob);
    } catch (InvalidDataException) {
      return [];
    }
  }

  /// <summary>
  /// Parses a PYZ blob (<c>PYZ\0</c> magic, 4-byte Python magic, u32 BE TOC offset,
  /// then a marshalled list of <c>(name, (typecode, offset, length))</c> tuples) and
  /// returns the module names.
  /// </summary>
  public static IReadOnlyList<string> ReadPyzModuleNames(ReadOnlySpan<byte> pyz) {
    var names = new List<string>();
    if (pyz.Length < 12 || pyz[0] != 'P' || pyz[1] != 'Y' || pyz[2] != 'Z' || pyz[3] != 0)
      return names;

    var tocOffset = BinaryPrimitives.ReadUInt32BigEndian(pyz.Slice(8, 4));
    if (tocOffset >= (uint)pyz.Length)
      return names;

    var reader = new MarshalReader(pyz[(int)tocOffset..].ToArray());
    object? root;
    try {
      root = reader.ReadObject();
    } catch (InvalidDataException) {
      return names;
    }

    if (root is not List<object?> list)
      return names;

    foreach (var item in list)
      if (item is object?[] { Length: >= 1 } tuple && tuple[0] is string name)
        names.Add(name);

    return names;
  }

  private static void ReadExactAt(Stream stream, long position, Span<byte> destination) {
    stream.Position = position;
    stream.ReadExactly(destination);
  }
}
