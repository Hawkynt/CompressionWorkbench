using System.Buffers.Binary;
using Compression.Core.Deflate;
using Compression.Core.Streams;
using FileFormat.Zlib;
using System.Text;
using Compression.Registry;

namespace FileFormat.PyInstaller;

/// <summary>Writes the documented PyInstaller CArchive container (the PKG payload).</summary>
/// <remarks>
/// Every offset in the TOC and the cookie is relative to where the archive starts, so the
/// PKG can be written after whatever the stream already holds — a bootloader, for a onefile
/// executable — without seeking.
/// </remarks>
internal static class PyInstallerWriter {

  /// <summary>Fixed part of a TOC entry: four big-endian uint32 fields, the compression flag and the type code.</summary>
  private const int TocEntryHeaderLength = 18;

  private sealed record EncodedEntry(string Name, byte TypeCode, byte[] Data, int UncompressedLength);

  public static void Create(
    Stream output,
    IReadOnlyList<ArchiveInputInfo> inputs,
    int pythonVersion,
    string pythonLibraryName,
    char typeCode,
    bool compress,
    int? level,
    char separator = '/'
  ) {
    if (level is < 0 or > 9)
      throw new ArgumentOutOfRangeException(nameof(level), "Compression level must be between 0 and 9.");

    var encoded = new List<EncodedEntry>(inputs.Count);
    foreach (var input in inputs) {
      var name = NormalizeEntryName(input.ArchiveName, separator);
      var nameBytes = Encoding.UTF8.GetBytes(name);
      if (nameBytes.Contains((byte)0))
        throw new ArgumentException($"CArchive entry name '{input.ArchiveName}' contains a NUL character.", nameof(inputs));
      if (nameBytes.Length > int.MaxValue - TocEntryHeaderLength - 16)
        throw new ArgumentException("CArchive entry name is too long.", nameof(inputs));

      var source = input.ReadContent();
      var data = compress ? Compress(source, level) : source;
      encoded.Add(new EncodedEntry(name, (byte)typeCode, data, source.Length));
    }

    long written = 0;
    var positions = new uint[encoded.Count];
    for (var index = 0; index < encoded.Count; ++index) {
      positions[index] = CheckedUInt32(written, "CArchive data offset");
      output.Write(encoded[index].Data);
      written += encoded[index].Data.Length;
    }

    var tocOffset = CheckedUInt32(written, "CArchive TOC offset");
    for (var index = 0; index < encoded.Count; ++index) {
      var entry = encoded[index];
      var name = Encoding.UTF8.GetBytes(entry.Name);
      // The name is NUL-terminated and then NUL-padded so every TOC entry is a
      // multiple of 16 bytes: the bootloader reads the TOC in place and faults on
      // strict-alignment targets (armhf) otherwise. PyInstaller's writer pads the same way.
      var entryLength = checked((TocEntryHeaderLength + name.Length + 1 + 15) & ~15);
      WriteUInt32BigEndian(output, checked((uint)entryLength));
      WriteUInt32BigEndian(output, positions[index]);
      WriteUInt32BigEndian(output, CheckedUInt32(entry.Data.Length, "CArchive entry length"));
      WriteUInt32BigEndian(output, CheckedUInt32(entry.UncompressedLength, "CArchive uncompressed length"));
      output.WriteByte(compress ? (byte)1 : (byte)0);
      output.WriteByte(entry.TypeCode);
      output.Write(name);
      output.Write(new byte[entryLength - TocEntryHeaderLength - name.Length]);
      written += entryLength;
    }

    var tocLength = CheckedUInt32(written - tocOffset, "CArchive TOC length");
    WriteCookie(output, written, tocOffset, tocLength, pythonVersion, pythonLibraryName);
  }

  /// <summary>
  /// A relative name with the separator the target bootloader expects: PyInstaller's
  /// writer stores backslashes for Windows ("the bootloader works only with back
  /// slashes") and forward slashes everywhere else.
  /// </summary>
  private static string NormalizeEntryName(string name, char separator) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    var normalized = name.Replace('\\', '/').TrimStart('/');
    if (normalized.Length == 0 || normalized.Split('/').Any(part => part is "" or "." or ".."))
      throw new ArgumentException($"'{name}' is not a safe relative CArchive entry name.", nameof(name));
    return separator == '/' ? normalized : normalized.Replace('/', separator);
  }

  // An empty entry still gets a complete zlib frame, which PyInstaller's reader requires
  // ("incomplete or truncated stream" otherwise); ZlibStream always writes one.
  private static byte[] Compress(byte[] data, int? level) {
    using var output = new MemoryStream();
    var compressionLevel = level switch {
      0 => DeflateCompressionLevel.None,
      <= 2 => DeflateCompressionLevel.Fast,
      >= 8 => DeflateCompressionLevel.Best,
      _ => DeflateCompressionLevel.Default,
    };
    using (var zlib = new ZlibStream(output, CompressionStreamMode.Compress, compressionLevel, leaveOpen: true))
      zlib.Write(data);
    return output.ToArray();
  }

  private static void WriteCookie(Stream output, long archiveLength, uint tocOffset, uint tocLength, int pythonVersion, string libraryName) {
    var packageLength = checked(archiveLength + PyInstallerReader.CookieSize);
    output.Write(PyInstallerReader.MagicCookie);
    WriteUInt32BigEndian(output, CheckedUInt32(packageLength, "CArchive package length"));
    WriteUInt32BigEndian(output, tocOffset);
    WriteUInt32BigEndian(output, tocLength);
    WriteUInt32BigEndian(output, checked((uint)pythonVersion));
    Span<byte> library = stackalloc byte[64];
    library.Clear();
    Encoding.ASCII.GetBytes(libraryName, library);
    output.Write(library);
  }

  private static uint CheckedUInt32(long value, string field) {
    if (value is < 0 or > uint.MaxValue)
      throw new InvalidDataException($"{field} exceeds the CArchive 32-bit limit.");
    return (uint)value;
  }

  private static void WriteUInt32BigEndian(Stream stream, uint value) {
    Span<byte> bytes = stackalloc byte[sizeof(uint)];
    BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
    stream.Write(bytes);
  }
}
