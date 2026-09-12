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
      case LhaConstants.MethodLh3:
        data = DecompressLzh(compressedData, (int)entry.OriginalSize, LzhConstants.Lh5PositionBits);
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
        data = PmaDecoder.Decode(compressedData, (int)entry.OriginalSize, 2);
        break;
      case LhaConstants.MethodPm2:
        data = PmaDecoder.Decode(compressedData, (int)entry.OriginalSize, 3);
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
      var entry = this.ReadHeader();
      if (entry == null)
        break;

      if (entry.Method != LhaConstants.MethodLhd)
        this._entries.Add(entry);

      this._stream.Position = entry.DataOffset + entry.CompressedSize;
    }
  }

  private LhaEntry? ReadHeader() {
    var firstByte = this._stream.ReadByte();
    if (firstByte <= 0)
      return null;

    var secondByte = this._stream.ReadByte();
    if (secondByte < 0)
      return null;

    var methodBytes = new byte[5];
    if (this._stream.Read(methodBytes, 0, 5) < 5)
      return null;
    var method = Encoding.ASCII.GetString(methodBytes);
    if (!method.StartsWith('-') || !method.EndsWith('-'))
      return null;

    var reader = new BinaryReader(this._stream, Encoding.ASCII, leaveOpen: true);
    var compressedSize = reader.ReadUInt32();
    var originalSize = reader.ReadUInt32();
    var timestamp = reader.ReadUInt32();
    var reserved = reader.ReadByte();
    var level = reader.ReadByte();

    var entry = new LhaEntry {
      Method = method,
      CompressedSize = compressedSize,
      OriginalSize = originalSize,
      HeaderLevel = level,
      LastModified = level == LhaConstants.HeaderLevel2
        ? DateTimeFromUnix(timestamp)
        : DateTimeFromMsdos(timestamp),
    };

    switch (level) {
      case LhaConstants.HeaderLevel0:
        this.ReadLevel0Header(reader, entry, firstByte);
        break;
      case LhaConstants.HeaderLevel1:
        ReadLevel1Header(reader, entry);
        break;
      case LhaConstants.HeaderLevel2:
        ReadLevel2Header(reader, entry, firstByte | (secondByte << 8));
        break;
      default:
        throw new InvalidDataException($"Unsupported LHA header level: {level}");
    }

    return entry;
  }

  private void ReadLevel0Header(BinaryReader reader, LhaEntry entry, int headerSize) {
    var nameLength = reader.ReadByte();
    var nameBytes = reader.ReadBytes(nameLength);
    entry.FileName = Encoding.ASCII.GetString(nameBytes);
    entry.Crc16 = reader.ReadUInt16();

    var headerStart = this._stream.Position - 2 - 5 - 4 - 4 - 4 - 1 - 1 - 1 - nameLength - 2;
    entry.DataOffset = headerStart + 2 + headerSize;
    this._stream.Position = entry.DataOffset;
  }

  private static void ReadLevel1Header(BinaryReader reader, LhaEntry entry) {
    var nameLength = reader.ReadByte();
    var nameBytes = reader.ReadBytes(nameLength);
    entry.FileName = Encoding.ASCII.GetString(nameBytes);
    entry.Crc16 = reader.ReadUInt16();
    entry.OsId = reader.ReadByte();

    while (true) {
      var extSize = reader.ReadUInt16();
      if (extSize == 0)
        break;
      if (extSize < 3)
        throw new InvalidDataException("Invalid LHA level-1 extended-header size.");

      var extType = reader.ReadByte();
      var extData = reader.ReadBytes(extSize - 3);
      if (extData.Length != extSize - 3)
        throw new EndOfStreamException("Truncated LHA level-1 extended header.");

      if (extType == 0x01 && extData.Length > 0)
        entry.FileName = Encoding.ASCII.GetString(extData);
    }

    entry.DataOffset = reader.BaseStream.Position;
  }

  private static void ReadLevel2Header(BinaryReader reader, LhaEntry entry, int totalHeaderSize) {
    entry.Crc16 = reader.ReadUInt16();
    entry.OsId = reader.ReadByte();

    var headerStart = reader.BaseStream.Position - 2 - 5 - 4 - 4 - 4 - 1 - 1 - 2 - 1;
    var headerEnd = headerStart + totalHeaderSize;
    if (headerEnd > reader.BaseStream.Length)
      throw new EndOfStreamException("Truncated LHA level-2 header.");

    while (reader.BaseStream.Position < headerEnd) {
      var extSize = reader.ReadUInt16();
      if (extSize == 0)
        break;
      if (extSize < 3 || reader.BaseStream.Position + extSize - 2 > headerEnd)
        throw new InvalidDataException("Invalid LHA level-2 extended-header size.");

      var extType = reader.ReadByte();
      var extData = reader.ReadBytes(extSize - 3);
      if (extData.Length != extSize - 3)
        throw new EndOfStreamException("Truncated LHA level-2 extended header.");

      if (extType == 0x01 && extData.Length > 0)
        entry.FileName = Encoding.ASCII.GetString(extData);
    }

    reader.BaseStream.Position = headerEnd;
    entry.DataOffset = headerEnd;
  }

  private static DateTime DateTimeFromUnix(uint timestamp)
    => DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;

  private static DateTime DateTimeFromMsdos(uint timestamp) {
    var time = (int)(timestamp & 0xFFFF);
    var date = (int)(timestamp >> 16);
    try {
      return new DateTime(
        ((date >> 9) & 0x7F) + 1980,
        (date >> 5) & 0x0F,
        date & 0x1F,
        (time >> 11) & 0x1F,
        (time >> 5) & 0x3F,
        (time & 0x1F) * 2);
    } catch {
      return DateTime.MinValue;
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
