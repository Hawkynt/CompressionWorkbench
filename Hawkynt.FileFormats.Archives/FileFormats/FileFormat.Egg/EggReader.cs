using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Deflate;

namespace FileFormat.Egg;

/// <summary>
/// Reads EGG (ALZip) archive files: lists entries and extracts Store/Deflate content.
/// </summary>
/// <remarks>
/// <para>
/// The layout is taken from the published EGG Format Specification, Version 1.0.
/// All multi-byte fields are little-endian.
/// </para>
/// <para>
/// An archive is: an <b>EGG header</b> (magic <c>0x41474745</c> "EGGA", 2-byte version,
/// 4-byte header id, 4-byte reserved) followed by a variable list of <em>extra fields</em>
/// terminated by the end-marker <c>0x08E28222</c>. Then, for each file: a <b>File header</b>
/// (magic <c>0x0A8590E3</c>, 4-byte file id, 8-byte total file length), a list of extra
/// fields (filename, comment, windows/posix file information, encryption) terminated by the
/// end-marker, and one or more <b>Block headers</b> (magic <c>0x02B50C13</c>: 1-byte algorithm,
/// 1-byte hint, 4-byte uncompressed size, 4-byte compressed size, 4-byte CRC-32) each followed
/// by its own extra-field list, the end-marker, and the compressed data. A trailing end-marker
/// closes the archive.
/// </para>
/// <para>
/// Extra fields share a generic shape — 4-byte magic, 1-byte general-purpose bit flag, a
/// 2- or 4-byte size (2 unless bit&#160;0 of the flag is set), then <c>size</c> data bytes —
/// so unknown fields are skipped by size.
/// </para>
/// </remarks>
public sealed class EggReader : IDisposable {

  /// <summary>EGG header magic (little-endian) — ASCII "EGGA".</summary>
  internal const uint EggMagic = 0x41474745;

  /// <summary>End-of-header / end-of-archive iteration stop marker ("EOFARC").</summary>
  internal const uint EndMarker = 0x08E28222;

  /// <summary>File header magic.</summary>
  internal const uint FileHeaderMagic = 0x0A8590E3;

  /// <summary>Block header magic.</summary>
  internal const uint BlockHeaderMagic = 0x02B50C13;

  /// <summary>Filename extra-field magic.</summary>
  internal const uint FilenameMagic = 0x0A8591AC;

  /// <summary>Comment extra-field magic.</summary>
  internal const uint CommentMagic = 0x04C63672;

  /// <summary>Windows file-information extra-field magic.</summary>
  internal const uint WindowsInfoMagic = 0x2C86950B;

  /// <summary>Posix file-information extra-field magic.</summary>
  internal const uint PosixInfoMagic = 0x1EE922E5;

  /// <summary>Encryption extra-field magic.</summary>
  internal const uint EncryptMagic = 0x08D1470F;

  /// <summary>Dummy (padding) extra-field magic.</summary>
  internal const uint DummyMagic = 0x07463307;

  /// <summary>Split-compression extra-field magic (multi-volume archive).</summary>
  internal const uint SplitMagic = 0x24F5A262;

  /// <summary>Solid-compression extra-field magic.</summary>
  internal const uint SolidMagic = 0x24E5A060;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly BinaryReader _reader;
  private readonly List<EggEntry> _entries = [];

  /// <summary>
  /// Opens an EGG archive over the given seekable stream and parses its directory.
  /// </summary>
  /// <param name="stream">A readable, seekable stream positioned anywhere; it is rewound to 0.</param>
  /// <param name="leaveOpen">When true the stream is not disposed with the reader.</param>
  public EggReader(Stream stream, bool leaveOpen = false) {
    _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new NotSupportedException("EGG reading requires a seekable stream.");
    _leaveOpen = leaveOpen;
    _reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
    _ReadArchive();
  }

  /// <summary>File entries discovered in the archive.</summary>
  public IReadOnlyList<EggEntry> Entries => _entries;

  /// <summary>True when the archive is a member of a split (multi-volume) set.</summary>
  public bool IsSplit { get; private set; }

  /// <summary>True when a global (archive-level) encryption header is present.</summary>
  public bool IsGloballyEncrypted { get; private set; }

