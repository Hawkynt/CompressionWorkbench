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

  private void ReadEntries() {
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
