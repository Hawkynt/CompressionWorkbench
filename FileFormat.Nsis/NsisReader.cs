using System.Buffers.Binary;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.Nsis;

/// <summary>
/// Reads data blocks from an NSIS (Nullsoft Scriptable Install System) installer executable.
/// </summary>
/// <remarks>
/// NSIS installers are Windows PE executables that carry compressed payload data in the file
/// overlay (bytes after the last PE section). This reader locates that overlay, validates the
/// NSIS first-header signature, and enumerates the embedded data blocks.
///
/// Full file-name recovery requires executing the compiled NSIS install script, which is out of
/// scope for a static reader. Entries are therefore named "block_N" in the order they appear in
/// the data section.
///
/// Supported compression methods: None (store), Zlib (Deflate), BZip2, LZMA.
/// </remarks>
public sealed class NsisReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;

  private readonly int _compressionType;
  private readonly bool _isSolid;
  private readonly long _overlayOffset;   // absolute stream position of the NSIS first-header
  private readonly long _headerDataOffset; // absolute stream position of the compressed header
  private readonly int  _headerDataSize;   // compressed size of header block (0 = not known)
  private readonly long _dataOffset;      // absolute stream position of first data block
  private readonly List<NsisEntry> _entries;

  private bool _disposed;

  /// <summary>Gets the data blocks found in the installer.</summary>
  public IReadOnlyList<NsisEntry> Entries => _entries;

  /// <summary>
  /// Gets the compression method used by this installer.
  /// </summary>
  public NsisCompression Compression => (NsisCompression)_compressionType;

  /// <summary>
  /// Gets whether the installer uses solid (single-stream) compression.
  /// </summary>
  public bool IsSolid => _isSolid;

  // -------------------------------------------------------------------------
  // Construction / parsing
  // -------------------------------------------------------------------------

  /// <summary>
  /// Opens an NSIS installer from a stream. The stream must be seekable.
  /// </summary>
  /// <param name="stream">A seekable stream positioned at the start of the PE executable.</param>
  /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open on dispose.</param>
  public NsisReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));

    _stream    = stream;
    _leaveOpen = leaveOpen;
    _entries   = [];

    _overlayOffset = FindOverlay();
    stream.Position = _overlayOffset;

    // Read the NSIS first-header (28 bytes)
    Span<byte> hdr = stackalloc byte[NsisConstants.FirstHeaderSize];
    ReadExact(stream, hdr);

    // Validate signature at offset 4 within the first-header
    if (!hdr.Slice(NsisConstants.SignatureOffset, NsisConstants.SignatureLength)
          .SequenceEqual(NsisConstants.Signature))
      throw new InvalidDataException("NSIS signature not found at the expected overlay position.");

    int flags = BinaryPrimitives.ReadInt32LittleEndian(hdr);
    _compressionType = flags & NsisConstants.CompressionMask;
    _isSolid         = (flags & NsisConstants.SolidFlag) != 0;
    _headerDataSize  = BinaryPrimitives.ReadInt32LittleEndian(hdr[20..]);
    // archive_size at hdr[24..] — not currently used beyond validation

    _headerDataOffset = _overlayOffset + NsisConstants.FirstHeaderSize;
    _dataOffset       = _headerDataOffset + _headerDataSize;

    ScanDataBlocks();
  }

  // -------------------------------------------------------------------------
  // Extraction
  // -------------------------------------------------------------------------

  /// <summary>
  /// Extracts the raw (decompressed) data for the given entry.
  /// </summary>
  /// <param name="entry">An entry obtained from <see cref="Entries"/>.</param>
  /// <returns>The decompressed bytes.</returns>
  public byte[] Extract(NsisEntry entry) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(entry);

    if (_isSolid)
      throw new NotSupportedException(
        "Extraction of individual entries from solid NSIS installers is not supported. " +
        "Use ExtractSolidStream to obtain the full decompressed stream.");

    int index = _entries.IndexOf(entry);
    if (index < 0)
      throw new ArgumentException("Entry does not belong to this archive.", nameof(entry));

    // Seek to the block
    long blockPos = GetBlockOffset(index);
    _stream.Position = blockPos;

    // Read 4-byte length prefix
    Span<byte> lenBuf = stackalloc byte[4];
    ReadExact(_stream, lenBuf);
    uint word = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);

    bool stored = (word & NsisConstants.UncompressedFlag) != 0;
    int  compressedSize = (int)(word & ~NsisConstants.UncompressedFlag);

    byte[] compressedData = new byte[compressedSize];
    ReadExact(_stream, compressedData);

    return stored ? compressedData : DecompressBlock(compressedData);
  }

  /// <summary>
  /// Decompresses the entire solid data stream and returns it as a single byte array.
  /// Only meaningful when <see cref="IsSolid"/> is <see langword="true"/>.
  /// </summary>
  public byte[] ExtractSolidStream() {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _stream.Position = _dataOffset;
    using var slice = new SubStream(_stream, _stream.Length - _dataOffset, leaveOpen: true);
    return DecompressFull(slice);
  }

  // -------------------------------------------------------------------------
  // IDisposable
  // -------------------------------------------------------------------------

  /// <inheritdoc/>
  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    if (!_leaveOpen)
      _stream.Dispose();
  }

  // -------------------------------------------------------------------------
  // Private helpers
  // -------------------------------------------------------------------------

  /// <summary>
  /// Finds the overlay (data after the last PE section) by parsing the DOS/PE headers.
  /// Falls back to scanning for the NSIS signature when the stream does not start with a PE.
  /// </summary>
  private long FindOverlay() {
    _stream.Position = 0;

    // Try proper PE parsing first
    long overlay = TryFindOverlayViaPe();
    if (overlay >= 0) {
      // Confirm there is an NSIS signature at that position
      if (HasNsisSignatureAt(overlay))
        return overlay;

      // The overlay might start further in if there is a stub before the NSIS header.
      // Fall through to scan.
    }

    // Fallback: linear scan from start
    return ScanForSignature();
  }

  private long TryFindOverlayViaPe() {
    try {
      _stream.Position = 0;
      using var reader = new BinaryReader(_stream, System.Text.Encoding.ASCII, leaveOpen: true);

      // DOS header: "MZ"
      if (reader.ReadUInt16() != 0x5A4D) // 'MZ'
        return -1;

      // e_lfanew at offset 0x3C
      _stream.Position = 0x3C;
      int peOffset = reader.ReadInt32();
      if (peOffset <= 0 || peOffset + 24 > _stream.Length)
        return -1;

      _stream.Position = peOffset;
      uint peSig = reader.ReadUInt32();
      if (peSig != 0x00004550) // 'PE\0\0'
        return -1;

      reader.ReadUInt16(); // Machine
      ushort numSections = reader.ReadUInt16();
      reader.ReadUInt32(); // TimeDateStamp
      reader.ReadUInt32(); // PointerToSymbolTable
      reader.ReadUInt32(); // NumberOfSymbols
      ushort sizeOfOptionalHeader = reader.ReadUInt16();
      reader.ReadUInt16(); // Characteristics

      long sectionTableOffset = peOffset + 24 + sizeOfOptionalHeader;
      if (sectionTableOffset + numSections * 40L > _stream.Length)
        return -1;

      long maxEnd = 0;
      _stream.Position = sectionTableOffset;
      for (int i = 0; i < numSections; ++i) {
        // Section header is 40 bytes:
        //   0..7   Name
        //   8      VirtualSize
        //  12      VirtualAddress
        //  16      SizeOfRawData
        //  20      PointerToRawData
        //  24..39  other fields
        byte[] secHdr = reader.ReadBytes(40);
        uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(secHdr.AsSpan(16));
        uint rawPtr  = BinaryPrimitives.ReadUInt32LittleEndian(secHdr.AsSpan(20));
        long end = rawPtr + rawSize;
        if (end > maxEnd)
          maxEnd = end;
      }

      return maxEnd > 0 ? maxEnd : -1;
    } catch {
      return -1;
    }
  }

  private bool HasNsisSignatureAt(long position) {
    if (position + NsisConstants.FirstHeaderSize > _stream.Length)
      return false;

    _stream.Position = position + NsisConstants.SignatureOffset;
    Span<byte> buf = stackalloc byte[NsisConstants.SignatureLength];
    if (!TryReadExact(_stream, buf))
      return false;

    return buf.SequenceEqual(NsisConstants.Signature);
  }

  private long ScanForSignature() {
    // Scan in 4-byte steps looking for the first 4 bytes of the signature (0xEFBEADDE)
    const int bufSize = 65536;
    byte[] buf = new byte[bufSize + NsisConstants.SignatureLength];
    long pos = 0;

    _stream.Position = 0;
    while (pos < _stream.Length) {
      int read = _stream.Read(buf, 0, buf.Length);
      if (read < NsisConstants.SignatureLength)
        break;

      for (int i = 0; i <= read - NsisConstants.SignatureLength; i++) {
        if (buf[i]     == 0xEF && buf[i + 1] == 0xBE &&
            buf[i + 2] == 0xAD && buf[i + 3] == 0xDE) {
          long candidate = pos + i - NsisConstants.SignatureOffset;
          if (candidate >= 0 && HasNsisSignatureAt(candidate))
            return candidate;
        }
      }

      // Rewind so we don't miss a signature that straddles a buffer boundary
      pos += bufSize;
      _stream.Position = pos;
    }

    throw new InvalidDataException("NSIS signature not found in stream.");
  }

  /// <summary>
  /// Iterates the non-solid data section to build the entry list.
  /// For solid archives a single synthetic entry is added instead.
  /// </summary>
  private void ScanDataBlocks() {
    if (_isSolid) {
      // Solid: single entry for the whole compressed stream
      long solidSize = _stream.Length - _dataOffset;
      _entries.Add(new NsisEntry("solid_stream", -1, solidSize, false));
      return;
    }

    // Non-solid: walk successive 4+N blocks until we run out of data or hit EOF
    _stream.Position = _dataOffset;
    int index = 0;
    byte[] lenBufArr = new byte[4];

    while (_stream.Position + 4 <= _stream.Length) {
      if (!TryReadExact(_stream, lenBufArr))
        break;
      Span<byte> lenBuf = lenBufArr;

      uint word = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);

      // A zero word at the end of the section is the sentinel used by some NSIS versions.
      if (word == 0)
        break;

      bool stored = (word & NsisConstants.UncompressedFlag) != 0;
      int  compressedSize = (int)(word & ~NsisConstants.UncompressedFlag);

      if (compressedSize < 0 || _stream.Position + compressedSize > _stream.Length)
        break;

      long uncompressedSize = stored ? compressedSize : -1L;
      _entries.Add(new NsisEntry(
        $"block_{index}",
        uncompressedSize,
        compressedSize,
        false));

      _stream.Position += compressedSize;
      ++index;
    }
  }

  /// <summary>
  /// Returns the absolute stream position of the length-prefix word for the block at
  /// <paramref name="targetIndex"/> by replaying the block list from <see cref="_dataOffset"/>.
  /// </summary>
  private long GetBlockOffset(int targetIndex) {
    _stream.Position = _dataOffset;
    byte[] lenBufArr = new byte[4];

    for (int i = 0; i < targetIndex; ++i) {
      ReadExact(_stream, lenBufArr);
      uint word = BinaryPrimitives.ReadUInt32LittleEndian(lenBufArr);
      int  size = (int)(word & ~NsisConstants.UncompressedFlag);
      _stream.Position += size;
    }

    return _stream.Position;
  }

  // -------------------------------------------------------------------------
  // Decompression helpers
  // -------------------------------------------------------------------------

  private byte[] DecompressBlock(byte[] data) =>
    _compressionType switch {
      NsisConstants.CompNone  => data,
      NsisConstants.CompZlib  => DecompressZlib(data),
      NsisConstants.CompBzip2 => DecompressBzip2(data),
      NsisConstants.CompLzma  => DecompressLzma(data),
      _ => throw new NotSupportedException($"Unsupported NSIS compression type: {_compressionType}")
    };

  private byte[] DecompressFull(Stream s) =>
    _compressionType switch {
      NsisConstants.CompNone  => ReadAllBytes(s),
      NsisConstants.CompZlib  => DecompressZlibStream(s),
      NsisConstants.CompBzip2 => DecompressBzip2Stream(s),
      NsisConstants.CompLzma  => DecompressLzmaStream(s),
      _ => throw new NotSupportedException($"Unsupported NSIS compression type: {_compressionType}")
    };

  // Zlib = 2-byte zlib header + raw Deflate
  private static byte[] DecompressZlib(byte[] data) {
    if (data.Length < NsisConstants.ZlibHeaderSize)
      throw new InvalidDataException("Zlib block too short.");
    var deflateData = data.AsSpan(NsisConstants.ZlibHeaderSize);
    return DeflateDecompressor.Decompress(deflateData);
  }

  private static byte[] DecompressZlibStream(Stream s) {
    // Skip 2-byte zlib header
    Span<byte> skip = stackalloc byte[NsisConstants.ZlibHeaderSize];
    ReadExact(s, skip);
    var decompressor = new DeflateDecompressor(s);
    return decompressor.DecompressAll();
  }

  private static byte[] DecompressBzip2(byte[] data) {
    using var ms = new MemoryStream(data);
    return DecompressBzip2Stream(ms);
  }

  private static byte[] DecompressBzip2Stream(Stream s) {
    using var bzip2 = new FileFormat.Bzip2.Bzip2Stream(
      s, global::Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    using var result = new MemoryStream();
    bzip2.CopyTo(result);
    return result.ToArray();
  }

  private static byte[] DecompressLzma(byte[] data) {
    using var ms = new MemoryStream(data);
    return DecompressLzmaStream(ms);
  }

  private static byte[] DecompressLzmaStream(Stream s) {
    // NSIS LZMA sub-header: 5-byte properties + 8-byte uncompressed size (LE int64)
    Span<byte> lzmaHdr = stackalloc byte[NsisConstants.LzmaHeaderSize];
    ReadExact(s, lzmaHdr);

    byte[] props = lzmaHdr[..NsisConstants.LzmaPropSize].ToArray();
    long uncompressedSize = BinaryPrimitives.ReadInt64LittleEndian(lzmaHdr[NsisConstants.LzmaPropSize..]);

    var decoder = new LzmaDecoder(s, props, uncompressedSize);
    return decoder.Decode();
  }

  // -------------------------------------------------------------------------
  // Low-level I/O helpers
  // -------------------------------------------------------------------------

  private static void ReadExact(Stream s, Span<byte> buf) {
    int total = 0;
    while (total < buf.Length) {
      int n = s.Read(buf[total..]);
      if (n == 0)
        throw new EndOfStreamException($"Unexpected end of stream while reading {buf.Length} bytes.");
      total += n;
    }
  }

  private static bool TryReadExact(Stream s, Span<byte> buf) {
    int total = 0;
    while (total < buf.Length) {
      int n = s.Read(buf[total..]);
      if (n == 0) return false;
      total += n;
    }
    return true;
  }

  private static byte[] ReadAllBytes(Stream s) {
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }
}
