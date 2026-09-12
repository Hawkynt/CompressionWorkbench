using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Crypto;
using Compression.Core.Dictionary.Sqx;

namespace FileFormat.Sqx;

/// <summary>
/// Creates SQX archives matching the real SQX format specification.
/// </summary>
public sealed class SqxWriter {
  private readonly List<(string name, byte[] data)> _files = [];
  private readonly string? _password;
  private readonly byte _method;
  private readonly bool _solid;
  private readonly int _recoveryPercent;
  private readonly int _dictSize;

  /// <summary>
  /// Initializes a new <see cref="SqxWriter"/>.
  /// </summary>
  /// <param name="password">Optional password for AES-128 encryption.</param>
  /// <param name="method">Compression method (0=store, 1=normal LZH, 2=multimedia, 3=audio, 4=LZH+BCJ, 5=LZH+delta).</param>
  /// <param name="solid">Whether to create a solid archive.</param>
  /// <param name="recoveryPercent">Recovery record percentage (0=none).</param>
  /// <param name="dictSize">Dictionary size in bytes (must be power of 2, 32KB-4MB).</param>
  public SqxWriter(string? password = null, byte method = SqxConstants.MethodLzh,
      bool solid = false, int recoveryPercent = 0,
      int dictSize = Compression.Core.Dictionary.Sqx.SqxConstants.DefaultDictSize) {
    this._password = password;
    this._method = method;
    this._solid = solid;
    this._recoveryPercent = recoveryPercent;
    this._dictSize = dictSize;
  }

  /// <summary>
  /// Adds a file to the archive.
  /// </summary>
  public void AddFile(string name, byte[] data) {
    this._files.Add((name, data));
  }

  /// <summary>
  /// Writes the archive to a stream.
  /// </summary>
  public void WriteTo(Stream output) {
    var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
    var dataStartPos = output.Position;

    // Write main header
    WriteMainBlock(writer);

    // Shared encoder for solid mode
    var solidEncoder = this._solid ? new SqxEncoder(this._dictSize) : null;

    // Write file entries
    var isFirst = true;
    foreach (var (name, data) in this._files) {
      WriteFileEntry(writer, name, data, solidEncoder, isFirst);
      isFirst = false;
    }

    // Write recovery record if requested
    if (this._recoveryPercent > 0) {
      var dataEndPos = output.Position;
      WriteRecoveryBlock(writer, output, dataStartPos, dataEndPos);
    }

    // Write end block
    WriteEndBlock(writer);
  }

  /// <summary>
  /// Creates an SQX archive as a byte array.
  /// </summary>
  public byte[] ToArray() {
    using var ms = new MemoryStream();
    WriteTo(ms);
    return ms.ToArray();
  }

  private void WriteMainBlock(BinaryWriter writer) {
    using var blockMs = new MemoryStream();
    using var bw = new BinaryWriter(blockMs, Encoding.ASCII, leaveOpen: true);

    // SQX_ID (5 bytes)
    bw.Write(Encoding.ASCII.GetBytes(SqxConstants.Magic));
    // ARC_VER (1 byte)
    bw.Write(SqxConstants.ArcVersion11);
    // EXTRA_1 (2 bytes reserved)
    bw.Write((ushort)0);
    // EXTRA_2 (4 bytes reserved)
    bw.Write(0);
    // RESERVED1-3 (6 bytes reserved)
    bw.Write((ushort)0);
    bw.Write((ushort)0);
    bw.Write((ushort)0);
    bw.Flush();

    ushort mainFlags = 0;
    if (this._solid)
      mainFlags |= SqxConstants.MainFlagSolid;

    var blockData = blockMs.ToArray();
    var blockSize = (ushort)(7 + blockData.Length); // crc(2) + type(1) + flags(2) + size(2) + data
    var blockCrc = ComputeBlockCrc(SqxConstants.BlockMain, mainFlags, blockSize, blockData);

    writer.Write(blockCrc);
    writer.Write(SqxConstants.BlockMain);
    writer.Write(mainFlags);
    writer.Write(blockSize);
    writer.Write(blockData);
  }

