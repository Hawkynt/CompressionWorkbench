using System.IO.Compression;
using System.Text;

namespace FileFormat.VppV2;

/// <summary>
/// Reads entries from a Volition Package v2 archive (Saint's Row 2 era, .vpp_pc).
/// </summary>
/// <remarks>
/// Layout: header (Magic+Version+ShortName(256)+Path(96)+8 dwords) → padding to 0x800 →
/// TOC (FileCount × 28 bytes) → padding to 0x800 → name table (packed null-terminated UTF-8) →
/// padding to 0x800 → data region. Per-entry zlib uses raw deflate via <see cref="ZLibStream"/>.
/// Whole-archive Condensed mode is explicitly rejected.
/// </remarks>
public sealed class VppV2Reader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets all entries in the archive in declaration order.</summary>
  public IReadOnlyList<VppV2Entry> Entries { get; }

  /// <summary>Gets the total archive size declared in the header.</summary>
  public long DeclaredArchiveSize { get; }

  /// <summary>
  /// Initializes a new <see cref="VppV2Reader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the VPP v2 archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public VppV2Reader(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < VppV2Constants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid VPP v2 archive.");

    Span<byte> header = stackalloc byte[VppV2Constants.HeaderSize];
    this._stream.Position = 0;
    ReadExact(header);

    var magic = BitConverter.ToUInt32(header[0..4]);
    if (magic != VppV2Constants.Magic)
      throw new InvalidDataException($"Invalid VPP v2 magic: 0x{magic:X8} (expected 0x{VppV2Constants.Magic:X8}).");

    var version = BitConverter.ToUInt32(header[4..8]);
    if (version != VppV2Constants.SupportedVersion)
      throw new NotSupportedException(
        $"VPP version {version} is not handled by VppV2Reader (this reader only supports version 2; "
        + "v1 archives should be opened via the FileFormat.Vpp descriptor).");

    var headerSizeField = BitConverter.ToUInt32(header.Slice(VppV2Constants.HeaderSizeFieldOffset, 4));
    if (headerSizeField != VppV2Constants.RequiredHeaderSizeField)
      throw new InvalidDataException(
        $"VPP v2 HeaderSize field is 0x{headerSizeField:X8}; expected 0x{VppV2Constants.RequiredHeaderSizeField:X8}.");

    var fileCount     = BitConverter.ToUInt32(header.Slice(VppV2Constants.FileCountFieldOffset, 4));
    var archiveSize   = BitConverter.ToUInt32(header.Slice(VppV2Constants.ArchiveSizeFieldOffset, 4));
    var tocSize       = BitConverter.ToUInt32(header.Slice(VppV2Constants.TocSizeFieldOffset, 4));
    var nameTableSize = BitConverter.ToUInt32(header.Slice(VppV2Constants.NameTableSizeFieldOffset, 4));
    var flags         = BitConverter.ToUInt32(header.Slice(VppV2Constants.FlagsFieldOffset, 4));

    this.DeclaredArchiveSize = archiveSize;

    if ((flags & VppV2Constants.FlagArchiveCondensed) != 0)
      throw new NotSupportedException("Condensed VPP archives not supported.");

    if (fileCount > int.MaxValue / VppV2Constants.TocEntrySize)
      throw new InvalidDataException($"Implausible VPP v2 file count: {fileCount}.");

    if (tocSize != fileCount * VppV2Constants.TocEntrySize)
      throw new InvalidDataException(
        $"VPP v2 TocSize {tocSize} does not match FileCount {fileCount} × {VppV2Constants.TocEntrySize}.");

    this.Entries = ReadEntries((int)fileCount, tocSize, nameTableSize);
  }

  /// <summary>
  /// Extracts the (decompressed) raw bytes for a given entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The decompressed payload (<see cref="VppV2Entry.DataSize"/> bytes).</returns>
  public byte[] Extract(VppV2Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.DataSize == 0)
      return [];

    if (entry.DataSize > int.MaxValue || entry.CompressedSize > int.MaxValue)
      throw new NotSupportedException(
        $"Entry '{entry.Name}' is too large to extract into a single byte array "
        + $"(data {entry.DataSize}, compressed {entry.CompressedSize}).");

    this._stream.Position = entry.DataOffset;

    if (!entry.IsCompressed) {
      var raw = new byte[entry.DataSize];
      ReadExact(raw);
      return raw;
    }

    var compressed = new byte[entry.CompressedSize];
    ReadExact(compressed);

    using var ms = new MemoryStream(compressed);
    using var zs = new ZLibStream(ms, CompressionMode.Decompress);
    var output = new byte[entry.DataSize];
    var read = 0;
    while (read < output.Length) {
      var got = zs.Read(output, read, output.Length - read);
      if (got == 0)
        throw new InvalidDataException(
          $"Truncated zlib stream for entry '{entry.Name}': expected {entry.DataSize} bytes, got {read}.");
      read += got;
    }
    return output;
  }

  private List<VppV2Entry> ReadEntries(int fileCount, uint tocSize, uint nameTableSize) {
    // TOC starts at the 0x800 boundary declared by the HeaderSize field.
    this._stream.Position = VppV2Constants.RequiredHeaderSizeField;

    var tocBuffer = new byte[fileCount * VppV2Constants.TocEntrySize];
    if (tocBuffer.Length > 0)
      ReadExact(tocBuffer);

    var tocBlockSize = AlignUp((long)tocSize, VppV2Constants.SectionAlignment);
    var nameTableOffset = (long)VppV2Constants.RequiredHeaderSizeField + tocBlockSize;

    var nameTable = new byte[nameTableSize];
    if (nameTableSize > 0) {
      this._stream.Position = nameTableOffset;
      ReadExact(nameTable);
    }

    var nameBlockSize = AlignUp((long)nameTableSize, VppV2Constants.SectionAlignment);
    var dataRegionStart = nameTableOffset + nameBlockSize;

    var entries = new List<VppV2Entry>(fileCount);
    for (var i = 0; i < fileCount; ++i) {
      var rec = tocBuffer.AsSpan(i * VppV2Constants.TocEntrySize, VppV2Constants.TocEntrySize);
      var nameOffset     = BitConverter.ToUInt32(rec[0..4]);
      _ = BitConverter.ToUInt32(rec[4..8]); // ExtensionOffset — cosmetic.
      var dataOffset     = BitConverter.ToUInt32(rec[8..12]);
      var dataSize       = BitConverter.ToUInt32(rec[12..16]);
      var compressedSize = BitConverter.ToUInt32(rec[16..20]);
      var flags          = BitConverter.ToUInt32(rec[20..24]);

      if (nameTableSize > 0 && nameOffset >= nameTableSize)
        throw new InvalidDataException($"Entry {i} has NameOffset {nameOffset} beyond name table ({nameTableSize}).");

      var name = nameTableSize == 0 ? "" : ReadAsciiZ(nameTable, (int)nameOffset);

      entries.Add(new VppV2Entry {
        Name           = name,
        DataOffset     = dataRegionStart + dataOffset,
        DataSize       = dataSize,
        CompressedSize = compressedSize,
        IsCompressed   = (flags & VppV2Constants.FlagEntryCompressed) != 0,
      });
    }

    return entries;
  }

  private static string ReadAsciiZ(byte[] table, int offset) {
    var end = offset;
    while (end < table.Length && table[end] != 0)
      ++end;
    return Encoding.UTF8.GetString(table, offset, end - offset);
  }

  private static long AlignUp(long value, long alignment) {
    var remainder = value % alignment;
    return remainder == 0 ? value : value + (alignment - remainder);
  }

  private void ReadExact(Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = this._stream.Read(buffer[totalRead..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of VPP v2 stream.");
      totalRead += read;
    }
  }

  private void ReadExact(byte[] buffer) => ReadExact(buffer.AsSpan());

  /// <inheritdoc />
  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
