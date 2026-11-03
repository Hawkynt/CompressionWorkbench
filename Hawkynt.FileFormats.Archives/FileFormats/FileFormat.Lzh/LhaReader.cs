using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzh;

namespace FileFormat.Lzh;

/// <summary>
/// Reads entries from an LHA/LZH archive.
/// </summary>
public sealed class LhaReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<LhaEntry> _entries;
  private bool _disposed;

  /// <summary>Gets the entries in the archive.</summary>
  public IReadOnlyList<LhaEntry> Entries => this._entries;

  /// <summary>Initializes a new <see cref="LhaReader"/> from a stream.</summary>
  public LhaReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._entries = [];
    this.ReadEntries();
  }

  /// <summary>Extracts the data for an entry.</summary>
  public byte[] ExtractEntry(LhaEntry entry) {
    this._stream.Position = entry.DataOffset;
    var compressedData = new byte[entry.CompressedSize];
    var totalRead = 0;
    while (totalRead < compressedData.Length) {
      var read = this._stream.Read(compressedData, totalRead, compressedData.Length - totalRead);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of LHA data.");
      totalRead += read;
    }

    byte[] data;
    switch (entry.Method) {
      case LhaConstants.MethodLh0:
      case LhaConstants.MethodLz4:
        data = compressedData;
        break;
      case LhaConstants.MethodLh1:
        data = DecompressLh1(compressedData, (int)entry.OriginalSize);
        break;
      case LhaConstants.MethodLh2:
        data = Lh2Decoder.Decode(compressedData, (int)entry.OriginalSize);
        break;
      case LhaConstants.MethodLh3:
        data = Lh3Decoder.Decode(compressedData, (int)entry.OriginalSize);
        break;
      case LhaConstants.MethodLh4:
        data = DecompressLzh(compressedData, (int)entry.OriginalSize, LzhConstants.Lh4PositionBits);
        break;
      case LhaConstants.MethodLh5:
        data = DecompressLzh(compressedData, (int)entry.OriginalSize, LzhConstants.Lh5PositionBits);
        break;
      case LhaConstants.MethodLh6:
        data = DecompressLzh(compressedData, (int)entry.OriginalSize, LzhConstants.Lh6PositionBits);
        break;
      case LhaConstants.MethodLh7:
        data = DecompressLzh(compressedData, (int)entry.OriginalSize, LzhConstants.Lh7PositionBits);
        break;
      case LhaConstants.MethodLhx:
        data = DecompressLzh(compressedData, (int)entry.OriginalSize, 20);
        break;
      case LhaConstants.MethodLk7: {
        using var ms = new MemoryStream(compressedData);
        data = new LzhDecoder(ms, LzhConstants.Lh7PositionBits, lhark: true).Decode((int)entry.OriginalSize);
        break;
      }
      case LhaConstants.MethodLzs:
        data = LzsDecoder.Decode(compressedData, (int)entry.OriginalSize);
        break;
      case LhaConstants.MethodLz5:
        data = Lz5Decoder.Decode(compressedData, (int)entry.OriginalSize);
        break;
      case LhaConstants.MethodPm0:
        data = compressedData;
        break;
      case LhaConstants.MethodPm1:
        data = Pm1Decoder.Decode(compressedData, (int)entry.OriginalSize);
        break;
      case LhaConstants.MethodPm2:
        data = Pm2Decoder.Decode(compressedData, (int)entry.OriginalSize);
        break;
      default:
        throw new NotSupportedException($"Unsupported LHA method: {entry.Method}");
    }

    var crc = Crc16.Compute(data);
    if (crc != entry.Crc16)
      throw new InvalidDataException($"CRC-16 mismatch for '{entry.FileName}': expected 0x{entry.Crc16:X4}, computed 0x{crc:X4}.");

    return data;
  }

  private static byte[] DecompressLzh(byte[] compressedData, int originalSize, int positionBits) {
    using var ms = new MemoryStream(compressedData);
    var decoder = new LzhDecoder(ms, positionBits);
    return decoder.Decode(originalSize);
  }

  private static byte[] DecompressLh1(byte[] compressedData, int originalSize) {
    using var ms = new MemoryStream(compressedData);
    var decoder = new Lh1Decoder(ms);
    return decoder.Decode(originalSize);
  }

  /// <summary>Gets the offset of the first member header: 0 for a plain archive, past the stub of a self-extractor.</summary>
  public long ArchiveOffset { get; private set; }

  /// <summary>Largest self-extractor stub searched (lhasa's limit; DECLHA's are the biggest known).</summary>
  private const int MaxSfxStubLength = 256 * 1024;

  /// <summary>
  /// Finds the first member header the way lhasa does: a method id ("-lh?-", "-lz4/5/s-", "-pm?-"
  /// but not PMarc's "-pms-") two bytes in. DECLHA and Amiga LhASFX stubs carry a decoy header of
  /// their own, so after their banner one match is skipped. A candidate must also parse as a
  /// header with a valid checksum.
  /// </summary>
  public static long FindArchiveOffset(Stream stream) {
    var length = (int)Math.Min(stream.Length, MaxSfxStubLength + 32);
    var buffer = new byte[length];
    stream.Position = 0;
    var read = stream.ReadAtLeast(buffer, length, throwOnEndOfStream: false);
    var span = buffer.AsSpan(0, read);
    // A plain archive starts with its first header; its damage must surface, not be skipped over.
    if (span.Length < 13 || LooksLikeHeader(span) || span[0] == 0)
      return 0;
    var skip = 0;
    for (var i = 0; i + 12 < span.Length && i <= MaxSfxStubLength; ++i) {
      if (span[i..].StartsWith("LHA-SFX"u8) || span[i..].StartsWith("LhASFX V1.2,"u8))
        skip = 1;
      if (!LooksLikeHeader(span[i..]))
        continue;
      if (skip > 0) {
        --skip;
        continue;
      }
      if (ParsesAt(stream, i))
        return i;
    }
    return 0;
  }

  private static bool LooksLikeHeader(ReadOnlySpan<byte> b) {
    if (b[2] != '-' || b[6] != '-')
      return false;
    if (b[3] == 'l' && b[4] == 'h')
      return true;
    if (b[3] == 'l' && b[4] == 'z' && b[5] is (byte)'4' or (byte)'5' or (byte)'s')
      return true;
    return b[3] == 'p' && b[4] == 'm' && b[5] != 's';
  }

  private static bool ParsesAt(Stream stream, long offset) {
    try {
      stream.Position = offset;
      return LhaHeaderParser.Read(stream) != null;
    } catch (InvalidDataException) {
      return false;
    }
  }

  private void ReadEntries() {
    this.ArchiveOffset = FindArchiveOffset(this._stream);
    this._stream.Position = this.ArchiveOffset;
    while (this._stream.Position < this._stream.Length) {
      var header = LhaHeaderParser.Read(this._stream);
      if (header == null)
        break;

      if (header.Method != LhaConstants.MethodLhd)
        this._entries.Add(new LhaEntry {
          FileName = header.Path,
          Method = header.Method,
          CompressedSize = header.CompressedSize,
          OriginalSize = header.OriginalSize,
          LastModified = header.LastModified,
          Crc16 = header.Crc16,
          HeaderLevel = header.Level,
          OsId = header.OsId,
          DataOffset = header.DataOffset,
        });

      if (header.End > this._stream.Length)
        break;  // a truncated last member is listed; extracting it fails
      this._stream.Position = header.End;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
