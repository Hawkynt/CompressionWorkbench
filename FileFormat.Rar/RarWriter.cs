using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Crypto;
using Compression.Core.Dictionary.Rar;

namespace FileFormat.Rar;

/// <summary>
/// Creates RAR5 archives. Supports Store and compressed (LZ+Huffman) methods,
/// optional solid mode, and optional AES-256 encryption.
/// </summary>
public sealed class RarWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly int _method;
  private readonly int _dictionarySize;
  private readonly bool _solid;
  private readonly string? _password;
  private readonly int _recoveryPercent;
  private readonly bool _encryptHeaders;
  private Rar5Encoder? _solidEncoder;
  private bool _isFirstFile = true;
  private bool _headerWritten;
  private bool _finished;
  private bool _disposed;

  // Encryption state
  private byte[]? _encryptionKey;
  private byte[]? _encryptionSalt;
  private int _kdfCount = 1 << 16; // 2^15 shifted by +1 in storage = log2(65536) - 1 = 15

  /// <summary>
  /// Initializes a new <see cref="RarWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the RAR archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="method">Compression method: 0=Store, 1-5=compressed (default: Normal=3).</param>
  /// <param name="dictionarySizeLog">Dictionary size as log2 value (17-28, default 17 = 128KB).</param>
  /// <param name="solid">Whether to create a solid archive (shared dictionary across files).</param>
  /// <param name="password">Optional password for AES-256 encryption.</param>
  /// <param name="recoveryPercent">Recovery record percentage (0=none, 1-100). Default 0.</param>
  /// <param name="encryptHeaders">When true and password is set, also encrypt file names and headers.</param>
  public RarWriter(Stream stream, bool leaveOpen = false, int method = RarConstants.MethodNormal,
      int dictionarySizeLog = 17, bool solid = false, string? password = null,
      int recoveryPercent = 0, bool encryptHeaders = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._method = method;
    this._dictionarySize = 1 << Math.Clamp(dictionarySizeLog, 17, 28);
    this._solid = solid;
    this._password = password;
    this._recoveryPercent = Math.Clamp(recoveryPercent, 0, 100);
    this._encryptHeaders = encryptHeaders;
  }

  /// <summary>
  /// Adds a file entry to the archive.
  /// </summary>
  /// <param name="fileName">The file name (UTF-8).</param>
  /// <param name="data">The file data.</param>
  /// <param name="modifiedTime">Optional modification time.</param>
  public void AddFile(string fileName, ReadOnlySpan<byte> data, DateTimeOffset? modifiedTime = null) {
    ArgumentNullException.ThrowIfNull(fileName);

    EnsureHeader();

    byte[] uncompressed = data.ToArray();
    uint dataCrc = Crc32.Compute(uncompressed);

    byte[] compressed;
    int actualMethod;

    if (this._method == RarConstants.MethodStore || data.Length == 0) {
      compressed = uncompressed;
      actualMethod = RarConstants.MethodStore;
    }
    else {
      Rar5Encoder encoder;
      if (this._solid) {
        this._solidEncoder ??= new Rar5Encoder(this._dictionarySize);
        encoder = this._solidEncoder;
      }
      else {
        encoder = new Rar5Encoder(this._dictionarySize);
      }
      compressed = encoder.Compress(data);

      if (compressed.Length >= uncompressed.Length) {
        compressed = uncompressed;
        actualMethod = RarConstants.MethodStore;
      }
      else {
        actualMethod = this._method;
      }
    }

    // Encrypt if password is set
    byte[]? fileIv = null;
    if (this._password != null) {
      fileIv = RandomNumberGenerator.GetBytes(16);

      // Pad to 16-byte boundary for AES-CBC
      int paddedLen = (compressed.Length + 15) & ~15;
      if (paddedLen != compressed.Length) {
        var padded = new byte[paddedLen];
        compressed.AsSpan().CopyTo(padded);
        compressed = padded;
      }

      compressed = AesCryptor.EncryptCbcNoPadding(compressed, this._encryptionKey!, fileIv);
    }

    // Build compression info field
    int dictLog = 0;
    int ds = this._dictionarySize;
    while (ds > 1) { ds >>= 1; ++dictLog; }
    int solidBit = (this._solid && !this._isFirstFile) ? 0x40 : 0;
    int compressionInfo = solidBit | (actualMethod << 7) | ((dictLog - 17) << 10);
    this._isFirstFile = false;

    // File flags
    int fileFlags = RarConstants.FileFlagCrc32;
    uint mtime = 0;
    if (modifiedTime != null) {
      fileFlags |= RarConstants.FileFlagTimeMtime;
      mtime = (uint)modifiedTime.Value.ToUnixTimeSeconds();
    }

    // Build extra area for encryption record
    byte[]? extraArea = null;
    if (this._password != null)
      extraArea = BuildEncryptionExtra(fileIv!);

    // Build file header body
    var bodyMs = new MemoryStream();
    RarVint.Write(bodyMs, RarConstants.HeaderTypeFile);

    // Header flags: extra area must come before data area in flags and field order
    int headerFlags = RarConstants.HeaderFlagDataArea;
    if (extraArea != null)
      headerFlags |= RarConstants.HeaderFlagExtraArea;
    RarVint.Write(bodyMs, (ulong)headerFlags);

    // Extra area size FIRST (reader expects this before data area size)
    if (extraArea != null)
      RarVint.Write(bodyMs, (ulong)extraArea.Length);

    // Data area size
    RarVint.Write(bodyMs, (ulong)compressed.Length);

    // File-specific fields
    RarVint.Write(bodyMs, (ulong)fileFlags);
    RarVint.Write(bodyMs, (ulong)uncompressed.Length);
    RarVint.Write(bodyMs, 0); // attributes

    if ((fileFlags & RarConstants.FileFlagTimeMtime) != 0)
      bodyMs.Write(BitConverter.GetBytes(mtime));

    if ((fileFlags & RarConstants.FileFlagCrc32) != 0)
      bodyMs.Write(BitConverter.GetBytes(dataCrc));

    RarVint.Write(bodyMs, (ulong)compressionInfo);
    RarVint.Write(bodyMs, RarConstants.OsWindows);

    byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
    RarVint.Write(bodyMs, (ulong)nameBytes.Length);
    bodyMs.Write(nameBytes);

    // Write extra area into body
    if (extraArea != null)
      bodyMs.Write(extraArea);

    byte[] body = bodyMs.ToArray();

    // Size vint
    var sizeMs = new MemoryStream();
    RarVint.Write(sizeMs, (ulong)body.Length);
    byte[] sizeBytes = sizeMs.ToArray();

    // CRC-32 covers sizeBytes + body
    byte[] crcData = new byte[sizeBytes.Length + body.Length];
    sizeBytes.AsSpan().CopyTo(crcData);
    body.AsSpan().CopyTo(crcData.AsSpan(sizeBytes.Length));
    uint headerCrc = Crc32.Compute(crcData);

    // Write: CRC(vint) + Size(vint) + Body
    RarVint.Write(this._stream, headerCrc);
    this._stream.Write(sizeBytes);
    this._stream.Write(body);

    // Write compressed (or encrypted) data
    this._stream.Write(compressed);
  }

  /// <summary>
  /// Writes the end-of-archive header and flushes.
  /// </summary>
  public void Finish() {
    if (this._finished) return;
    this._finished = true;

    EnsureHeader();

    if (this._recoveryPercent > 0)
      WriteRecoveryRecord();

    WriteSimpleHeader(RarConstants.HeaderTypeEndArchive, 0);
    this._stream.Flush();
  }

  /// <summary>
  /// Creates a RAR5 archive split into multiple volumes.
  /// </summary>
  /// <param name="maxVolumeSize">Maximum size of each volume in bytes.</param>
  /// <param name="entries">The entries to add (name, data pairs).</param>
  /// <param name="method">Compression method (0=Store, 1-5=compressed).</param>
  /// <param name="password">Optional password for AES-256 encryption.</param>
  /// <returns>An array of byte arrays, one per volume.</returns>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries,
      int method = RarConstants.MethodNormal,
      string? password = null) {
    using var ms = new MemoryStream();
    using (var writer = new RarWriter(ms, leaveOpen: true, method: method, password: password)) {
      foreach (var (name, data) in entries)
        writer.AddFile(name, data);
      writer.Finish();
    }

    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(ms.ToArray(), maxVolumeSize);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._finished)
        Finish();
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  private void EnsureHeader() {
    if (this._headerWritten) return;
    this._headerWritten = true;

    this._stream.Write(RarConstants.Rar5Signature);
    WriteSimpleHeader(RarConstants.HeaderTypeMain, 0);

    // Write encryption header if password is set
    if (this._password != null)
      WriteEncryptionHeader();
  }

  private void WriteEncryptionHeader() {
    this._encryptionSalt = RandomNumberGenerator.GetBytes(16);
    this._encryptionKey = KeyDerivation.Rar5DeriveKey(this._password!, this._encryptionSalt, this._kdfCount);

    // Encryption header body:
    // type(vint) + flags(vint) + encVersion(vint) + encFlags(vint) + kdfCount(1) + salt(16)
    var bodyMs = new MemoryStream();
    RarVint.Write(bodyMs, RarConstants.HeaderTypeEncryption);
    RarVint.Write(bodyMs, 0UL); // header flags: no data area, no extra area
    RarVint.Write(bodyMs, (ulong)RarConstants.EncryptionVersionAes256);
    ulong encFlags = this._encryptHeaders ? (ulong)RarConstants.EncryptFlagHeaderEncrypt : 0UL;
    RarVint.Write(bodyMs, encFlags); // encryption flags

    // KDF count: stored as log2(iterations) - 1
    int kdfLog = 0;
    int kdf = this._kdfCount;
    while (kdf > 1) { kdf >>= 1; ++kdfLog; }
    bodyMs.WriteByte((byte)(kdfLog - 1));

    // Salt (16 bytes)
    bodyMs.Write(this._encryptionSalt);

    byte[] body = bodyMs.ToArray();

    var sizeMs = new MemoryStream();
    RarVint.Write(sizeMs, (ulong)body.Length);
    byte[] sizeBytes = sizeMs.ToArray();

    byte[] crcData = new byte[sizeBytes.Length + body.Length];
    sizeBytes.AsSpan().CopyTo(crcData);
    body.AsSpan().CopyTo(crcData.AsSpan(sizeBytes.Length));
    uint crc = Crc32.Compute(crcData);

    RarVint.Write(this._stream, crc);
    this._stream.Write(sizeBytes);
    this._stream.Write(body);
  }

  private byte[] BuildEncryptionExtra(byte[] iv) {
    // Extra record: size(vint) + type(vint) + version(vint) + flags(vint) + kdfCount(1) + salt(16) + iv(16)
    var ms = new MemoryStream();

    // Record content (without the size prefix)
    var contentMs = new MemoryStream();
    RarVint.Write(contentMs, (ulong)RarConstants.FileExtraEncryption);
    RarVint.Write(contentMs, (ulong)RarConstants.EncryptionVersionAes256);
    RarVint.Write(contentMs, 0UL); // flags

    int kdfLog = 0;
    int kdf = this._kdfCount;
    while (kdf > 1) { kdf >>= 1; ++kdfLog; }
    contentMs.WriteByte((byte)(kdfLog - 1));
    contentMs.Write(this._encryptionSalt!);
    contentMs.Write(iv);

    byte[] content = contentMs.ToArray();
    RarVint.Write(ms, (ulong)content.Length);
    ms.Write(content);

    return ms.ToArray();
  }

  private void WriteRecoveryRecord() {
    // Read all archive data written so far (from start of stream to current position)
    long archiveDataEnd = this._stream.Position;
    long archiveDataSize = archiveDataEnd;

    // Compute number of recovery sectors based on percentage
    int sectorSize = RarConstants.RecoverySectorSize;
    int dataSectors = (int)((archiveDataSize + sectorSize - 1) / sectorSize);
    int paritySectors = Math.Max(1, dataSectors * this._recoveryPercent / 100);

    // Read archive data into sector-sized blocks
    var dataShards = new byte[dataSectors][];
    this._stream.Position = 0;
    for (int i = 0; i < dataSectors; ++i) {
      dataShards[i] = new byte[sectorSize];
      int toRead = (int)Math.Min(sectorSize, archiveDataSize - (long)i * sectorSize);
      int totalRead = 0;
      while (totalRead < toRead) {
        int n = this._stream.Read(dataShards[i], totalRead, toRead - totalRead);
        if (n == 0) break;
        totalRead += n;
      }
    }

    // Generate Reed-Solomon parity
    var rs = new Compression.Core.Checksums.ReedSolomon(dataSectors, paritySectors);
    byte[][] parityShards = rs.Encode(dataShards);

    // Flatten parity into single byte array
    byte[] recoveryData = new byte[paritySectors * sectorSize];
    for (int i = 0; i < paritySectors; ++i)
      parityShards[i].AsSpan().CopyTo(recoveryData.AsSpan(i * sectorSize));

    // Seek back to where we'll write the recovery header
    this._stream.Position = archiveDataEnd;

    // Build service header (type 3) with name "RR"
    // Header body: type(vint) + flags(vint) + extraSize(vint) + dataSize(vint) + name("RR")
    byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(RarConstants.RecoveryRecordName);

    var bodyMs = new MemoryStream();
    RarVint.Write(bodyMs, (ulong)RarConstants.HeaderTypeService);
    int headerFlags = RarConstants.HeaderFlagDataArea; // has data area
    RarVint.Write(bodyMs, (ulong)headerFlags);
    RarVint.Write(bodyMs, (ulong)recoveryData.Length); // data area size

    // Service data: just the name "RR" + sector info
    bodyMs.Write(nameBytes);
    byte[] body = bodyMs.ToArray();

    var sizeMs = new MemoryStream();
    RarVint.Write(sizeMs, (ulong)body.Length);
    byte[] sizeBytes = sizeMs.ToArray();

    byte[] crcData = new byte[sizeBytes.Length + body.Length];
    sizeBytes.AsSpan().CopyTo(crcData);
    body.AsSpan().CopyTo(crcData.AsSpan(sizeBytes.Length));
    uint crc = Crc32.Compute(crcData);

    RarVint.Write(this._stream, crc);
    this._stream.Write(sizeBytes);
    this._stream.Write(body);

    // Write recovery data
    this._stream.Write(recoveryData);
  }

  private void WriteSimpleHeader(int type, int flags) {
    var bodyMs = new MemoryStream();
    RarVint.Write(bodyMs, (ulong)type);
    RarVint.Write(bodyMs, (ulong)flags);
    byte[] body = bodyMs.ToArray();

    var sizeMs = new MemoryStream();
    RarVint.Write(sizeMs, (ulong)body.Length);
    byte[] sizeBytes = sizeMs.ToArray();

    byte[] crcData = new byte[sizeBytes.Length + body.Length];
    sizeBytes.AsSpan().CopyTo(crcData);
    body.AsSpan().CopyTo(crcData.AsSpan(sizeBytes.Length));
    uint crc = Crc32.Compute(crcData);

    RarVint.Write(this._stream, crc);
    this._stream.Write(sizeBytes);
    this._stream.Write(body);
  }
}