  private void _ReadArchive() {
    _stream.Position = 0;

    var magic = _reader.ReadUInt32();
    if (magic != EggMagic)
      throw new InvalidDataException("Not an EGG archive (missing EGGA signature).");

    _ = _reader.ReadUInt16(); // version
    _ = _reader.ReadUInt32(); // header id
    _ = _reader.ReadUInt32(); // reserved

    // EGG-header extra fields (split / solid / global encryption) up to the end-marker.
    _SkipExtraFields((field, _, _, _) => {
      switch (field) {
        case SplitMagic: this.IsSplit = true; break;
        case SolidMagic: break;
        case EncryptMagic: this.IsGloballyEncrypted = true; break;
      }
    });

    // File entries.
    while (_stream.Position + 4 <= _stream.Length) {
      var token = _reader.ReadUInt32();
      if (token == EndMarker)
        break; // end of archive

      if (token != FileHeaderMagic)
        throw new InvalidDataException($"Unexpected token 0x{token:X8} where a file header was expected.");

      _ = _reader.ReadUInt32(); // file id
      var fileLength = _reader.ReadInt64();
      if (fileLength < 0)
        throw new InvalidDataException("EGG file header declares a negative uncompressed size.");

      var entry = new EggEntry { UncompressedSize = fileLength };
      if (this.IsGloballyEncrypted)
        entry.IsEncrypted = true;

      // File-header extra fields (filename, comment, windows/posix info, encryption).
      _SkipExtraFields((field, bitFlag, size, dataStart) => {
        switch (field) {
          case FilenameMagic: _ParseFilename(bitFlag, size, dataStart, entry); break;
          case WindowsInfoMagic: _ParseWindowsInfo(entry); break;
          case PosixInfoMagic: _ParsePosixInfo(entry); break;
          case EncryptMagic: entry.IsEncrypted = true; break;
        }
      });

      // One or more data blocks (each: block header + own extra fields + end-marker + data).
      _ReadBlocks(entry);

      if (entry.Blocks.Count > 0)
        entry.PrimaryAlgorithm = entry.Blocks[0].Algorithm;
      entry.CompressedSize = entry.Blocks.Sum(b => b.CompressedSize);

      _entries.Add(entry);
    }
  }

  private void _ReadBlocks(EggEntry entry) {
    while (_stream.Position + 4 <= _stream.Length) {
      var token = _reader.ReadUInt32();
      if (token != BlockHeaderMagic) {
        _stream.Position -= 4; // belongs to the next file header or the archive end-marker
        break;
      }

      var algorithm = _reader.ReadByte();
      _ = _reader.ReadByte(); // hint
      var uncompressedSize = _reader.ReadUInt32();
      var compressedSize = _reader.ReadUInt32();
      var crc = _reader.ReadUInt32();

      // Block-level extra fields up to the end-marker.
      _SkipExtraFields(null);

      var dataOffset = _stream.Position;
      entry.Blocks.Add(new EggBlock(dataOffset, compressedSize, uncompressedSize, algorithm, crc));

      var next = dataOffset + compressedSize;
      if (next > _stream.Length) {
        // A split first volume can truncate the trailing block; keep the metadata and stop.
        _stream.Position = _stream.Length;
        break;
      }
      _stream.Position = next;
    }
  }

  /// <summary>
  /// Extracts and decompresses an entry into a byte array. Store and Deflate blocks
  /// are decoded natively; any other algorithm, or an encrypted / split-volume entry,
  /// raises <see cref="NotSupportedException"/> rather than returning wrong bytes.
  /// Every decoded block is checked against its declared size and CRC-32.
  /// </summary>
  public byte[] Extract(EggEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.IsDirectory)
      return [];
    if (this.IsSplit)
      throw new NotSupportedException("EGG split (multi-volume) archives are not supported for extraction.");
    if (entry.IsEncrypted)
      throw new NotSupportedException($"EGG entry '{entry.Name}' is encrypted; extraction is not supported.");

    using var output = new MemoryStream();
    var crc = new Crc32();
    foreach (var block in entry.Blocks) {
      if (block.CompressedSize > int.MaxValue)
        throw new InvalidDataException($"EGG entry '{entry.Name}' has a block too large for in-memory extraction.");
      _stream.Position = block.DataOffset;
      var compressed = new byte[(int)block.CompressedSize];
      _ReadExactly(compressed);

      var plain = block.Algorithm switch {
        0 => compressed, // Store
        1 => DeflateDecompressor.Decompress(compressed), // Deflate (raw)
        _ => throw new NotSupportedException(
          $"EGG entry '{entry.Name}' uses unsupported compression '{new EggEntry { PrimaryAlgorithm = block.Algorithm }.MethodName}'."),
      };

      if (plain.LongLength != block.UncompressedSize)
        throw new InvalidDataException(
          $"EGG entry '{entry.Name}' block expands to {plain.LongLength} bytes; header declares {block.UncompressedSize}.");
      crc.Reset();
      crc.Update(plain);
      if (crc.Value != block.Crc32)
        throw new InvalidDataException(
          $"EGG entry '{entry.Name}' failed CRC-32 validation (stored 0x{block.Crc32:X8}, computed 0x{crc.Value:X8}).");

      output.Write(plain, 0, plain.Length);
    }

