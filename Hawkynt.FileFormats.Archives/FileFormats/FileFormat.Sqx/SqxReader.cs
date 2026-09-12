using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Crypto;
using Compression.Core.Dictionary.Sqx;

namespace FileFormat.Sqx;

/// <summary>
/// Reads entries from an SQX archive (supports V11 and V20 formats).
/// </summary>
public sealed class SqxReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly string? _password;
  private readonly List<SqxEntry> _entries = [];
  private bool _disposed;
  private bool _isSolid;
  private int _recoveryPercent;
  private long _recoveryDataOffset;
  private int _recoveryFileBlocks;
  private long _recoveryDataSize;
  private uint _recoveryRdCrc;
  private uint _recoveryFdCrc;

  /// <summary>Gets the entries in the archive.</summary>
  public IReadOnlyList<SqxEntry> Entries => this._entries;

  /// <summary>Gets whether this is a solid archive.</summary>
  public bool IsSolid => this._isSolid;

  /// <summary>Gets whether the archive has a recovery record.</summary>
  public bool HasRecoveryRecord => this._recoveryPercent > 0;

  /// <summary>
  /// Initializes a new <see cref="SqxReader"/> from a stream.
  /// </summary>
  public SqxReader(Stream stream, bool leaveOpen = false, string? password = null) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._password = password;
    ReadArchive();
  }

  /// <summary>
  /// Extracts the data for an entry.
  /// </summary>
  public byte[] ExtractEntry(SqxEntry entry) {
    if (entry.IsEncrypted && this._password == null)
      throw new InvalidOperationException("Entry is encrypted but no password was provided.");

    this._stream.Position = entry.DataOffset;
    var compressed = new byte[entry.CompressedSize];
    ReadFully(this._stream, compressed);

    if (entry.IsEncrypted) {
      var key = SqxWriter.DeriveKey(this._password!);
      var iv = new byte[16];
      compressed = AesCryptor.DecryptCbcNoPaddingAny(compressed, key, iv);
    }

    return DecompressEntry(entry, compressed);
  }

  /// <summary>
  /// Extracts all entries in order. Required for solid archives.
  /// </summary>
  public byte[][] ExtractAll() {
    var results = new byte[this._entries.Count][];

    SqxDecoder? solidDecoder = null;
    if (this._isSolid) {
      var dictSize = this._entries.Count > 0 ? this._entries[0].DictionarySize : SqxConstants.GetDictSize(0);
      solidDecoder = new SqxDecoder(dictSize);
    }

    for (var i = 0; i < this._entries.Count; ++i) {
      var entry = this._entries[i];

      this._stream.Position = entry.DataOffset;
      var compressed = new byte[entry.CompressedSize];
      ReadFully(this._stream, compressed);

      if (entry.IsEncrypted) {
        var key = SqxWriter.DeriveKey(this._password!);
        var iv = new byte[16];
        compressed = AesCryptor.DecryptCbcNoPaddingAny(compressed, key, iv);
      }

      byte[] data;
      if (solidDecoder != null && entry.Method is >= SqxConstants.MethodNormal and <= SqxConstants.MethodExBest) {
        // Solid: don't reset decoder between files
        if (!entry.IsSolid)
          solidDecoder.Reset();
        data = solidDecoder.Decode(compressed, (int)entry.OriginalSize);
      }
      else {
        data = DecompressEntry(entry, compressed);
      }

      var crc = Crc32.Compute(data);
      if (crc != entry.Crc32)
        throw new InvalidDataException(
          $"CRC-32 mismatch for '{entry.FileName}': expected 0x{entry.Crc32:X8}, computed 0x{crc:X8}.");

      results[i] = data;
    }

    return results;
  }

  /// <summary>
  /// Verifies the recovery record against the archive data.
  /// </summary>
  /// <returns>True if recovery record is valid or absent.</returns>
  public bool VerifyRecoveryRecord() {
    if (!this.HasRecoveryRecord) return true;

    // Read all archive data up to recovery block
    this._stream.Position = 0;
    var archiveData = new byte[this._recoveryDataSize];
    ReadFully(this._stream, archiveData);

    var computedFdCrc = Crc32.Compute(archiveData);
    if (computedFdCrc != this._recoveryFdCrc) return false;

    // Also verify per-block parity
    var sectorSize = SqxConstants.RecoverySectorSize;
    this._stream.Position = this._recoveryDataOffset;
    var reader = new BinaryReader(this._stream, Encoding.ASCII, leaveOpen: true);

    // Skip block CRCs
    for (var i = 0; i < this._recoveryFileBlocks; ++i)
      reader.ReadUInt16();

    // Read parity
    var parity = reader.ReadBytes(sectorSize);
    var rdCrc = Crc32.Compute(parity);
    return rdCrc == this._recoveryRdCrc;
  }

  private byte[] DecompressEntry(SqxEntry entry, byte[] compressed) {
    byte[] data;
    var dictSize = entry.DictionarySize;

    switch (entry.Method) {
      case SqxConstants.MethodStore:
        data = entry.IsEncrypted ? compressed[..(int)entry.OriginalSize] : compressed;
        break;
      case SqxConstants.MethodNormal:
      case SqxConstants.MethodGood:
      case SqxConstants.MethodHigh:
      case SqxConstants.MethodBest:
      case SqxConstants.MethodExNormal:
      case SqxConstants.MethodExGood:
      case SqxConstants.MethodExHigh:
      case SqxConstants.MethodExBest:
        data = SqxDecoder.Decode(compressed, (int)entry.OriginalSize, dictSize);
        // Apply post-decompression transforms
        if ((entry.ExtraCompFlags & SqxConstants.ExtraFlagBcj) != 0)
          data = Compression.Core.Transforms.BcjFilter.DecodeX86(data);
        if ((entry.ExtraCompFlags & SqxConstants.ExtraFlagDelta) != 0)
          data = Compression.Core.Transforms.DeltaFilter.Decode(data);
        break;
      case SqxConstants.MethodAudio:
      case SqxConstants.MethodExAudio:
        data = SqxAudioCodec.Decode(compressed, (int)entry.OriginalSize);
        break;
      case SqxConstants.MethodMultimedia:
        data = SqxMultimediaCodec.Decode(compressed, (int)entry.OriginalSize);
        break;
      case SqxConstants.MethodLzhBcj: {
        var lzhDecoded = SqxDecoder.Decode(compressed, (int)entry.OriginalSize, dictSize);
        data = Compression.Core.Transforms.BcjFilter.DecodeX86(lzhDecoded);
        break;
      }
      case SqxConstants.MethodLzhDelta: {
        var lzhDecoded = SqxDecoder.Decode(compressed, (int)entry.OriginalSize, dictSize);
        data = Compression.Core.Transforms.DeltaFilter.Decode(lzhDecoded);
        break;
      }
      default:
        throw new NotSupportedException($"Unsupported SQX method: {entry.Method}");
    }

    var crc = Crc32.Compute(data);
    if (crc != entry.Crc32)
      throw new InvalidDataException(
        $"CRC-32 mismatch for '{entry.FileName}': expected 0x{entry.Crc32:X8}, computed 0x{crc:X8}.");

    return data;
  }

  private void ReadArchive() {
    var reader = new BinaryReader(this._stream, Encoding.ASCII, leaveOpen: true);

    while (this._stream.Position + 7 <= this._stream.Length) {
      var blockCrc = reader.ReadUInt16();
      var blockType = reader.ReadByte();
      var blockFlags = reader.ReadUInt16();
      var blockSize = reader.ReadUInt16();

      // blockSize includes crc(2) + type(1) + flags(2) + size(2) + data = 7 + data
      var dataLen = blockSize - 7;
      if (dataLen < 0) dataLen = 0;

      var blockDataStart = this._stream.Position;

      switch (blockType) {
        case SqxConstants.BlockEnd:
        case SqxConstants.BlockRecovery:
        case SqxConstants.BlockAV:
          if (blockType == SqxConstants.BlockRecovery)
            ReadRecoveryBlock(reader, dataLen);
          return; // stop reading entries

        case SqxConstants.BlockMain:
          ReadMainBlock(reader, blockFlags, dataLen);
          break;

        case SqxConstants.BlockFile:
          ReadFileEntry(reader, blockDataStart, dataLen, blockFlags);
          break;

        default:
          // Skip unknown blocks
          this._stream.Position = blockDataStart + dataLen;
          break;
      }
    }
  }

  private void ReadMainBlock(BinaryReader reader, ushort flags, int dataLen) {
    var magicBytes = reader.ReadBytes(5);
    var magic = Encoding.ASCII.GetString(magicBytes);
    if (magic != SqxConstants.Magic)
      throw new InvalidDataException("Invalid SQX magic signature.");

    this._isSolid = (flags & SqxConstants.MainFlagSolid) != 0;

    // Skip remaining header fields (ARC_VER, reserved)
    var remaining = dataLen - 5;
    if (remaining > 0)
      this._stream.Position += remaining;
  }

  private void ReadFileEntry(BinaryReader reader, long blockDataStart, int dataLen, ushort flags) {
    var compFlags = reader.ReadByte();       // COMP_FLAGS
    reader.ReadUInt16();                       // EXTRA_FLAGS (reserved)
    reader.ReadByte();                         // OS_FILE_SYS
    var arcVer = reader.ReadByte();          // ARC_VER
    var method = reader.ReadByte();          // ARC_METHOD
    var crc32 = reader.ReadUInt32();          // FILE_CRC32
    var attributes = reader.ReadUInt32();     // FILE_ATTR
    var timestamp = reader.ReadUInt32();      // FILE_TIME
    var compressedSize = reader.ReadUInt32(); // COMP_SIZE
    var originalSize = reader.ReadUInt32();   // ORIG_SIZE

    // 64-bit sizes
    long comp64 = compressedSize;
    long orig64 = originalSize;
    if ((flags & SqxConstants.FileFlagFile64) != 0) {
      comp64 |= (long)reader.ReadUInt32() << 32;
      orig64 |= (long)reader.ReadUInt32() << 32;
    }

    var nameLen = reader.ReadUInt16();       // NAME_LEN
    var nameBytes = reader.ReadBytes(nameLen); // FILE_NAME

    // Position to end of block header
    this._stream.Position = blockDataStart + dataLen;

    // Read EXTRA_COMPRESSOR if indicated
    ushort extraCompFlags = 0;
    if ((compFlags & SqxConstants.CompFlagExtra) != 0 && (flags & SqxConstants.FileFlagNextBlock) != 0) {
      extraCompFlags = reader.ReadUInt16();
    }

    var entry = new SqxEntry {
      FileName = Encoding.ASCII.GetString(nameBytes),
      CompressedSize = comp64,
      OriginalSize = orig64,
      Crc32 = crc32,
      Method = method,
      Attributes = attributes,
      Flags = flags,
      ArchiveVersion = arcVer,
      CompFlags = compFlags,
      ExtraCompFlags = extraCompFlags,
      LastModified = DateTimeFromDos(timestamp),
      DataOffset = this._stream.Position
    };

    this._entries.Add(entry);

    // Skip compressed data
    this._stream.Position += comp64;
  }

  private void ReadRecoveryBlock(BinaryReader reader, int dataLen) {
    if (dataLen < 42) return;

    var sig = reader.ReadBytes(5);
    if (Encoding.ASCII.GetString(sig) != SqxConstants.RecoverySignature)
      return;

    this._recoveryPercent = reader.ReadInt32();  // R_LEVEL
    this._recoveryFileBlocks = (int)reader.ReadInt64(); // FILE_BLOCKS
    var rdBlocks = reader.ReadInt64();            // RD_BLOCKS
    reader.ReadByte();                             // ARC_VER
    this._recoveryDataSize = reader.ReadInt64();   // DATA_SIZE
    this._recoveryRdCrc = reader.ReadUInt32();     // RD_CRC
    this._recoveryFdCrc = reader.ReadUInt32();     // FD_CRC

    this._recoveryDataOffset = this._stream.Position;
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

  private static void ReadFully(Stream stream, byte[] buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
      if (read == 0) throw new EndOfStreamException("Unexpected end of SQX data.");
      totalRead += read;
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
