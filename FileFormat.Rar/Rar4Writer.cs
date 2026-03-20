using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Crypto;
using Compression.Core.Dictionary.Rar;

namespace FileFormat.Rar;

/// <summary>
/// Creates RAR4 archives. Supports Store and compressed (LZ+Huffman) methods,
/// with optional AES-128-CBC encryption.
/// RAR4 uses the v2.9 (UnPack29) compression algorithm.
/// </summary>
public sealed class Rar4Writer : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly int _method;
  private readonly int _windowBits;
  private readonly bool _solid;
  private readonly string? _password;
  private Rar3Encoder? _solidEncoder;
  private bool _isFirstFile = true;
  private bool _headerWritten;
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="Rar4Writer"/>.
  /// </summary>
  /// <param name="stream">The stream to write the RAR4 archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="method">RAR4 compression method (0x30=Store, 0x31-0x35=compressed). Default Normal.</param>
  /// <param name="windowBits">Window size as log2 (15-22). Default 20 (1MB).</param>
  /// <param name="solid">Whether to create a solid archive.</param>
  /// <param name="password">Optional password for AES-128 encryption.</param>
  public Rar4Writer(Stream stream, bool leaveOpen = false,
      byte method = RarConstants.Rar4MethodNormal,
      int windowBits = 20, bool solid = false, string? password = null) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._method = method;
    this._windowBits = Math.Clamp(windowBits, 15, 22);
    this._solid = solid;
    this._password = password;
  }

  /// <summary>
  /// Adds a file entry to the archive.
  /// </summary>
  public void AddFile(string fileName, ReadOnlySpan<byte> data, DateTimeOffset? modifiedTime = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    EnsureHeader();

    byte[] uncompressed = data.ToArray();
    uint dataCrc = Crc32.Compute(uncompressed);

    byte[] compressed;
    byte actualMethod;

    if (this._method == RarConstants.Rar4MethodStore || data.Length == 0) {
      compressed = uncompressed;
      actualMethod = RarConstants.Rar4MethodStore;
    } else {
      Rar3Encoder encoder;
      if (this._solid) {
        this._solidEncoder ??= new Rar3Encoder(this._windowBits);
        encoder = this._solidEncoder;
      } else {
        encoder = new Rar3Encoder(this._windowBits);
      }
      compressed = encoder.Compress(data);

      if (compressed.Length >= uncompressed.Length) {
        compressed = uncompressed;
        actualMethod = RarConstants.Rar4MethodStore;
      } else {
        actualMethod = (byte)this._method;
      }
    }

    // Encrypt compressed data if password is set
    byte[]? salt = null;
    if (this._password != null) {
      salt = RandomNumberGenerator.GetBytes(8);
      var (key, iv) = KeyDerivation.Rar3DeriveKey(this._password, salt);
      // Pad to 16-byte boundary for AES
      int padded = (compressed.Length + 15) & ~15;
      if (padded != compressed.Length) {
        var tmp = new byte[padded];
        compressed.CopyTo(tmp, 0);
        compressed = tmp;
      }
      compressed = AesCryptor.EncryptCbcNoPaddingAny(compressed, key, iv);
    }

    // Build file header
    byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);

    // File flags
    ushort fileFlags = RarConstants.Rar4FlagAddSize;
    if (this._solid && !this._isFirstFile)
      fileFlags |= RarConstants.Rar4FlagSolid;
    if (this._password != null)
      fileFlags |= RarConstants.Rar4FlagEncrypted;
    this._isFirstFile = false;

    // Determine UnPack version
    byte unpackVer = actualMethod == RarConstants.Rar4MethodStore ? (byte)20 : (byte)29;

    // Dictionary size shift for header
    // dictSizeShift = windowBits - 16 (0-6), stored in bits 5-7 of flags
    int dictShift = Math.Clamp(this._windowBits - 16, 0, 7);
    fileFlags |= (ushort)((dictShift & 0x07) << 5);

    // MS-DOS date/time
    uint dosTime = modifiedTime != null ? MsDosDateTime(modifiedTime.Value) : MsDosDateTime(DateTimeOffset.Now);

    // Build the file header:
    // HEAD_CRC(2) + HEAD_TYPE(1) + HEAD_FLAGS(2) + HEAD_SIZE(2) + PACK_SIZE(4) + UNP_SIZE(4)
    // + HOST_OS(1) + FILE_CRC(4) + FTIME(4) + UNP_VER(1) + METHOD(1) + NAME_SIZE(2) + ATTR(4)
    // + FILE_NAME(nameSize) [+ SALT(8) if encrypted]
    int saltSize = salt != null ? 8 : 0;
    int headerBodySize = 7 + 4 + 4 + 1 + 4 + 4 + 1 + 1 + 2 + 4 + nameBytes.Length + saltSize;
    // HEAD_SIZE includes itself
    ushort headSize = (ushort)(headerBodySize);

    using var headerMs = new MemoryStream();
    using var bw = new BinaryWriter(headerMs, Encoding.ASCII, leaveOpen: true);

    // Placeholder for CRC (will overwrite)
    bw.Write((ushort)0); // HEAD_CRC
    bw.Write(RarConstants.Rar4TypeFile); // HEAD_TYPE
    bw.Write(fileFlags); // HEAD_FLAGS
    bw.Write(headSize); // HEAD_SIZE
    bw.Write((uint)compressed.Length); // PACK_SIZE
    bw.Write((uint)uncompressed.Length); // UNP_SIZE
    bw.Write((byte)0); // HOST_OS (MS DOS)
    bw.Write(dataCrc); // FILE_CRC
    bw.Write(dosTime); // FTIME
    bw.Write(unpackVer); // UNP_VER
    bw.Write(actualMethod); // METHOD
    bw.Write((ushort)nameBytes.Length); // NAME_SIZE
    bw.Write((uint)0x20); // ATTR (archive attribute)
    bw.Write(nameBytes); // FILE_NAME
    if (salt != null)
      bw.Write(salt); // SALT (8 bytes)
    bw.Flush();

    byte[] headerData = headerMs.ToArray();

    // Compute CRC-16 over header from HEAD_TYPE onwards (offset 2)
    uint headerCrc32 = Crc32.Compute(headerData.AsSpan(2));
    ushort headerCrc16 = (ushort)(headerCrc32 & 0xFFFF);
    BinaryPrimitives.WriteUInt16LittleEndian(headerData, headerCrc16);

    this._stream.Write(headerData);
    this._stream.Write(compressed);
  }

  /// <summary>
  /// Writes the end-of-archive header and flushes.
  /// </summary>
  public void Finish() {
    if (this._finished) return;
    this._finished = true;
    EnsureHeader();
    WriteEndHeader();
    this._stream.Flush();
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._finished) Finish();
      if (!this._leaveOpen) this._stream.Dispose();
    }
  }

  /// <summary>
  /// Creates a RAR4 archive split into multiple volumes.
  /// </summary>
  /// <param name="maxVolumeSize">Maximum volume size in bytes.</param>
  /// <param name="entries">Files to add.</param>
  /// <param name="method">Compression method.</param>
  /// <param name="password">Optional password for AES-128 encryption.</param>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries,
      byte method = RarConstants.Rar4MethodNormal, string? password = null) {
    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true, method: method, password: password)) {
      foreach (var (name, data) in entries)
        writer.AddFile(name, data);
      writer.Finish();
    }
    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(ms.ToArray(), maxVolumeSize);
  }

  private void EnsureHeader() {
    if (this._headerWritten) return;
    this._headerWritten = true;

    // RAR4 signature
    this._stream.Write(RarConstants.Rar4Signature);

    // Main archive header: type 0x73
    ushort mainFlags = 0;
    if (this._solid) mainFlags |= RarConstants.Rar4FlagSolid;

    WriteSimpleHeader(RarConstants.Rar4TypeMain, mainFlags);
  }

  private void WriteSimpleHeader(byte type, ushort flags) {
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

    bw.Write((ushort)0); // HEAD_CRC placeholder
    bw.Write(type); // HEAD_TYPE
    bw.Write(flags); // HEAD_FLAGS
    ushort headSize = 7; // minimum: CRC(2) + TYPE(1) + FLAGS(2) + SIZE(2)
    bw.Write(headSize); // HEAD_SIZE
    bw.Flush();

    byte[] headerData = ms.ToArray();
    uint crc32 = Crc32.Compute(headerData.AsSpan(2));
    ushort crc16 = (ushort)(crc32 & 0xFFFF);
    BinaryPrimitives.WriteUInt16LittleEndian(headerData, crc16);

    this._stream.Write(headerData);
  }

  private void WriteEndHeader() {
    WriteSimpleHeader(RarConstants.Rar4TypeEnd, 0);
  }

  private static uint MsDosDateTime(DateTimeOffset dto) {
    var dt = dto.LocalDateTime;
    int time = (dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2);
    int date = ((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day;
    return (uint)((date << 16) | time);
  }
}