  private void WriteFileEntry(BinaryWriter writer, string name, byte[] data,
      SqxEncoder? solidEncoder, bool isFirst) {
    var dataCrc = Crc32.Compute(data);

    byte[] compressed;
    byte method;
    byte compFlags = 0;
    ushort extraComp = 0;

    if (data.Length == 0) {
      compressed = data;
      method = SqxConstants.MethodStore;
    }
    else {
      var internalMethod = this._method;
      ReadOnlySpan<byte> toCompress = data;
      byte[] preprocessed;

      // Translate internal method codes to real on-disk codes + transform flags
      if (internalMethod == SqxConstants.MethodLzhBcj) {
        preprocessed = Compression.Core.Transforms.BcjFilter.EncodeX86(data);
        toCompress = preprocessed;
        compFlags |= SqxConstants.CompFlagExtra;
        extraComp |= SqxConstants.ExtraFlagBcj;
        method = SqxConstants.MethodNormal;
      }
      else if (internalMethod == SqxConstants.MethodLzhDelta) {
        preprocessed = Compression.Core.Transforms.DeltaFilter.Encode(data);
        toCompress = preprocessed;
        compFlags |= SqxConstants.CompFlagExtra;
        extraComp |= SqxConstants.ExtraFlagDelta;
        method = SqxConstants.MethodNormal;
      }
      else {
        method = internalMethod;
      }

      switch (internalMethod) {
        case SqxConstants.MethodMultimedia:
          compressed = SqxMultimediaCodec.Encode(data);
          break;
        case SqxConstants.MethodAudio:
          compressed = SqxAudioCodec.Encode(data);
          break;
        default:
          if (solidEncoder != null) {
            compressed = solidEncoder.Encode(toCompress);
          }
          else {
            compressed = SqxEncoder.Encode(toCompress, this._dictSize);
          }
          break;
      }

      // Fall back to store if LZH-based compression doesn't help
      if (internalMethod != SqxConstants.MethodMultimedia && internalMethod != SqxConstants.MethodAudio) {
        if (compressed.Length >= data.Length) {
          compressed = data;
          method = SqxConstants.MethodStore;
          compFlags = 0;
          extraComp = 0;
        }
      }
    }

    // Encrypt if password is set
    var fileFlags = SqxConstants.GetDictFlag(this._dictSize);
    if (this._password != null && compressed.Length > 0) {
      var key = DeriveKey(this._password);
      var iv = new byte[16];
      var paddedLen = (compressed.Length + 15) & ~15;
      if (paddedLen != compressed.Length) {
        var padded = new byte[paddedLen];
        compressed.AsSpan().CopyTo(padded);
        compressed = padded;
      }
      compressed = AesCryptor.EncryptCbcNoPaddingAny(compressed, key, iv);
      fileFlags |= SqxConstants.FileFlagEncrypted;
    }

    // Solid flag
    if (this._solid && !isFirst && method != SqxConstants.MethodStore)
      fileFlags |= SqxConstants.FileFlagSolid;

    // EXTRA_COMPRESSOR subblock follows
    if (compFlags != 0)
      fileFlags |= SqxConstants.FileFlagNextBlock;

    var nameBytes = Encoding.ASCII.GetBytes(name);

    // Build file header block (spec field order)
    using var blockMs = new MemoryStream();
    using var bw = new BinaryWriter(blockMs, Encoding.ASCII, leaveOpen: true);
    bw.Write(compFlags);                          // COMP_FLAGS (1 byte)
    bw.Write((ushort)0);                          // EXTRA_FLAGS (2 bytes reserved)
    bw.Write((byte)0);                            // OS_FILE_SYS (1 byte)
    bw.Write(SqxConstants.ArcVersion11);           // ARC_VER (1 byte)
    bw.Write(method);                             // ARC_METHOD (1 byte)
    bw.Write(dataCrc);                            // FILE_CRC32 (4 bytes)
    bw.Write((uint)0);                            // FILE_ATTR (4 bytes)
    bw.Write(DosTimestamp(DateTime.Now));          // FILE_TIME (4 bytes)
    bw.Write((uint)compressed.Length);             // COMP_SIZE (4 bytes)
    bw.Write((uint)data.Length);                   // ORIG_SIZE (4 bytes)
    bw.Write((ushort)nameBytes.Length);             // NAME_LEN (2 bytes)
    bw.Write(nameBytes);                          // FILE_NAME
    bw.Flush();

    var blockData = blockMs.ToArray();
    var blockSize = (ushort)(7 + blockData.Length); // crc(2) + type(1) + flags(2) + size(2) + data
    var blockCrc = ComputeBlockCrc(SqxConstants.BlockFile, fileFlags, blockSize, blockData);

    writer.Write(blockCrc);
    writer.Write(SqxConstants.BlockFile);
    writer.Write(fileFlags);
    writer.Write(blockSize);
    writer.Write(blockData);

    // Write EXTRA_COMPRESSOR if needed (after header, before data)
    if (compFlags != 0) {
      writer.Write(extraComp);
    }

    // Write compressed data
    writer.Write(compressed);
  }

