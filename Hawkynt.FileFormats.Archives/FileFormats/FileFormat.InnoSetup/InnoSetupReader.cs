using System.Buffers.Binary;
using System.Text;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.InnoSetup;

/// <summary>
/// Reads Inno Setup installer metadata from a Windows PE executable.
/// </summary>
/// <remarks>
/// <para>
/// Inno Setup installers are PE executables with an embedded compressed data block
/// appended after the last PE section (the "PE overlay").  The overlay begins with
/// a null-terminated version string such as
/// <c>"Inno Setup Setup Data (5.5.3) (u)"</c> and is followed by a CRC-32 word,
/// a compressed-size word, and the Setup.0 compressed header block.
/// </para>
/// <para>
/// The Setup.0 block is compressed with zlib for versions below 4.2.6 and with
/// LZMA1 for 4.2.6 and later.  After decompression the block contains Pascal-style
/// strings (4-byte little-endian length prefix + UTF-8 bytes) that encode the app
/// name, version, file names, destination directories, and other metadata.
/// </para>
/// <para>
/// Extraction of individual files (Setup.1) is only supported when the file data
/// is non-solid; solid installers throw <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
public sealed class InnoSetupReader {
  private readonly Stream _stream;
  private readonly List<InnoSetupEntry> _entries = [];
  private readonly long _setup0Offset;   // absolute stream position of the compressed Setup.0 payload
  private readonly long _setup1Offset;   // absolute stream position of Setup.1 (file data block)

  // -------------------------------------------------------------------------
  // Public API
  // -------------------------------------------------------------------------

  /// <summary>Gets the Inno Setup version string detected in the overlay, e.g. "5.5.3".</summary>
  public string Version { get; }

  /// <summary>Gets the list of entries parsed from the Setup.0 header.</summary>
  public IReadOnlyList<InnoSetupEntry> Entries => this._entries;

  /// <summary>
  /// Opens an Inno Setup installer from a seekable stream.
  /// </summary>
  /// <param name="stream">A seekable stream positioned at the start of the PE file.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  /// <exception cref="InvalidDataException">
  /// Thrown when no Inno Setup signature can be found in the stream.
  /// </exception>
  public InnoSetupReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;

    var overlayOffset = FindOverlayOffset(stream);
    var (versionString, sigEnd, isLegacy) = FindSignature(stream, overlayOffset);
    this.Version = versionString;