    if (output.Length != entry.UncompressedSize)
      throw new InvalidDataException(
        $"EGG entry '{entry.Name}' expands to {output.Length} bytes; file header declares {entry.UncompressedSize}.");
    return output.ToArray();
  }

  // ── Extra-field iteration ────────────────────────────────────────────────

  private void _SkipExtraFields(Action<uint, byte, long, long>? onField) {
    while (true) {
      if (_stream.Position + 4 > _stream.Length)
        throw new InvalidDataException("EGG archive ends inside an extra-field list.");
      var field = _reader.ReadUInt32();
      if (field == EndMarker)
        return;

      if (_stream.Position >= _stream.Length)
        throw new InvalidDataException("EGG archive ends before an extra-field flag byte.");
      var bitFlag = _reader.ReadByte();
      var sizeWidth = (bitFlag & 0x01) != 0 ? 4 : 2;
      if (_stream.Position + sizeWidth > _stream.Length)
        throw new InvalidDataException("EGG archive ends inside an extra-field size.");
      var size = sizeWidth == 4 ? _reader.ReadUInt32() : _reader.ReadUInt16();
      var dataStart = _stream.Position;
      if (dataStart > _stream.Length - size)
        throw new InvalidDataException($"EGG extra field 0x{field:X8} extends beyond the input stream.");

      onField?.Invoke(field, bitFlag, size, dataStart);

      _stream.Position = dataStart + size; // resync to the field end regardless of handling
    }
  }

  // ── Field parsers ────────────────────────────────────────────────────────

  private void _ParseFilename(byte bitFlag, long size, long dataStart, EggEntry entry) {
    _stream.Position = dataStart;

    // bit 2 (0x04) = name encrypted, bit 3 (0x08) = area-code (else UTF-8),
    // bit 4 (0x10) = relative path (parent-path id present).
    var encrypted = (bitFlag & 0x04) != 0;
    var useCodePage = (bitFlag & 0x08) != 0;
    var hasParent = (bitFlag & 0x10) != 0;

    var localeLen = useCodePage ? 2 : 0;
    var parentLen = hasParent ? 4 : 0;

    var codePage = 0;
    if (localeLen == 2)
      codePage = _reader.ReadUInt16();
    if (parentLen == 4)
      _ = _reader.ReadUInt32(); // parent-path id (relative-path linkage; not resolved here)

    var nameLen = size - localeLen - parentLen;
    if (nameLen < 0 || nameLen > int.MaxValue)
      throw new InvalidDataException("EGG filename field has an invalid length.");
    var nameBytes = _reader.ReadBytes((int)nameLen);
    if (nameBytes.Length != nameLen)
      throw new InvalidDataException("EGG archive ends inside a filename field.");

    if (encrypted)
      entry.IsEncrypted = true;

    entry.Name = (useCodePage ? _DecodeCodePage(codePage, nameBytes) : Encoding.UTF8.GetString(nameBytes))
      .Replace('\\', '/');
  }

  private void _ParseWindowsInfo(EggEntry entry) {
    var fileTime = _reader.ReadInt64(); // 100-ns ticks since 1601-01-01 UTC
    var attribute = _reader.ReadByte();
    if ((attribute & 0x80) != 0) // bit 7 = Directory
      entry.IsDirectory = true;
    if (fileTime > 0) {
      try { entry.LastModified ??= DateTime.FromFileTimeUtc(fileTime); } catch (ArgumentOutOfRangeException) { /* out-of-range timestamp */ }
    }
  }

  private void _ParsePosixInfo(EggEntry entry) {
    var mode = _reader.ReadUInt32();
    _ = _reader.ReadUInt32(); // uid
    _ = _reader.ReadUInt32(); // gid
    var seconds = _reader.ReadInt64(); // seconds since 1970-01-01 UTC
    if ((mode & 0xF000) == 0x4000) // S_IFDIR
      entry.IsDirectory = true;
    try { entry.LastModified ??= DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime; } catch (ArgumentOutOfRangeException) { /* out-of-range timestamp */ }
  }

  private static string _DecodeCodePage(int codePage, byte[] bytes) {
    if (codePage <= 0)
      return Encoding.UTF8.GetString(bytes);
    try {
      return Encoding.GetEncoding(codePage).GetString(bytes);
    } catch (Exception) {
      // The requested legacy code page (e.g. Shift-JIS 932, Korean 949) may be
      // unavailable without a code-pages provider; fall back to a lossless 1:1 map.
      return Encoding.Latin1.GetString(bytes);
    }
  }

  private void _ReadExactly(byte[] buffer) {
    var read = 0;
    while (read < buffer.Length) {
      var n = _stream.Read(buffer, read, buffer.Length - read);
      if (n <= 0)
        throw new EndOfStreamException("Unexpected end of EGG archive while reading block data.");
      read += n;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    _reader.Dispose();
    if (!_leaveOpen)
      _stream.Dispose();
  }
}
