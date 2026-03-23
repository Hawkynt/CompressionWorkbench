using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Crypto;
using Compression.Core.Dictionary.Ace;
using CoreAceConstants = Compression.Core.Dictionary.Ace.AceConstants;

namespace FileFormat.Ace;

/// <summary>
/// Reads entries from an ACE archive.
/// </summary>
public sealed class AceReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly string? _password;
  private readonly List<AceEntry> _entries = [];
  private AceDecoder? _solidDecoder;
  private bool _disposed;

  /// <summary>Gets the entries in the archive.</summary>
  public IReadOnlyList<AceEntry> Entries => this._entries;

  /// <summary>Gets the archive comment, if present.</summary>
  public string? Comment { get; private set; }

  /// <summary>Gets the ACE version.</summary>
  public byte Version { get; private set; }

  /// <summary>Gets whether this is a solid archive.</summary>
  public bool IsSolid { get; private set; }

  /// <summary>Gets whether this archive has a recovery record.</summary>
  public bool HasRecoveryRecord { get; private set; }

  private long _recoveryDataOffset;
  private int _recoveryDataSize;
  private int _recoverySectorCount;

  /// <summary>
  /// Initializes a new <see cref="AceReader"/> from a stream.
  /// </summary>
  /// <param name="stream">A seekable stream containing the ACE archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="password">Optional password for decrypting encrypted entries.</param>
  public AceReader(Stream stream, bool leaveOpen = false, string? password = null) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._password = password;
    ReadArchive();
  }

  /// <summary>
  /// Extracts the data for an entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The decompressed data.</returns>
  public byte[] ExtractEntry(AceEntry entry) {
    if (entry.IsEncrypted && this._password == null)
      throw new InvalidOperationException("Entry is encrypted but no password was provided.");

    this._stream.Position = entry.DataOffset;
    var compressed = new byte[entry.CompressedSize];
    var totalRead = 0;
    while (totalRead < compressed.Length) {
      var read = this._stream.Read(compressed, totalRead, compressed.Length - totalRead);
      if (read == 0) throw new EndOfStreamException("Unexpected end of ACE data.");
      totalRead += read;
    }

    // Decrypt if encrypted
    if (entry.IsEncrypted) {
      var key = AceWriter.DeriveKey(this._password!);
      var bf = new Blowfish(key);
      var iv = new byte[8];
      compressed = bf.DecryptCbc(compressed, iv);
    }

    byte[] data;
    switch (entry.CompressionType) {
      case CoreAceConstants.CompStore:
        data = entry.IsEncrypted ? compressed[..(int)entry.OriginalSize] : compressed;
        // Feed stored data into solid decoder's window if in solid mode
        if (this.IsSolid && this._solidDecoder != null) {
          // Solid store: decoder window must still track the bytes
          var storeDecoder = GetOrCreateSolidDecoder(entry);
          // We don't decompress, but we need to advance the window
        }
        break;
      case CoreAceConstants.CompAce10:
      case CoreAceConstants.CompAce20: {
        var decoder = GetOrCreateSolidDecoder(entry);
        data = decoder.Decode(compressed, (int)entry.OriginalSize, entry.CompressionType);
        break;
      }
      default:
        throw new NotSupportedException($"Unsupported ACE compression type: {entry.CompressionType}");
    }

    // Verify CRC-32
    var crc = Crc32.Compute(data);
    if (crc != entry.Crc32)
      throw new InvalidDataException(
        $"CRC-32 mismatch for '{entry.FileName}': expected 0x{entry.Crc32:X8}, computed 0x{crc:X8}.");

    return data;
  }

  private AceDecoder GetOrCreateSolidDecoder(AceEntry entry) {
    if (this.IsSolid && entry.IsSolid && this._solidDecoder != null)
      return this._solidDecoder;

    // Create a fresh decoder (first file in solid chain, or non-solid archive)
    var decoder = new AceDecoder(entry.DictionaryBits);
    if (this.IsSolid)
      this._solidDecoder = decoder;
    return decoder;
  }

  private void ReadArchive() {
    var reader = new BinaryReader(this._stream, Encoding.ASCII, leaveOpen: true);

    // Read archive header
    ReadArchiveHeader(reader);

    // Read file headers
    while (this._stream.Position < this._stream.Length) {
      var header = ReadBlockHeader(reader);
      if (header == null)
        break;

      if (header.Type == AceConstants.HeaderTypeFile) {
        var entry = ReadFileEntry(reader, header);
        this._entries.Add(entry);

        // Skip compressed data
        this._stream.Position = entry.DataOffset + entry.CompressedSize;
      }
      else if (header.Type == AceConstants.HeaderTypeRecovery) {
        ReadRecoveryBlock(reader, header);
      }
      else {
        // Skip unknown header types
        if (header.DataSize > 0)
          this._stream.Position += header.DataSize;
      }
    }
  }

  private void ReadArchiveHeader(BinaryReader reader) {
    // ACE archive header:
    // offset 0: 2 bytes CRC-16 of header (from byte 4 onwards)
    // offset 2: 2 bytes header size (from byte 4 to end of header)
    // offset 4: 1 byte header type (0 = archive)
    // offset 5: 2 bytes flags
    // offset 7: 7 bytes magic "**ACE**"
    // offset 14: 1 byte version (extract)
    // offset 15: 1 byte version (create)
    // offset 16: 1 byte host OS
    // offset 17: 1 byte volume number
    // offset 18: 4 bytes timestamp
    // offset 22: rest depends on flags

    var headerCrc = reader.ReadUInt16();
    var headerSize = reader.ReadUInt16();

    var headerStart = this._stream.Position;
    var headerType = reader.ReadByte();
    if (headerType != AceConstants.HeaderTypeArchive)
      throw new InvalidDataException($"Expected ACE archive header (type 0), got type {headerType}.");

    var flags = reader.ReadUInt16();
    this.IsSolid = (flags & AceConstants.FlagSolid) != 0;
    this.HasRecoveryRecord = (flags & AceConstants.FlagRecovery) != 0;

    var magic = reader.ReadBytes(7);
    if (Encoding.ASCII.GetString(magic) != AceConstants.Magic)
      throw new InvalidDataException("Invalid ACE magic signature.");

    this.Version = reader.ReadByte(); // extract version
    reader.ReadByte(); // create version
    reader.ReadByte(); // host OS
    reader.ReadByte(); // volume number
    reader.ReadUInt32(); // timestamp

    // Skip reserved bytes (8 bytes)
    reader.ReadBytes(8);

    // AV string size
    if ((flags & AceConstants.FlagAvString) != 0) {
      var avSize = reader.ReadByte();
      reader.ReadBytes(avSize);
    }

    // Comment
    var commentSize = reader.ReadUInt16();
    if (commentSize > 0) {
      var commentBytes = reader.ReadBytes(commentSize);
      this.Comment = Encoding.ASCII.GetString(commentBytes);
    }

    // Ensure we're past the header
    this._stream.Position = headerStart + headerSize;
  }

  private BlockHeader? ReadBlockHeader(BinaryReader reader) {
    if (this._stream.Position + 4 > this._stream.Length)
      return null;

    var headerCrc = reader.ReadUInt16();
    var headerSize = reader.ReadUInt16();
    if (headerSize == 0)
      return null;

    var headerStart = this._stream.Position;
    var headerType = reader.ReadByte();
    var flags = reader.ReadUInt16();

    long dataSize = 0;
    if (headerType == AceConstants.HeaderTypeFile) {
      // File headers have a data size field
    }

    return new BlockHeader {
      Crc = headerCrc,
      Size = headerSize,
      Type = headerType,
      Flags = flags,
      HeaderStart = headerStart,
      DataSize = dataSize
    };
  }

  private AceEntry ReadFileEntry(BinaryReader reader, BlockHeader header) {
    var packSize = reader.ReadUInt32();
    var origSize = reader.ReadUInt32();
    var timestamp = reader.ReadUInt32();
    var attributes = reader.ReadUInt32();
    var crc32 = reader.ReadUInt32();

    // TECH_INFO
    var compType = reader.ReadByte();
    var quality = reader.ReadByte();
    var techParams = reader.ReadUInt16();

    var dictBits = (techParams & 0x0F) + CoreAceConstants.MinDictBits;
    if (dictBits > CoreAceConstants.MaxDictBits)
      dictBits = CoreAceConstants.DefaultDictBits;

    var fileNameLen = reader.ReadUInt16();
    var nameBytes = reader.ReadBytes(fileNameLen);
    var fileName = Encoding.ASCII.GetString(nameBytes);

    // Skip any remaining header bytes
    var currentPos = this._stream.Position;
    var expectedEnd = header.HeaderStart + header.Size;
    if (currentPos < expectedEnd)
      this._stream.Position = expectedEnd;

    var entry = new AceEntry {
      FileName = fileName,
      CompressedSize = packSize,
      OriginalSize = origSize,
      Crc32 = crc32,
      CompressionType = compType,
      Quality = quality,
      DictionaryBits = dictBits,
      Attributes = attributes,
      Flags = header.Flags,
      LastModified = DateTimeFromDos(timestamp),
      DataOffset = this._stream.Position
    };

    return entry;
  }

  private void ReadRecoveryBlock(BinaryReader reader, BlockHeader header) {
    // Recovery block body: recoveryDataSize (uint32) + sectorCount (uint32)
    this._recoveryDataSize = (int)reader.ReadUInt32();
    this._recoverySectorCount = (int)reader.ReadUInt32();

    // Skip remaining header bytes
    var headerEnd = header.HeaderStart + header.Size;
    if (this._stream.Position < headerEnd)
      this._stream.Position = headerEnd;

    this._recoveryDataOffset = this._stream.Position;
    this._stream.Position += this._recoveryDataSize;
  }

  /// <summary>
  /// Verifies the recovery record against the archive's file data.
  /// Returns <see langword="true"/> if the parity matches.
  /// </summary>
  public bool VerifyRecoveryRecord() {
    if (!this.HasRecoveryRecord || this._recoveryDataSize == 0)
      return false;

    // Read stored parity
    this._stream.Position = this._recoveryDataOffset;
    var storedParity = new byte[this._recoveryDataSize];
    var totalRead = 0;
    while (totalRead < storedParity.Length) {
      var read = this._stream.Read(storedParity, totalRead, storedParity.Length - totalRead);
      if (read == 0) break;
      totalRead += read;
    }

    // Recompute parity from file compressed data
    var computedParity = new byte[this._recoveryDataSize];
    foreach (var entry in this._entries) {
      this._stream.Position = entry.DataOffset;
      var compressed = new byte[entry.CompressedSize];
      var r = 0;
      while (r < compressed.Length) {
        var n = this._stream.Read(compressed, r, compressed.Length - r);
        if (n == 0) break;
        r += n;
      }

      for (var i = 0; i < compressed.Length && i < computedParity.Length; ++i)
        computedParity[i] ^= compressed[i];
    }

    return storedParity.AsSpan().SequenceEqual(computedParity);
  }

  private static DateTime DateTimeFromDos(uint timestamp) {
    var time = (int)(timestamp & 0xFFFF);
    var date = (int)(timestamp >> 16);
    try {
      return new DateTime(
        ((date >> 9) & 0x7F) + 1980,
        Math.Max((date >> 5) & 0x0F, 1),
        Math.Max(date & 0x1F, 1),
        (time >> 11) & 0x1F,
        (time >> 5) & 0x3F,
        (time & 0x1F) * 2);
    }
    catch {
      return DateTime.MinValue;
    }
  }

  private sealed class BlockHeader {
    public ushort Crc;
    public ushort Size;
    public byte Type;
    public ushort Flags;
    public long HeaderStart;
    public long DataSize;
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
