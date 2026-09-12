using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Crypto;
using Compression.Core.Dictionary.Rar;

namespace FileFormat.Rar;

/// <summary>
/// Creates archives in the shared RAR 1.5-4.x container family. RAR4 targets support Store and
/// compressed (LZ+Huffman) methods with optional AES-128-CBC encryption; RAR 1.5 compatibility
/// deliberately emits stored, non-solid, unencrypted members with <c>UNP_VER=15</c>.
/// </summary>
public sealed class Rar4Writer : IDisposable {
  private static readonly Encoding Rar15FileNameEncoding;

  static Rar4Writer() {
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    Rar15FileNameEncoding = Encoding.GetEncoding(
      437,
      EncoderFallback.ExceptionFallback,
      DecoderFallback.ExceptionFallback);
  }

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly int _method;
  private readonly int _windowBits;
  private readonly bool _solid;
  private readonly string? _password;
  private readonly RarCompatibility _targetCompatibility;
  private Rar3Encoder? _solidEncoder;
  private bool _isFirstFile = true;
  private bool _headerWritten;
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a writer for the RAR 1.5-4.x container family.
  /// </summary>
  /// <param name="stream">The stream to write the RAR archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="method">RAR4 compression method (0x30=Store, 0x31-0x35=compressed). Default Normal.</param>
  /// <param name="windowBits">Window size as log2 (15-22). Default 20 (1MB).</param>
  /// <param name="solid">Whether to create a solid archive.</param>
  /// <param name="password">Optional password for RAR3/4 AES-128 encryption.</param>
  /// <param name="targetCompatibility">RAR generation that must be able to read the emitted archive.</param>
  public Rar4Writer(Stream stream, bool leaveOpen = false,
      byte method = RarConstants.Rar4MethodNormal,
      int windowBits = 20, bool solid = false, string? password = null,
      RarCompatibility targetCompatibility = RarCompatibility.Rar4) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._targetCompatibility = targetCompatibility;

    if (targetCompatibility is not (RarCompatibility.Rar4 or RarCompatibility.Rar1_5))
      throw new ArgumentOutOfRangeException(nameof(targetCompatibility), targetCompatibility,
        "Rar4Writer can emit only the shared RAR 1.5-4.x container family.");

    if (targetCompatibility == RarCompatibility.Rar1_5) {
      if (method != RarConstants.Rar4MethodStore)
        throw new NotSupportedException("RAR 1.5 target currently supports stored members only; Unpack15 compression is not implemented yet.");
      if (solid)
        throw new NotSupportedException("RAR 1.5 target does not use the later solid-archive semantics.");
      if (password != null)
        throw new NotSupportedException("RAR 1.5 target cannot use RAR3/4 AES encryption.");

      this._method = RarConstants.Rar4MethodStore;
      this._windowBits = 16;
      this._solid = false;
      this._password = null;
      return;
    }

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

    var uncompressed = data.ToArray();
    var dataCrc = Crc32.Compute(uncompressed);

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

    byte[]? salt = null;
    if (this._password != null) {
      salt = RandomNumberGenerator.GetBytes(8);
      var (key, iv) = KeyDerivation.Rar3DeriveKey(this._password, salt);
      var padded = (compressed.Length + 15) & ~15;
      if (padded != compressed.Length) {
        var tmp = new byte[padded];
        compressed.CopyTo(tmp, 0);
        compressed = tmp;
      }
      compressed = AesCryptor.EncryptCbcNoPaddingAny(compressed, key, iv);
    }

    byte[] nameBytes;
    try {
      nameBytes = this._targetCompatibility == RarCompatibility.Rar1_5
        ? Rar15FileNameEncoding.GetBytes(fileName)
        : Encoding.UTF8.GetBytes(fileName);
    } catch (EncoderFallbackException exception) {
      throw new NotSupportedException(
        $"Filename '{fileName}' cannot be represented by the RAR 1.5 OEM code page.", exception);
    }

    var fileFlags = RarConstants.Rar4FlagAddSize;
    if (this._solid && !this._isFirstFile)
      fileFlags |= RarConstants.Rar4FlagSolid;
    if (this._password != null)
      fileFlags |= RarConstants.Rar4FlagEncrypted;
    this._isFirstFile = false;

    var unpackVer = this._targetCompatibility == RarCompatibility.Rar1_5
      ? (byte)15
      : actualMethod == RarConstants.Rar4MethodStore ? (byte)20 : (byte)29;

    // RAR 1.5 has only the 64 KiB-era dictionary semantics and stored members do not need
    // the later window-size bits. RAR 2.9+ records the selected dictionary in bits 5-7.
    if (this._targetCompatibility != RarCompatibility.Rar1_5) {
      var dictShift = Math.Clamp(this._windowBits - 16, 0, 7);
      fileFlags |= (ushort)((dictShift & 0x07) << 5);
    }

    var dosTime = modifiedTime != null ? MsDosDateTime(modifiedTime.Value) : MsDosDateTime(DateTimeOffset.Now);

    // HEAD_SIZE includes the seven-byte generic header and all file-specific fields.
    var saltSize = salt != null ? 8 : 0;
    var headSize = checked((ushort)(32 + nameBytes.Length + saltSize));

    using var headerMs = new MemoryStream();
    using var bw = new BinaryWriter(headerMs, Encoding.ASCII, leaveOpen: true);

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
      bw.Write(salt);
    bw.Flush();

    var headerData = headerMs.ToArray();
    var headerCrc32 = Crc32.Compute(headerData.AsSpan(2));
    BinaryPrimitives.WriteUInt16LittleEndian(headerData, (ushort)(headerCrc32 & 0xFFFF));

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

    this._stream.Write(RarConstants.Rar4Signature);
    WriteMainHeader();
  }

  private void WriteMainHeader() {
    // RAR 1.5-4.x MAIN_HEAD is 13 bytes: generic seven-byte block header plus
    // RESERVED1(2) and RESERVED2(4). Older readers rely on this exact layout.
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

    ushort flags = 0;
    if (this._solid)
      flags |= 0x0008; // MHD_SOLID; FILE_HEAD's solid flag is a different bit.

    bw.Write((ushort)0); // HEAD_CRC
    bw.Write(RarConstants.Rar4TypeMain); // HEAD_TYPE
    bw.Write(flags); // HEAD_FLAGS
    bw.Write((ushort)13); // HEAD_SIZE
    bw.Write((ushort)0); // RESERVED1
    bw.Write((uint)0); // RESERVED2
    bw.Flush();

    var headerData = ms.ToArray();
    var crc32 = Crc32.Compute(headerData.AsSpan(2));
    BinaryPrimitives.WriteUInt16LittleEndian(headerData, (ushort)(crc32 & 0xFFFF));
    this._stream.Write(headerData);
  }

  private void WriteSimpleHeader(byte type, ushort flags) {
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

    bw.Write((ushort)0);
    bw.Write(type);
    bw.Write(flags);
    bw.Write((ushort)7);
    bw.Flush();

    var headerData = ms.ToArray();
    var crc32 = Crc32.Compute(headerData.AsSpan(2));
    BinaryPrimitives.WriteUInt16LittleEndian(headerData, (ushort)(crc32 & 0xFFFF));

    this._stream.Write(headerData);
  }

  private void WriteEndHeader() {
    WriteSimpleHeader(RarConstants.Rar4TypeEnd, 0);
  }

  private static uint MsDosDateTime(DateTimeOffset dto) {
    var dt = dto.LocalDateTime;
    var time = (dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2);
    var date = ((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day;
    return (uint)((date << 16) | time);
  }
}
