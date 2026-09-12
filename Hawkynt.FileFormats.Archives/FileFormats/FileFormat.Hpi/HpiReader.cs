using System.IO.Compression;
using System.Text;

namespace FileFormat.Hpi;

/// <summary>
/// Reads the unencrypted, zlib-only subset of Total Annihilation HPI/UFO/CCX/GP3 archives.
/// Encrypted archives (HeaderKey != 0) and TA's bespoke LZ77 chunk variant are rejected.
/// </summary>
public sealed class HpiReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>Gets all entries discovered in the archive (files and directories), with full forward-slash paths.</summary>
  public IReadOnlyList<HpiEntry> Entries { get; }

  /// <summary>
  /// Initializes a new <see cref="HpiReader"/> from a stream.
  /// </summary>
  /// <param name="stream">The stream containing the HPI archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <exception cref="InvalidDataException">If the magic or layout is invalid.</exception>
  /// <exception cref="NotSupportedException">If the archive is encrypted (HeaderKey != 0).</exception>
  public HpiReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length < HpiConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid HPI archive.");

    stream.Position = 0;
    Span<byte> header = stackalloc byte[HpiConstants.HeaderSize];
    ReadExact(header);

    var magic     = BitConverter.ToUInt32(header[0..4]);
    var version   = BitConverter.ToUInt32(header[4..8]);
    var dirSize   = BitConverter.ToUInt32(header[8..12]);
    var headerKey = BitConverter.ToUInt32(header[12..16]);
    var dirStart  = BitConverter.ToUInt32(header[16..20]);

    if (magic != HpiConstants.Magic)
      throw new InvalidDataException($"Invalid HPI magic: 0x{magic:X8} (expected 'HAPI').");

    // HeaderKey gates everything: non-zero means TA's XOR-key encryption applies to the directory and file data.
    // We refuse rather than silently mis-parse — encrypted parsing is a separate, known wave.
    if (headerKey != 0)
      throw new NotSupportedException($"Encrypted HPI not supported (HeaderKey=0x{headerKey:X8}).");

    if (version != HpiConstants.VersionTaClassic)
      throw new NotSupportedException($"Unsupported HPI version: 0x{version:X8} (expected 0x{HpiConstants.VersionTaClassic:X8}).");

    if (dirStart + HpiConstants.DirectoryHeaderSize > stream.Length)
      throw new InvalidDataException($"DirectoryStart 0x{dirStart:X} is past the end of the stream.");

    _ = dirSize; // Not strictly needed for parsing — directory walking is offset-driven.

    var entries = new List<HpiEntry>();
    ReadDirectory(dirStart, "", entries);
    this.Entries = entries;
  }

  /// <summary>
  /// Extracts the raw, decompressed bytes of a file entry. Returns <c>[]</c> for directories.
  /// </summary>
  /// <exception cref="NotSupportedException">If a chunk uses LZ77 (compression flag = 1) or per-chunk encryption.</exception>
  public byte[] Extract(HpiEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      return [];

    this._stream.Position = entry.DataOffset;
    Span<byte> dataHeader = stackalloc byte[8];
    ReadExact(dataHeader);

    var fileSize        = (int)BitConverter.ToUInt32(dataHeader[0..4]);
    var wholeFileFlag   = BitConverter.ToUInt32(dataHeader[4..8]);

    if (fileSize < 0)
      throw new InvalidDataException($"Negative file size in HPI data block at 0x{entry.DataOffset:X}.");

    if (wholeFileFlag == HpiConstants.CompressionStored) {
      var data = new byte[fileSize];
      ReadExact(data);
      return data;
    }

    // Compressed files are split into independently-compressed chunks of up to MaxChunkSize bytes.
    // We don't trust wholeFileFlag for the per-chunk codec — every SQSH header carries its own.
    var output = new byte[fileSize];
    var written = 0;
    while (written < fileSize) {
      var thisChunk = Math.Min(HpiConstants.MaxChunkSize, fileSize - written);
      var produced = ReadAndDecompressChunk(output.AsSpan(written, thisChunk));
      if (produced != thisChunk)
        throw new InvalidDataException($"HPI chunk produced {produced} bytes but {thisChunk} were expected for entry '{entry.Name}'.");
      written += produced;
    }
    return output;
  }

  private int ReadAndDecompressChunk(Span<byte> destination) {
    Span<byte> chunkHeader = stackalloc byte[HpiConstants.ChunkHeaderSize];
    ReadExact(chunkHeader);

    var magic        = BitConverter.ToUInt32(chunkHeader[0..4]);
    // chunkHeader[4] = marker — informational, not validated.
    var compression  = chunkHeader[5];
    var encrypt      = chunkHeader[6];
    var compressedSz = (int)BitConverter.ToUInt32(chunkHeader[7..11]);
    var decompressed = (int)BitConverter.ToUInt32(chunkHeader[11..15]);
    // chunkHeader[15..19] = checksum — best-effort, we don't validate.

    if (magic != HpiConstants.ChunkMagic)
      throw new InvalidDataException($"Invalid SQSH chunk magic: 0x{magic:X8}.");

    if (encrypt != HpiConstants.EncryptPlain)
      throw new NotSupportedException($"Encrypted HPI chunks not supported (encrypt={encrypt}).");

    if (compressedSz < 0 || decompressed < 0 || decompressed > destination.Length)
      throw new InvalidDataException($"HPI chunk size fields out of range (compressed={compressedSz}, decompressed={decompressed}, expected<={destination.Length}).");

    var payload = new byte[compressedSz];
    ReadExact(payload);

    switch (compression) {
      case HpiConstants.CompressionStored:
        if (compressedSz != decompressed)
          throw new InvalidDataException($"Stored HPI chunk size mismatch (compressed={compressedSz}, decompressed={decompressed}).");
        payload.AsSpan().CopyTo(destination[..decompressed]);
        return decompressed;

      case HpiConstants.CompressionZlib:
        return DecompressZlib(payload, destination[..decompressed]);

      case HpiConstants.CompressionLz77:
        // TA's LZ77 is bespoke (not standard LZSS) and out of scope for this wave.
        throw new NotSupportedException("HPI LZ77 chunks not supported, only zlib (flag=2).");

      default:
        throw new InvalidDataException($"Unknown HPI chunk compression flag: {compression}.");
    }
  }

  private static int DecompressZlib(byte[] payload, Span<byte> destination) {
    using var src = new MemoryStream(payload, writable: false);
    using var z = new ZLibStream(src, CompressionMode.Decompress);
    var written = 0;
    while (written < destination.Length) {
      var read = z.Read(destination[written..]);
      if (read == 0)
        throw new InvalidDataException($"HPI zlib chunk ended after {written} bytes; expected {destination.Length}.");
      written += read;
    }
    return written;
  }

  private void ReadDirectory(long dirHeaderOffset, string pathPrefix, List<HpiEntry> sink) {
    this._stream.Position = dirHeaderOffset;
    Span<byte> dirHeader = stackalloc byte[HpiConstants.DirectoryHeaderSize];
    ReadExact(dirHeader);

    var entryCount    = (int)BitConverter.ToUInt32(dirHeader[0..4]);
    var entryListOff  = (long)BitConverter.ToUInt32(dirHeader[4..8]);

    if (entryCount < 0)
      throw new InvalidDataException($"Negative HPI entry count at 0x{dirHeaderOffset:X}.");

    // Materialize all entry records first so we don't fight the stream cursor while recursing into sub-directories.
    var records = new (uint NameOffset, uint DataOffset, byte Flag)[entryCount];
    this._stream.Position = entryListOff;
    Span<byte> rec = stackalloc byte[HpiConstants.EntryRecordSize];
    for (var i = 0; i < entryCount; ++i) {
      ReadExact(rec);
      records[i] = (
        BitConverter.ToUInt32(rec[0..4]),
        BitConverter.ToUInt32(rec[4..8]),
        rec[8]);
    }

    Span<byte> sizeBuf = stackalloc byte[4];
    foreach (var (nameOff, dataOff, flag) in records) {
      var name = ReadCString(nameOff);
      var fullPath = pathPrefix.Length == 0 ? name : pathPrefix + "/" + name;
      var isDir = flag == 1;

      if (isDir) {
        sink.Add(new HpiEntry { Name = fullPath, DataOffset = dataOff, Size = 0, IsDirectory = true });
        ReadDirectory(dataOff, fullPath, sink);
      } else {
        // Peek the file size for listing without decompressing.
        this._stream.Position = dataOff;
        ReadExact(sizeBuf);
        var size = BitConverter.ToUInt32(sizeBuf);
        sink.Add(new HpiEntry { Name = fullPath, DataOffset = dataOff, Size = size, IsDirectory = false });
      }
    }
  }

  private string ReadCString(uint offset) {
    this._stream.Position = offset;
    var sb = new StringBuilder();
    while (true) {
      var b = this._stream.ReadByte();
      if (b < 0)
        throw new InvalidDataException($"Unexpected EOF reading HPI string at 0x{offset:X}.");
      if (b == 0)
        return sb.ToString();
      sb.Append((char)b);
    }
  }

  private void ReadExact(Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = this._stream.Read(buffer[total..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of HPI stream.");
      total += read;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