  private static void WriteRecoveryBlock(BinaryWriter writer, Stream output,
      long dataStartPos, long dataEndPos) {
    var dataSize = dataEndPos - dataStartPos;
    if (dataSize <= 0) return;

    // Read all archive data for parity computation
    var savedPos = output.Position;
    output.Position = dataStartPos;
    var archiveData = new byte[dataSize];
    var totalRead = 0;
    while (totalRead < archiveData.Length) {
      var read = output.Read(archiveData, totalRead, archiveData.Length - totalRead);
      if (read == 0) break;
      totalRead += read;
    }
    output.Position = savedPos;

    var sectorSize = SqxConstants.RecoverySectorSize;
    var fileBlocks = (archiveData.Length + sectorSize - 1) / sectorSize;

    // Compute per-block CRC16
    var blockCrcs = new ushort[fileBlocks];
    for (var i = 0; i < fileBlocks; ++i) {
      var offset = i * sectorSize;
      var len = Math.Min(sectorSize, archiveData.Length - offset);
      blockCrcs[i] = Crc16.Compute(archiveData.AsSpan(offset, len));
    }

    // Compute XOR parity (single recovery block)
    var parity = new byte[sectorSize];
    for (var i = 0; i < archiveData.Length; ++i)
      parity[i % sectorSize] ^= archiveData[i];

    var rdCrc = Crc32.Compute(parity);
    var fdCrc = Crc32.Compute(archiveData);

    // Build recovery header
    using var blockMs = new MemoryStream();
    using var bw = new BinaryWriter(blockMs, Encoding.ASCII, leaveOpen: true);
    bw.Write(Encoding.ASCII.GetBytes(SqxConstants.RecoverySignature)); // BLOCK_ID "SQ4RD" (5 bytes)
    bw.Write(1);                                                       // R_LEVEL (4 bytes)
    bw.Write((long)fileBlocks);                                        // FILE_BLOCKS (8 bytes)
    bw.Write((long)1);                                                 // RD_BLOCKS (8 bytes)
    bw.Write(SqxConstants.ArcVersion11);                                // ARC_VER (1 byte)
    bw.Write(dataSize);                                                // DATA_SIZE (8 bytes)
    bw.Write(rdCrc);                                                   // RD_CRC (4 bytes)
    bw.Write(fdCrc);                                                   // FD_CRC (4 bytes)
    bw.Flush();

    var blockData = blockMs.ToArray();
    var blockSize = (ushort)(7 + blockData.Length); // crc(2) + type(1) + flags(2) + size(2) + data
    var blockCrcVal = ComputeBlockCrc(SqxConstants.BlockRecovery, 0, blockSize, blockData);

    writer.Write(blockCrcVal);
    writer.Write(SqxConstants.BlockRecovery);
    writer.Write((ushort)0); // flags
    writer.Write(blockSize);
    writer.Write(blockData);

    // Write per-block CRCs
    foreach (var crc in blockCrcs)
      writer.Write(crc);

    // Write parity block
    writer.Write(parity);
  }

  private static void WriteEndBlock(BinaryWriter writer) {
    const ushort blockSize = 7; // spec: always 7 (crc(2) + type(1) + flags(2) + size(2), no body)
    byte[] empty = [];
    var blockCrc = ComputeBlockCrc(SqxConstants.BlockEnd, 0, blockSize, empty);
    writer.Write(blockCrc);
    writer.Write(SqxConstants.BlockEnd);
    writer.Write((ushort)0); // flags
    writer.Write(blockSize);
  }

  /// <summary>
  /// Derives a 16-byte AES-128 key from a password using MD5.
  /// </summary>
  internal static byte[] DeriveKey(string password) {
    var passBytes = Encoding.UTF8.GetBytes(password);
    return Md5.Compute(passBytes);
  }

  private static ushort ComputeBlockCrc(byte type, ushort flags, ushort blockSize, byte[] blockData) {
    // CRC-16 over type + flags + size + data (everything after the CRC field)
    var crcData = new byte[5 + blockData.Length];
    crcData[0] = type;
    crcData[1] = (byte)(flags & 0xFF);
    crcData[2] = (byte)(flags >> 8);
    crcData[3] = (byte)(blockSize & 0xFF);
    crcData[4] = (byte)(blockSize >> 8);
    blockData.CopyTo(crcData, 5);
    return Crc16.Compute(crcData);
  }

  /// <summary>
  /// Creates an SQX archive split into multiple volumes.
  /// </summary>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries,
      string? password = null) {
    var w = new SqxWriter(password);
    foreach (var (name, data) in entries)
      w.AddFile(name, data);
    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(w.ToArray(), maxVolumeSize);
  }

  private static uint DosTimestamp(DateTime dt) {
    var time = (dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2);
    var date = ((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day;
    return (uint)((date << 16) | time);
  }
}