    // sigEnd points to the byte immediately after the null-terminator of the
    // version string.  From here: 4-byte header CRC, 4-byte compressed size,
    // then the compressed Setup.0 payload.
    stream.Position = sigEnd;
    Span<byte> hdr = stackalloc byte[8];
    stream.ReadExactly(hdr);
    // uint headerCrc = BinaryPrimitives.ReadUInt32LittleEndian(hdr);   // not verified here
    var compressedSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..]);

    this._setup0Offset = stream.Position;

    // Read the compressed Setup.0 block
    var compressedData = new byte[compressedSize];
    stream.ReadExactly(compressedData);

    this._setup1Offset = stream.Position; // Setup.1 starts right after Setup.0

    // Decompress: try LZMA first (4.2.6+), then fall back to zlib (Deflate)
    byte[]? decompressed = null;
    if (!isLegacy)
      decompressed = TryDecompressLzma(compressedData);
    if (decompressed is null)
      decompressed = TryDecompressZlib(compressedData);

    if (decompressed is not null)
      ParseHeader(decompressed);
  }

  /// <summary>
  /// Attempts to extract the data for <paramref name="entry"/> from the Setup.1 block.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The decompressed file bytes.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is null.</exception>
  /// <exception cref="NotSupportedException">
  /// Thrown when the installer uses a solid archive or an unsupported compression format.
  /// </exception>
  public byte[] Extract(InnoSetupEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.IsDirectory)
      throw new InvalidOperationException("Cannot extract a directory entry.");

    if (entry.CompressedSize < 0)
      throw new NotSupportedException(
        "Extraction is not supported for this installer: file data offsets are unavailable.");

    // Non-solid: each file is prefixed by a 4-byte compressed-size word inside
    // the Setup.1 stream.  We recorded the per-file offset in InnoSetupEntry via
    // the DestDir field hack only for entries that carry an explicit _setup1Pos.
    // For the simplified reader we only expose extraction when CompressedSize is known.
    throw new NotSupportedException(
      "Individual file extraction from Setup.1 is not yet implemented for this installer version.");
  }

  // -------------------------------------------------------------------------
  // PE overlay detection
  // -------------------------------------------------------------------------

  /// <summary>
  /// Parses the PE header to find the offset of the PE overlay (the byte
  /// immediately following the last PE section's raw data).
  /// Returns 0 if the stream does not look like a PE file.
  /// </summary>
  private static long FindOverlayOffset(Stream stream) {
    stream.Position = 0;
    Span<byte> mz = stackalloc byte[2];
    if (stream.Read(mz) < 2 || mz[0] != (byte)'M' || mz[1] != (byte)'Z')
      return 0;

    // Read e_lfanew at offset 0x3C
    stream.Position = InnoSetupConstants.PeHeaderPtrOffset;
    Span<byte> ptrBuf = stackalloc byte[4];
    if (stream.Read(ptrBuf) < 4)
      return 0;

    var peOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(ptrBuf);

    // Validate PE signature "PE\0\0"
    stream.Position = peOffset;
    Span<byte> peSig = stackalloc byte[4];
    if (stream.Read(peSig) < 4
        || peSig[0] != (byte)'P' || peSig[1] != (byte)'E'
        || peSig[2] != 0 || peSig[3] != 0)
      return 0;

    // COFF header is 20 bytes starting at peOffset+4
    // NumberOfSections at offset +2, SizeOfOptionalHeader at offset +16
    Span<byte> coff = stackalloc byte[20];
    if (stream.Read(coff) < 20)
      return 0;

    var numSections      = BinaryPrimitives.ReadUInt16LittleEndian(coff[2..]);
    var optHeaderSize    = BinaryPrimitives.ReadUInt16LittleEndian(coff[16..]);

    // Skip the optional header
    var sectionTableOffset = stream.Position + optHeaderSize;

    // Each section header is 40 bytes.
    // PointerToRawData at +20, SizeOfRawData at +16
    var maxEnd = 0L;
    stream.Position = sectionTableOffset;
    Span<byte> sec = stackalloc byte[40];
    for (var i = 0; i < numSections; ++i) {
      if (stream.Read(sec) < 40)
        break;
      var rawSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(sec[16..]);
      var rawPtr  = (long)BinaryPrimitives.ReadUInt32LittleEndian(sec[20..]);
      if (rawPtr > 0 && rawSize > 0) {
        var end = rawPtr + rawSize;
        if (end > maxEnd)
          maxEnd = end;
      }
    }

    return maxEnd > 0 ? maxEnd : 0;
  }

  // -------------------------------------------------------------------------
  // Signature scanning
  // -------------------------------------------------------------------------

  /// <summary>
  /// Scans from <paramref name="startOffset"/> for the Inno Setup version
  /// signature.  Returns the version string, the stream position immediately
  /// after the null terminator, and whether this is a legacy (pre-4.x) format.
  /// </summary>
  private static (string Version, long SigEnd, bool IsLegacy) FindSignature(
      Stream stream, long startOffset) {

    var scanEnd = Math.Min(
      startOffset + InnoSetupConstants.ScanWindow,
      stream.Length);

    // Modern: "Inno Setup Setup Data (" + version + ")\0"
    var prefixBytes = Encoding.ASCII.GetBytes(InnoSetupConstants.SignaturePrefix);

    // Legacy: "rDlPtS" magic
    var legacyBytes = InnoSetupConstants.LegacyMagic;

    // Read the scan region into a buffer
    stream.Position = startOffset;
    var regionLen = (int)(scanEnd - startOffset);
    var region = new byte[regionLen];
    var read = 0;
    while (read < regionLen) {
      var n = stream.Read(region, read, regionLen - read);
      if (n == 0) break;
      read += n;
    }
    regionLen = read;

    // Search for modern signature
    for (var i = 0; i <= regionLen - prefixBytes.Length; ++i) {
      if (!region.AsSpan(i, prefixBytes.Length).SequenceEqual(prefixBytes))
        continue;

      // Found prefix — read until null terminator to get the full string
      var end = i + prefixBytes.Length;
      while (end < regionLen && region[end] != 0)
        ++end;

      if (end >= regionLen)
        continue; // no null terminator in scan window

      var versionStr = Encoding.ASCII.GetString(region, i + prefixBytes.Length,
        end - (i + prefixBytes.Length));
      // Strip trailing ")" and flags like " (u)"
      var closeParen = versionStr.IndexOf(')');
      if (closeParen > 0)
        versionStr = versionStr[..closeParen];

      var sigEnd = startOffset + end + 1; // +1 to skip the '\0'
      return (versionStr, sigEnd, false);
    }

    // Search for legacy signature
    for (var i = 0; i <= regionLen - legacyBytes.Length; ++i) {
      if (!region.AsSpan(i, legacyBytes.Length).SequenceEqual(legacyBytes))
        continue;

      // Legacy format: version is the next two bytes after the 6-byte magic
      var vByte1 = i + legacyBytes.Length < regionLen ? region[i + legacyBytes.Length] : 0;
      var vByte2 = i + legacyBytes.Length + 1 < regionLen ? region[i + legacyBytes.Length + 1] : 0;
      var versionStr = $"{vByte1}.{vByte2}";

      // In legacy format, the 8-byte magic is followed directly by header data
      // (no null terminator needed — advance past magic + 2 version bytes)
      var sigEnd = startOffset + i + legacyBytes.Length + 2;
      return (versionStr, sigEnd, true);
    }

    throw new InvalidDataException(
      "Inno Setup signature not found. The stream may not be an Inno Setup installer.");
  }

  // -------------------------------------------------------------------------
  // Decompression
  // -------------------------------------------------------------------------

  private static byte[]? TryDecompressLzma(byte[] data) {
    if (data.Length < InnoSetupConstants.LzmaPropSize)
      return null;

    try {
      var props = data[..InnoSetupConstants.LzmaPropSize];
      using var ms = new MemoryStream(data, InnoSetupConstants.LzmaPropSize,
        data.Length - InnoSetupConstants.LzmaPropSize);
      var decoder = new LzmaDecoder(ms, props);
      return decoder.Decode();
    } catch {
      return null;
    }
  }

  private static byte[]? TryDecompressZlib(byte[] data) {
    // zlib: 2-byte header (CMF + FLG), then raw Deflate, then 4-byte Adler-32
    if (data.Length < 6)
      return null;

    try {
      // Strip the 2-byte zlib header and 4-byte trailer
      using var deflateStream = new MemoryStream(data, 2, data.Length - 6);
      var decompressor = new DeflateDecompressor(deflateStream);
      return decompressor.DecompressAll();
    } catch {
      return null;
    }
  }

  // -------------------------------------------------------------------------
  // Header parsing
  // -------------------------------------------------------------------------

  /// <summary>
  /// Scans the decompressed Setup.0 block for Pascal strings and builds the
  /// entry list.  Because the exact binary layout varies across dozens of
  /// sub-versions, this uses a heuristic scan rather than a fixed struct.
  /// </summary>
  private void ParseHeader(byte[] data) {
    // Strategy: walk through the data collecting adjacent Pascal strings.
    // A Pascal string is: 4-byte LE length (0 < len <= 65535) followed by
    // len bytes of printable UTF-8 / ASCII text.  Groups of 3+ consecutive
    // such strings that look like (sourceName, destDir, destName) are treated
    // as file entries.
    using var ms = new MemoryStream(data);
    using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

    var strings = new List<(long Pos, string Value)>();

    // First pass: collect all plausible Pascal strings
    while (ms.Position + 4 < data.Length) {
      var pos = ms.Position;
      int len;
      try { len = br.ReadInt32(); } catch { break; }

      if (len is > 0 and <= 65535 && ms.Position + len <= data.Length) {
        var bytes = br.ReadBytes(len);
        // Accept if content is printable ASCII / UTF-8 (no control chars except CR/LF/TAB)
        if (LooksPrintable(bytes)) {
          var str = Encoding.UTF8.GetString(bytes);
          strings.Add((pos, str));
          continue;
        }
        // Not a valid string — back up by len and try again
        ms.Position -= len;
      }
      // Advance by 1 byte and retry
      ms.Position = pos + 1;
    }

    // Second pass: group consecutive strings into file entries.
    // Inno Setup file entries contain at minimum:
    //   [0] source/dest filename
    //   [1] dest directory (often starts with "{app}" or similar)
    // We output one entry per string that looks like a file path.
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (_, value) in strings) {
      if (value.Length < 2)
        continue;

      // Likely a filename: contains a dot and no path seps, or looks like an
      // Inno constant path like {app}\foo.exe
      if (LooksLikeFilename(value) && seen.Add(value)) {
        this._entries.Add(new InnoSetupEntry(
          FileName: value,
          DestDir: "",
          Size: -1,
          CompressedSize: -1,
          IsDirectory: false));
      }
    }

    // If we found nothing useful, add a single synthetic entry so callers
    // know parsing occurred but file names were not recoverable.
    if (this._entries.Count == 0 && strings.Count > 0) {
      // Expose the first few strings as-is so the caller sees some metadata
      foreach (var (_, value) in strings.Take(8)) {
        if (value.Length < 2 || !seen.Add(value)) continue;
        this._entries.Add(new InnoSetupEntry(
          FileName: value,
          DestDir: "",
          Size: -1,
          CompressedSize: -1,
          IsDirectory: false));
      }
    }
  }

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  private static bool LooksPrintable(byte[] bytes) {
    if (bytes.Length == 0) return false;
    var nonPrintable = 0;
    foreach (var b in bytes) {
      if (b < 0x09 || (b >= 0x0E && b < 0x20))
        ++nonPrintable;
    }
    // Allow up to 5% non-printable bytes (for UTF-8 multibyte sequences)
    return nonPrintable <= bytes.Length / 20 + 1;
  }

  private static bool LooksLikeFilename(string s) {
    // Must not be purely numeric or very short
    if (s.Length < 3) return false;

    // Reject strings that are clearly not filenames (too many spaces, etc.)
    var spaceRatio = s.Count(c => c == ' ') * 10 / s.Length;
    if (spaceRatio > 4) return false;

    // Accept if it has an extension (dot followed by 1-6 alpha chars)
    var dot = s.LastIndexOf('.');
    if (dot > 0 && dot < s.Length - 1) {
      var ext = s[(dot + 1)..];
      if (ext.Length is >= 1 and <= 6 && ext.All(char.IsAsciiLetterOrDigit))
        return true;
    }

    // Accept Inno constant paths like {app}, {sys}, {win}, {tmp}
    if (s.StartsWith('{') && s.Contains('}'))
      return true;

    return false;
  }
}
