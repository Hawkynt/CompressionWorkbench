using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Compression.Registry;

namespace FileFormat.PyInstaller;

/// <summary>Writes the documented PyInstaller CArchive container (the PKG payload).</summary>
internal static class PyInstallerWriter {

  private sealed record EncodedEntry(string Name, byte TypeCode, byte[] Data, int UncompressedLength);

  public static void Create(
    Stream output,
    IReadOnlyList<ArchiveInputInfo> inputs,
    int pythonVersion,
    string pythonLibraryName,
    char typeCode,
    bool compress,
    int? level
  ) {
    if (level is < 0 or > 9)
      throw new ArgumentOutOfRangeException(nameof(level), "Compression level must be between 0 and 9.");
    if (!output.CanSeek)
      throw new ArgumentException("Writing a PyInstaller CArchive requires a seekable output stream.", nameof(output));

    output.SetLength(0);
    output.Position = 0;
    var encoded = new List<EncodedEntry>(inputs.Count);
    foreach (var input in inputs) {
      var name = NormalizeEntryName(input.ArchiveName);
      var nameBytes = Encoding.UTF8.GetBytes(name);
      if (nameBytes.Contains((byte)0))
        throw new ArgumentException($"CArchive entry name '{input.ArchiveName}' contains a NUL character.", nameof(inputs));
      if (nameBytes.Length > int.MaxValue - 18)
        throw new ArgumentException("CArchive entry name is too long.", nameof(inputs));

      var source = input.ReadContent();
      var data = compress ? Compress(source, level) : source;
      encoded.Add(new EncodedEntry(name, (byte)typeCode, data, source.Length));
    }

    var positions = new uint[encoded.Count];
    foreach (var (entry, index) in encoded.Select((entry, index) => (entry, index))) {
      positions[index] = CheckedUInt32(output.Position, "CArchive data offset");
      output.Write(entry.Data);
    }

    var tocOffset = CheckedUInt32(output.Position, "CArchive TOC offset");
    foreach (var (entry, index) in encoded.Select((entry, index) => (entry, index))) {
      var name = Encoding.UTF8.GetBytes(entry.Name);
      var entryLength = checked(18 + name.Length + 1);
      WriteUInt32BigEndian(output, checked((uint)entryLength));
      WriteUInt32BigEndian(output, positions[index]);
      WriteUInt32BigEndian(output, CheckedUInt32(entry.Data.Length, "CArchive entry length"));
      WriteUInt32BigEndian(output, CheckedUInt32(entry.UncompressedLength, "CArchive uncompressed length"));
      output.WriteByte(compress ? (byte)1 : (byte)0);
      output.WriteByte(entry.TypeCode);
      output.Write(name);
      output.WriteByte(0);
    }

    var tocLength = output.Position - tocOffset;
    WriteCookie(output, tocOffset, CheckedUInt32(tocLength, "CArchive TOC length"), pythonVersion, pythonLibraryName);
  }

  private static string NormalizeEntryName(string name) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    var normalized = name.Replace('\\', '/').TrimStart('/');
    if (normalized.Length == 0 || normalized.Split('/').Any(part => part is "" or "." or ".."))
      throw new ArgumentException($"'{name}' is not a safe relative CArchive entry name.", nameof(name));
    return normalized;
  }

  private static byte[] Compress(byte[] data, int? level) {
    using var output = new MemoryStream();
    var compressionLevel = level switch {
      0 => CompressionLevel.NoCompression,
      <= 2 => CompressionLevel.Fastest,
      >= 8 => CompressionLevel.SmallestSize,
      _ => CompressionLevel.Optimal,
    };
    using (var zlib = new ZLibStream(output, compressionLevel, leaveOpen: true))
      zlib.Write(data);
    return output.ToArray();
  }

  private static void WriteCookie(Stream output, uint tocOffset, uint tocLength, int pythonVersion, string libraryName) {
    var archiveLength = output.Position;
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
