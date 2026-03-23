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

  /// <summary>
  /// Gets the entries in the archive.
  /// </summary>
  public IReadOnlyList<LhaEntry> Entries => this._entries;

  /// <summary>
  /// Initializes a new <see cref="LhaReader"/> from a stream.
  /// </summary>
  /// <param name="stream">A seekable stream containing the LHA archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public LhaReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._entries = [];

    this.ReadEntries();
  }

  /// <summary>
  /// Extracts the data for an entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The decompressed data.</returns>
  public byte[] ExtractEntry(LhaEntry entry) {
    this._stream.Position = entry.DataOffset;
    var compressedData = new byte[entry.CompressedSize];
    var totalRead = 0;
    while (totalRead < compressedData.Length) {
      var read = this._stream.Read(compressedData, totalRead, compressedData.Length - totalRead);
      if (read == 0) throw new EndOfStreamException("Unexpected end of LHA data.");
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
        data = DecompressLzh(compressedData, (int)entry.OriginalSize, Compression.Core.Dictionary.Lzh.LzhConstants.Lh5PositionBits);
        break;
      case LhaConstants.MethodLh4:
        data = DecompressLzh(compressedData, (int)entry.OriginalSize, Compression.Core.Dictionary.Lzh.LzhConstants.Lh4PositionBits);
        break;
      case LhaConstants.MethodLh5:
        data = DecompressLzh(compressedData, (int)entry.OriginalSize, Compression.Core.Dictionary.Lzh.LzhConstants.Lh5PositionBits);
        break;
      case LhaConstants.MethodLh6:
        data = DecompressLzh(compressedData, (int)entry.OriginalSize, Compression.Core.Dictionary.Lzh.LzhConstants.Lh6PositionBits);
        break;
      case LhaConstants.MethodLh7:
        data = DecompressLzh(compressedData, (int)entry.OriginalSize, Compression.Core.Dictionary.Lzh.LzhConstants.Lh7PositionBits);
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

    // Verify CRC-16
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

      // Skip compressed data
      this._stream.Position = entry.DataOffset + entry.CompressedSize;
    }
  }

  private LhaEntry? ReadHeader() {
    // Peek at first byte to determine if we have a valid header
    var firstByte = this._stream.ReadByte();
    if (firstByte <= 0)
      return null;

    // Read second byte to check method string position
    var secondByte = this._stream.ReadByte();
    if (secondByte < 0)
      return null;

    // Read method string (5 bytes starting at offset 2)
    var methodBytes = new byte[5];
    if (this._stream.Read(methodBytes, 0, 5) < 5)
      return null;
    var method = Encoding.ASCII.GetString(methodBytes);

    // Validate method
    if (!method.StartsWith('-') || !method.EndsWith('-'))
      return null;

    // Determine header level from later in the header
    // For level 0/1: firstByte = header size, secondByte = checksum
    // For level 2: firstByte + secondByte << 8 = total header size

    // Read common fields after method
    var reader = new BinaryReader(this._stream, Encoding.ASCII, leaveOpen: true);
    var compressedSize = reader.ReadUInt32();
    var originalSize = reader.ReadUInt32();
    var timestamp = reader.ReadUInt32();
    var reserved = reader.ReadByte(); // attribute (level 0) or reserved (level 1/2)
    var level = reader.ReadByte();

    var entry = new LhaEntry {
      Method = method,
      CompressedSize = compressedSize,
      OriginalSize = originalSize,
      HeaderLevel = level,
      LastModified = DateTimeFromMsdos(timestamp)
    };

    switch (level) {
      case 0:
        ReadLevel0Header(reader, entry, firstByte);
        break;
      case 1:
        ReadLevel1Header(reader, entry);
        break;
      case 2:
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

    // Skip any remaining header bytes
    var expectedDataStart = this._stream.Position;
    // Level 0: headerSize includes everything from offset 2 to end of header
    // Total header = 2 + headerSize bytes, data follows
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

    // Read extended headers
    while (true) {
      var extSize = reader.ReadUInt16();
      if (extSize == 0)
        break;

      var extType = reader.ReadByte();
      var extData = reader.ReadBytes(extSize - 3);

      if (extType == 0x01 && extData.Length > 0)
        entry.FileName = Encoding.ASCII.GetString(extData);
    }

    entry.DataOffset = reader.BaseStream.Position;
  }

  private static void ReadLevel2Header(BinaryReader reader, LhaEntry entry, int totalHeaderSize) {
    entry.Crc16 = reader.ReadUInt16();
    entry.OsId = reader.ReadByte();

    // Read extended headers
    var headerStart = reader.BaseStream.Position - 2 - 5 - 4 - 4 - 4 - 1 - 1 - 2 - 1;
    var headerEnd = headerStart + totalHeaderSize;

    while (reader.BaseStream.Position < headerEnd) {
      var extSize = reader.ReadUInt16();
      if (extSize == 0)
        break;

      var extType = reader.ReadByte();
      var extData = reader.ReadBytes(extSize - 3);

      if (extType == 0x01 && extData.Length > 0)
        entry.FileName = Encoding.ASCII.GetString(extData);
    }

    reader.BaseStream.Position = headerEnd;
    entry.DataOffset = headerEnd;
  }

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
