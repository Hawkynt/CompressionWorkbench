#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Pst;

/// <summary>
/// Microsoft Outlook personal storage (<c>.pst</c> / <c>.ost</c>). The archive
/// view surfaces: <c>FULL.pst</c> (passthrough), <c>metadata.ini</c>
/// (format=ansi|unicode, version, file size, header CRC, root BBT/NBT offsets)
/// and <c>header.bin</c> (raw 512-byte ANSI or 564-byte Unicode header).
/// <para>
/// This is an opaque, byte-preserving container view: the NDB/LTP tree is not
/// unpacked into folders and messages. Creation accepts one existing,
/// header-validated PST/OST image and copies it without changing its internal compression,
/// encryption, or metadata. A PST cannot be synthesized from loose files here.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-pst/141923d5-15ab-4ef1-a524-6dce75aae546</c> — [MS-PST]: Outlook Personal Folders (.pst) File Format (Microsoft Open Specifications)</description></item>
///   <item><description><c>https://github.com/libyal/libpff</c> — libpff — open PST/OST implementation with format documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Personal_Storage_Table</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class PstFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  private const int AnsiHeaderSize = 512;
  private const int UnicodeHeaderSize = 564;

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Pst";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "PST / OST (Outlook mailbox)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".pst";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".pst", ".ost"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "!BDN" at offset 0 — 21 42 44 4E.
    new([0x21, 0x42, 0x44, 0x4E], Offset: 0, Confidence: 0.98),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Outlook PST/OST mailbox; byte-preserving opaque container with header metadata.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var header = ReadHeader(stream);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.pst", stream.Length, stream.Length, "stored", false, false, null, "Container"),
    };
    foreach (var e in BuildSynthetic(stream, header))
      entries.Add(new ArchiveEntryInfo(
        entries.Count, e.Name, e.Data.Length, e.Data.Length,
        "stored", false, false, null, e.Kind));
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var header = ReadHeader(stream);
    // Stream FULL.pst directly — never buffer the whole file.
    if (files == null || files.Length == 0 || MatchesFilter("FULL.pst", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.pst");
      var dir = Path.GetDirectoryName(fullPath);
      if (dir != null) Directory.CreateDirectory(dir);
      using var outStream = File.Create(fullPath);
      stream.CopyTo(outStream);
    }
    foreach (var e in BuildSynthetic(stream, header)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. The synthetic
  /// <c>FULL.pst</c> is exposed as a passthrough slice over the whole
  /// archive; <c>metadata.ini</c> and <c>header.bin</c> are produced by
  /// <see cref="BuildSynthetic"/> and wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to their logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var header = ReadHeader(archive);
    if (string.Equals(entryName, "FULL.pst", StringComparison.OrdinalIgnoreCase)) {
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new Compression.Registry.Streaming.ReadOnlyStreamSlice(archive, 0, archive.Length),
        archive.Length, leaveOpen: false);
    }
    foreach (var e in BuildSynthetic(archive, header)) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(e.Data, writable: false), e.Data.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// Re-emits one existing PST/OST image exactly as supplied. This preserves
  /// every Outlook property, page, free map, compression bit, and opaque field;
  /// no PST writer is implied by this operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    using var input = OpenSingleImage(inputs);
    _ = ReadHeader(input);
    input.Position = 0;
    input.CopyTo(output);
  }

  /// <summary>Streaming variant suitable for large Outlook data files.</summary>
  public void CreateFromStreams(Stream target, IEnumerable<StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(inputs);
    using var input = OpenSingleImage(inputs);
    if (input.CanSeek) {
      _ = ReadHeader(input);
      input.Position = 0;
    } else {
      var headerBytes = ReadHeaderBytes(input);
      _ = ParseHeader(headerBytes);
      target.Write(headerBytes);
    }
    input.CopyTo(target);
  }

  private static Stream OpenSingleImage(IReadOnlyList<ArchiveInputInfo> inputs) {
    var files = inputs.Where(static i => !i.IsDirectory)
      .Where(static i => !IsSyntheticName(i.ArchiveName))
      .ToArray();
    if (files.Length != 1 || !IsPstName(files[0].ArchiveName))
      throw new NotSupportedException("PST/OST creation requires exactly one existing .pst or .ost image (synthetic metadata entries are ignored).");
    var input = files[0];
    return input.InMemoryContent is { } bytes
      ? new MemoryStream(bytes, writable: false)
      : File.OpenRead(input.FullPath);
  }

  private static Stream OpenSingleImage(IEnumerable<StreamingArchiveInput> inputs) {
    var files = inputs.Where(static i => !i.IsDirectory)
      .Where(static i => !IsSyntheticName(i.Name))
      .ToArray();
    if (files.Length != 1 || !IsPstName(files[0].Name))
      throw new NotSupportedException("PST/OST creation requires exactly one existing .pst or .ost image (synthetic metadata entries are ignored).");
    return files[0].OpenStream();
  }

  private static bool IsSyntheticName(string name) {
    var leaf = Path.GetFileName(name);
    return leaf.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)
      || leaf.Equals("header.bin", StringComparison.OrdinalIgnoreCase);
  }

  private static bool IsPstName(string name) {
    var extension = Path.GetExtension(name);
    return extension.Equals(".pst", StringComparison.OrdinalIgnoreCase)
      || extension.Equals(".ost", StringComparison.OrdinalIgnoreCase);
  }

  private sealed record PstHeader(
    byte[] Bytes, bool IsUnicode, ushort Version, ushort ClientVersion, uint CrcPartial,
    ulong FileEof, ulong AMapLast, ulong AMapFree, ulong PMapFree,
    ulong NbtBid, ulong NbtOffset, ulong BbtBid, ulong BbtOffset,
    byte CryptMethod, uint? CrcFull
  );

  private static PstHeader ReadHeader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("PST/OST input must be readable and seekable.", nameof(stream));
    var originalPosition = stream.Position;
    try {
      stream.Position = 0;
      return ParseHeader(ReadHeaderBytes(stream));
    } finally {
      stream.Position = originalPosition;
    }
  }

  private static byte[] ReadHeaderBytes(Stream stream) {
    Span<byte> prefix = stackalloc byte[14];
    ReadFully(stream, prefix);
    ValidatePrefix(prefix);
    var version = BinaryPrimitives.ReadUInt16LittleEndian(prefix[10..]);
    var headerSize = version >= 23 ? UnicodeHeaderSize : AnsiHeaderSize;
    if (stream.CanSeek && stream.Length < headerSize)
      throw new InvalidDataException($"Truncated PST/OST header: expected {headerSize} bytes.");
    var header = new byte[headerSize];
    prefix.CopyTo(header);
    ReadFully(stream, header.AsSpan(prefix.Length));
    return header;
  }

  private static void ValidatePrefix(ReadOnlySpan<byte> prefix) {
    if (!prefix[..4].SequenceEqual(new byte[] { 0x21, 0x42, 0x44, 0x4E }))
      throw new InvalidDataException("Not a PST/OST file: the !BDN signature is missing.");
    if (BinaryPrimitives.ReadUInt16LittleEndian(prefix[8..]) != 0x4D53)
      throw new InvalidDataException("Not a PST/OST file: the client signature is invalid.");
    var version = BinaryPrimitives.ReadUInt16LittleEndian(prefix[10..]);
    if (version < 23 && version is not (14 or 15))
      throw new InvalidDataException($"Unsupported PST/OST file version {version}.");
  }

  private static PstHeader ParseHeader(byte[] header) {
    var version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
    var isUnicode = version >= 23;
    var sentinelOffset = isUnicode ? 512 : 460;
    if (header[sentinelOffset] != 0x80)
      throw new InvalidDataException("Invalid PST/OST header sentinel.");
    var cryptMethod = header[sentinelOffset + 1];
    if (cryptMethod is not (0x00 or 0x01 or 0x02 or 0x10))
      throw new InvalidDataException($"Unsupported PST/OST crypt method 0x{cryptMethod:X2}.");

    var rootOffset = isUnicode ? 180 : 164;
    ulong ReadRootValue(int offset, bool wide) => wide
      ? BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(rootOffset + offset))
      : BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(rootOffset + offset));

    var nbtBid = ReadRootValue(isUnicode ? 36 : 20, isUnicode);
    var nbtOffset = ReadRootValue(isUnicode ? 44 : 24, isUnicode);
    var bbtBid = ReadRootValue(isUnicode ? 52 : 28, isUnicode);
    var bbtOffset = ReadRootValue(isUnicode ? 60 : 32, isUnicode);
    return new PstHeader(
      header, isUnicode, version, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12)),
      BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)),
      ReadRootValue(4, isUnicode), ReadRootValue(isUnicode ? 12 : 8, isUnicode),
      ReadRootValue(isUnicode ? 20 : 12, isUnicode), ReadRootValue(isUnicode ? 28 : 16, isUnicode),
      nbtBid, nbtOffset, bbtBid, bbtOffset, cryptMethod,
      isUnicode ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(524)) : null
    );
  }

  private static void ReadFully(Stream stream, Span<byte> destination) {
    var read = 0;
    while (read < destination.Length) {
      var n = stream.Read(destination[read..]);
      if (n <= 0) break;
      read += n;
    }
    if (read != destination.Length)
      throw new EndOfStreamException("Truncated PST/OST header.");
  }

  private static IReadOnlyList<(string Name, byte[] Data, string Kind)> BuildSynthetic(Stream stream, PstHeader header) {
    var fileSize = stream.Length;
    var ini = new StringBuilder();
    ini.AppendLine("; Outlook PST/OST header");
    ini.AppendLine("storage_format=PST/OST shared PFF");
    ini.AppendLine("parse_status=header-only");
    ini.Append("format=").AppendLine(header.IsUnicode ? "unicode" : "ansi");
    ini.Append("version=").AppendLine(header.Version.ToString(CultureInfo.InvariantCulture));
    ini.Append("version_client=").AppendLine(header.ClientVersion.ToString(CultureInfo.InvariantCulture));
    ini.Append("file_size=").AppendLine(fileSize.ToString(CultureInfo.InvariantCulture));
    ini.Append("header_crc_partial=0x").AppendLine(header.CrcPartial.ToString("X8", CultureInfo.InvariantCulture));
    if (header.CrcFull is { } crcFull)
      ini.Append("header_crc_full=0x").AppendLine(crcFull.ToString("X8", CultureInfo.InvariantCulture));
    ini.Append("file_eof=").AppendLine(header.FileEof.ToString(CultureInfo.InvariantCulture));
    ini.Append("amap_last_offset=").AppendLine(header.AMapLast.ToString(CultureInfo.InvariantCulture));
    ini.Append("amap_free_bytes=").AppendLine(header.AMapFree.ToString(CultureInfo.InvariantCulture));
    ini.Append("pmap_free_bytes=").AppendLine(header.PMapFree.ToString(CultureInfo.InvariantCulture));
    ini.Append("root_nbt_bid=0x").AppendLine(header.NbtBid.ToString("X", CultureInfo.InvariantCulture));
    ini.Append("root_nbt_offset=").AppendLine(header.NbtOffset.ToString(CultureInfo.InvariantCulture));
    ini.Append("root_bbt_bid=0x").AppendLine(header.BbtBid.ToString("X", CultureInfo.InvariantCulture));
    ini.Append("root_bbt_offset=").AppendLine(header.BbtOffset.ToString(CultureInfo.InvariantCulture));
    ini.Append("crypt_method=0x").AppendLine(header.CryptMethod.ToString("X2", CultureInfo.InvariantCulture));

    return [
      ("metadata.ini", Encoding.UTF8.GetBytes(ini.ToString()), "Tag"),
      ("header.bin", header.Bytes, "Track"),
    ];
  }
}
