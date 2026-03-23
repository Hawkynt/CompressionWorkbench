using Compression.Core.Checksums;
using Compression.Core.Crypto;
using Compression.Core.Dictionary.Rar;
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Rar;

/// <summary>
/// Reads entries from a RAR v4 or v5 archive.
/// </summary>
public sealed class RarReader : IDisposable {
  static RarReader() {
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
  }

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<RarEntry> _entries = [];
  private readonly List<(long DataOffset, long DataSize, RarFileHeader FileHeader)> _entryDetails = [];
  private readonly List<(long DataOffset, long DataSize, int Method, long UnpackedSize, int WindowBits, bool IsSolid, int Version, byte[]? Salt)> _rar4Details = [];
  private bool _disposed;
  private readonly string? _password;

  // Encryption state from encryption header
  private byte[]? _encryptionSalt;
  private int _encryptionKdfCount;
  private byte[]? _derivedKey;

  // Solid archive state: decoder persists across files in a solid block
  private Rar5Decoder? _solidDecoder;
  private int _lastSolidDictSize;
  private Rar3Decoder? _solidRar3Decoder;

  // Recovery record state
  private long _recoveryHeaderOffset; // start of recovery service header
  private long _recoveryDataOffset;   // start of recovery parity data (after header)
  private long _recoveryDataSize;

  /// <summary>
  /// Gets the entries in the RAR archive.
  /// </summary>
  public IReadOnlyList<RarEntry> Entries => this._entries;

  /// <summary>
  /// Gets a value indicating whether this is a RAR5 format archive.
  /// </summary>
  public bool IsRar5 { get; }

  /// <summary>
  /// Gets a value indicating whether this is a RAR4 (or earlier) format archive.
  /// </summary>
  public bool IsRar4 { get; }

  /// <summary>
  /// Gets the RAR version (1-5) for the overall archive format.
  /// </summary>
  public int Version { get; }

  /// <summary>Gets whether this archive has a recovery record.</summary>
  public bool HasRecoveryRecord { get; private set; }

  /// <summary>
  /// Initializes a new <see cref="RarReader"/> with a password for encrypted archives.
  /// </summary>
  /// <param name="stream">A seekable stream containing the RAR archive.</param>
  /// <param name="password">The password for encrypted entries, or <see langword="null"/> for unencrypted archives.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this reader is disposed.</param>
  public RarReader(Stream stream, string? password, bool leaveOpen = false)
    : this(stream, leaveOpen) {
    this._password = password;
  }

  /// <summary>
  /// Initializes a new <see cref="RarReader"/> from a seekable stream containing a RAR archive.
  /// </summary>
  /// <param name="stream">A seekable stream containing the RAR archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this reader is disposed.</param>
  /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException">The stream is not seekable.</exception>
  /// <exception cref="InvalidDataException">The stream does not contain a valid RAR archive.</exception>
  public RarReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._leaveOpen = leaveOpen;

    // Read and validate signature
    var signature = new byte[8];
    var sigRead = 0;
    while (sigRead < 8) {
      var read = this._stream.Read(signature, sigRead, 8 - sigRead);
      if (read == 0)
        throw new InvalidDataException("Stream too short to contain a RAR signature.");
      sigRead += read;
    }

    if (signature.AsSpan().SequenceEqual(RarConstants.Rar5Signature)) {
      IsRar5 = true;
      Version = 5;
    } else if (signature.AsSpan(0, 7).SequenceEqual(RarConstants.Rar4Signature)) {
      IsRar4 = true;
      Version = 4; // Could be 1-4, determined by UnpVer in file headers
      // RAR4 signature is 7 bytes; back up 1 byte since we read 8
      this._stream.Position -= 1;
    } else
      throw new InvalidDataException("Not a valid RAR archive.");

    // Read headers
    if (IsRar5)
      ReadHeaders();
    else
      ReadRar4Headers();
  }

  /// <summary>
  /// Extracts the data for the entry at the specified index.
  /// </summary>
  /// <param name="entryIndex">The zero-based index of the entry to extract.</param>
  /// <returns>The decompressed data as a byte array.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="entryIndex"/> is out of range.</exception>
  /// <exception cref="InvalidDataException">The CRC-32 of the extracted data does not match.</exception>
  /// <exception cref="NotSupportedException">The compression method is not supported.</exception>
  public byte[] Extract(int entryIndex) {
    using var ms = new MemoryStream();
    Extract(entryIndex, ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Extracts the data for the entry at the specified index into the given output stream.
  /// </summary>
  /// <param name="entryIndex">The zero-based index of the entry to extract.</param>
  /// <param name="output">The stream to write the decompressed data to.</param>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="entryIndex"/> is out of range.</exception>
  /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
  /// <exception cref="InvalidDataException">The CRC-32 of the extracted data does not match.</exception>
  /// <exception cref="NotSupportedException">The compression method is not supported.</exception>
  public void Extract(int entryIndex, Stream output) {
    if ((uint)entryIndex >= (uint)this._entries.Count)
      throw new ArgumentOutOfRangeException(nameof(entryIndex));
    ArgumentNullException.ThrowIfNull(output);

    var entry = this._entries[entryIndex];

    if (entry.IsDirectory || entry.Size == 0)
      return;

    if (IsRar4) {
      ExtractRar4(entryIndex, output);
      return;
    }

    var (dataOffset, dataSize, fileHeader) = this._entryDetails[entryIndex];

    // Seek to data
    this._stream.Position = dataOffset;

    // If encrypted, read all data, decrypt, then wrap in a MemoryStream
    var inputStream = this._stream;
    var effectiveDataSize = dataSize;

    if (fileHeader.IsEncrypted) {
      if (fileHeader.EncryptionIv == null)
        throw new InvalidDataException($"Entry '{entry.Name}' is encrypted but has no IV.");

      var encryptedData = new byte[dataSize];
      ReadExact(this._stream, encryptedData, 0, encryptedData.Length);
      var decryptedData = DecryptData(encryptedData, fileHeader.EncryptionIv);

      // Decrypted data may have AES padding bytes; trim to unpacked compressed size
      inputStream = new MemoryStream(decryptedData);
      effectiveDataSize = decryptedData.Length;
    }

    // Determine whether to reuse the solid decoder.
    // A file with IsSolid=true continues from the previous decoder state.
    // A file with IsSolid=false starts a fresh decoder (beginning of a solid block or non-solid).
    Rar5Decoder? existingDecoder = null;
    var dictSize = fileHeader.CompressionMethod == RarConstants.MethodStore ? 0 : fileHeader.DictionarySize;

    if (fileHeader.IsSolid && this._solidDecoder != null && this._lastSolidDictSize == dictSize)
      existingDecoder = this._solidDecoder;
    else {
      // Not solid, or dictionary size changed — start fresh
      this._solidDecoder = null;
    }

    // Create decompressor
    var decompressor = new RarDecompressor(
      inputStream,
      fileHeader.CompressionMethod,
      fileHeader.UnpackedSize,
      dictSize,
      effectiveDataSize,
      existingDecoder);

    // Read all decompressed data
    var buffer = new byte[8192];
    var totalWritten = 0L;
    while (!decompressor.IsFinished) {
      var read = decompressor.Read(buffer, 0, buffer.Length);
      if (read == 0)
        break;
      output.Write(buffer, 0, read);
      totalWritten += read;
    }

    // Preserve decoder state for subsequent solid files
    if (decompressor.Decoder != null) {
      this._solidDecoder = decompressor.Decoder;
      this._lastSolidDictSize = dictSize;
    }

    // Verify CRC-32 if present
    if (entry.Crc.HasValue && totalWritten > 0) {
      output.Flush();
      if (output is MemoryStream ms && ms.TryGetBuffer(out var seg)) {
        var computed = Crc32.Compute(seg.AsSpan());
        if (computed != entry.Crc.Value)
          throw new InvalidDataException(
            $"CRC-32 mismatch for '{entry.Name}': expected 0x{entry.Crc.Value:X8}, computed 0x{computed:X8}.");
      }
    }
  }

  private void ExtractRar4(int entryIndex, Stream output) {
    var entry = this._entries[entryIndex];
    var detail = this._rar4Details[entryIndex];

    this._stream.Position = detail.DataOffset;
    var compressed = new byte[detail.DataSize];
    ReadExact(this._stream, compressed, 0, compressed.Length);

    // Decrypt if encrypted
    if (detail.Salt != null) {
      if (this._password == null)
        throw new InvalidOperationException($"Entry '{entry.Name}' is encrypted but no password was provided.");

      var (key, iv) = KeyDerivation.Rar3DeriveKey(this._password, detail.Salt);
      // RAR3 pads to 16-byte boundary
      var alignedLen = (compressed.Length + 15) & ~15;
      if (alignedLen > compressed.Length) {
        var padded = new byte[alignedLen];
        compressed.AsSpan().CopyTo(padded);
        compressed = padded;
      }
      compressed = AesCryptor.DecryptCbcNoPaddingAny(compressed, key, iv);
      // Trim to original packed size
      if (compressed.Length > detail.DataSize)
        compressed = compressed[..(int)detail.DataSize];
    }

    byte[] decompressed;
    if (detail.Method == RarConstants.Rar4MethodStore) {
      // Trim to unpack size — encrypted Store data may have AES padding
      decompressed = compressed.Length > detail.UnpackedSize
        ? compressed[..(int)detail.UnpackedSize]
        : compressed;
    } else {
      var unpackVer = detail.Version;
      if (unpackVer <= 15) {
        var decoder = new Rar1Decoder();
        decompressed = decoder.Decompress(compressed, (int)detail.UnpackedSize);
      } else if (unpackVer <= 20) {
        var decoder = new Rar2Decoder();
        decompressed = decoder.Decompress(compressed, (int)detail.UnpackedSize);
      } else if (unpackVer <= 36) {
        Rar3Decoder decoder;
        if (detail.IsSolid && this._solidRar3Decoder != null)
          decoder = this._solidRar3Decoder;
        else
          decoder = new Rar3Decoder();
        decompressed = decoder.Decompress(compressed, (int)detail.UnpackedSize, detail.WindowBits);
        this._solidRar3Decoder = decoder;
      } else
        throw new NotSupportedException($"RAR unpack version {unpackVer} is not supported in RAR4 archives.");
    }

    output.Write(decompressed, 0, decompressed.Length);

    // Verify CRC-32
    if (entry.Crc.HasValue) {
      var computed = Crc32.Compute(decompressed);
      if (computed != entry.Crc.Value)
        throw new InvalidDataException(
          $"CRC-32 mismatch for '{entry.Name}': expected 0x{entry.Crc.Value:X8}, computed 0x{computed:X8}.");
    }
  }

  /// <summary>
  /// Verifies the recovery record by checking Reed-Solomon parity data against archive contents.
  /// Returns <see langword="true"/> if the parity is valid.
  /// </summary>
  public bool VerifyRecoveryRecord() {
    if (!this.HasRecoveryRecord || this._recoveryDataSize == 0)
      return false;

    // Recovery data covers archive from position 0 to the start of the recovery service header
    var archiveDataSize = this._recoveryHeaderOffset;
    var sectorSize = RarConstants.RecoverySectorSize;
    var dataSectors = (int)((archiveDataSize + sectorSize - 1) / sectorSize);

    // Determine parity sector count
    var paritySectors = (int)(this._recoveryDataSize / sectorSize);
    if (paritySectors == 0) return false;

    // Read all archive data as sectors
    var dataShards = new byte[dataSectors][];
    this._stream.Position = 0;
    for (var i = 0; i < dataSectors; ++i) {
      dataShards[i] = new byte[sectorSize];
      var toRead = (int)Math.Min(sectorSize, archiveDataSize - (long)i * sectorSize);
      var totalRead = 0;
      while (totalRead < toRead) {
        var n = this._stream.Read(dataShards[i], totalRead, toRead - totalRead);
        if (n == 0) break;
        totalRead += n;
      }
    }

    // Recompute parity
    var rs = new Compression.Core.Checksums.ReedSolomon(dataSectors, paritySectors);
    var computedParity = rs.Encode(dataShards);

    // Read stored parity
    this._stream.Position = this._recoveryDataOffset;
    var storedRecovery = new byte[this._recoveryDataSize];
    var r = 0;
    while (r < storedRecovery.Length) {
      var n = this._stream.Read(storedRecovery, r, storedRecovery.Length - r);
      if (n == 0) break;
      r += n;
    }

    // Compare
    for (var i = 0; i < paritySectors; ++i) {
      if (!computedParity[i].AsSpan().SequenceEqual(
          storedRecovery.AsSpan(i * sectorSize, sectorSize)))
        return false;
    }

    return true;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  private void ReadHeaders() {
    // First header should be the main archive header
    var mainHeader = RarHeader.Read(this._stream);
    if (mainHeader.HeaderType != RarConstants.HeaderTypeMain)
      throw new InvalidDataException(
        $"Expected main archive header (type {RarConstants.HeaderTypeMain}), got type {mainHeader.HeaderType}.");

    // Skip data area of main header if present
    if (mainHeader.HasDataArea && mainHeader.DataSize > 0)
      this._stream.Position += mainHeader.DataSize;

    // Read remaining headers
    while (this._stream.Position < this._stream.Length) {
      var headerStartPos = this._stream.Position;
      RarHeader header;

      try {
        header = RarHeader.Read(this._stream);
      }
      catch (EndOfStreamException) {
        break;
      }

      switch (header.HeaderType) {
        case RarConstants.HeaderTypeFile:
          ReadFileEntry(header);
          break;

        case RarConstants.HeaderTypeEndArchive:
          return; // Done reading

        case RarConstants.HeaderTypeEncryption:
          ParseEncryptionHeader(header);
          break;

        case RarConstants.HeaderTypeService:
          ParseServiceHeader(header, headerStartPos);
          break;

        default:
          // Skip data area if present
          if (header.HasDataArea && header.DataSize > 0)
            this._stream.Position += header.DataSize;
          break;
      }
    }
  }

  private void ReadRar4Headers() {
    // RAR4 starts after the 7-byte signature.
    // The first header is the marker block (type 0x72), already consumed as part of signature.
    // Next is the main archive header (type 0x73).

    while (this._stream.Position < this._stream.Length) {
      // Read RAR4 block header: HEAD_CRC(2) + HEAD_TYPE(1) + HEAD_FLAGS(2) + HEAD_SIZE(2)
      var headerBuf = new byte[7];
      if (!TryReadExact(this._stream, headerBuf, 0, 7))
        break;

      var headType = headerBuf[2];
      var headFlags = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf.AsSpan(3));
      var headSize = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf.AsSpan(5));

      // Read additional data size if ADD_SIZE flag is set
      long addSize = 0;
      if ((headFlags & RarConstants.Rar4FlagAddSize) != 0 && headSize > 7) {
        // ADD_SIZE is stored as 4 bytes after the basic 7-byte header in some block types
        // For file headers, it's part of the extended header
      }

      if (headType == RarConstants.Rar4TypeEnd)
        break;

      if (headType == RarConstants.Rar4TypeFile) {
        // RAR4 file header: after the 7-byte common header:
        // PACK_SIZE(4) + UNP_SIZE(4) + HOST_OS(1) + FILE_CRC(4) + FTIME(4) +
        // UNP_VER(1) + METHOD(1) + NAME_SIZE(2) + ATTR(4)
        // = 25 more bytes minimum
        var remaining = headSize - 7;
        if (remaining < 25) {
          // Skip malformed header
          this._stream.Position += remaining;
          continue;
        }

        var fileBuf = new byte[remaining];
        ReadExact(this._stream, fileBuf, 0, remaining);

        var packSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(fileBuf);
        var unpSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(fileBuf.AsSpan(4));
        var fileCrc = BinaryPrimitives.ReadUInt32LittleEndian(fileBuf.AsSpan(9));
        var unpVer = fileBuf[17];
        var method = fileBuf[18];
        var nameSize = BinaryPrimitives.ReadUInt16LittleEndian(fileBuf.AsSpan(19));

        // High 4 bytes of pack/unpack size for large files
        if ((headFlags & RarConstants.Rar4FlagLargeFile) != 0 && remaining >= 25 + 8) {
          var highPackSize = BinaryPrimitives.ReadUInt32LittleEndian(fileBuf.AsSpan(25));
          var highUnpSize = BinaryPrimitives.ReadUInt32LittleEndian(fileBuf.AsSpan(29));
          packSize |= (long)highPackSize << 32;
          unpSize |= (long)highUnpSize << 32;
        }

        // Read filename
        var nameOffset = 25;
        if ((headFlags & RarConstants.Rar4FlagLargeFile) != 0)
          nameOffset += 8;

        var name = "";
        if (nameSize > 0 && nameOffset + nameSize <= remaining) {
          if ((headFlags & RarConstants.Rar4FlagUnicode) != 0) {
            // Find the zero terminator between the OEM name and Unicode name
            var zeroPos = Array.IndexOf(fileBuf, (byte)0, nameOffset, nameSize);
            if (zeroPos >= 0)
              name = Encoding.ASCII.GetString(fileBuf, nameOffset, zeroPos - nameOffset);
            else
              name = Encoding.ASCII.GetString(fileBuf, nameOffset, nameSize);
          } else
            name = Encoding.GetEncoding(437).GetString(fileBuf, nameOffset, nameSize);
        }

        // Normalize path separators
        name = name.Replace('\\', '/');

        var isDirectory = (headFlags & RarConstants.Rar4FlagDirectory) == RarConstants.Rar4FlagDirectory;
        var isEncrypted = (headFlags & RarConstants.Rar4FlagEncrypted) != 0;

        // Read 8-byte salt if file is encrypted
        byte[]? salt = null;
        if (isEncrypted) {
          var saltOffset = nameOffset + nameSize;
          if (saltOffset + 8 <= remaining) {
            salt = new byte[8];
            fileBuf.AsSpan(saltOffset, 8).CopyTo(salt);
          }
        }

        // Window bits: RAR3/4 encodes dict size in the flags
        // For RAR3 (unpVer 29): dict size = 64KB * 2^((flags>>5)&7), max 4MB
        var windowBits = 20; // Default 1MB
        if (unpVer >= 29) {
          var dictSizeShift = (headFlags >> 5) & 7;
          windowBits = 16 + (int)dictSizeShift;
          if (windowBits > 22) windowBits = 22; // Cap at 4MB for RAR3
        }

        var dataOffset = this._stream.Position;

        var entry = new RarEntry {
          Name = name,
          Size = unpSize,
          CompressedSize = packSize,
          IsDirectory = isDirectory,
          IsEncrypted = isEncrypted,
          CompressionMethod = method - RarConstants.Rar4MethodStore, // Normalize to 0-5
          Crc = fileCrc
        };

        this._entries.Add(entry);
        this._rar4Details.Add((dataOffset, packSize, method,
          unpSize, windowBits, (headFlags & RarConstants.Rar4FlagSolid) != 0, unpVer, salt));

        // Skip past compressed data
        if (packSize > 0)
          this._stream.Position = dataOffset + packSize;
      } else {
        // Skip other header types
        var remaining = headSize - 7;

        // For headers with ADD_SIZE, read the additional data size
        if ((headFlags & RarConstants.Rar4FlagAddSize) != 0 && remaining >= 4) {
          var addSizeBuf = new byte[4];
          ReadExact(this._stream, addSizeBuf, 0, 4);
          addSize = BinaryPrimitives.ReadUInt32LittleEndian(addSizeBuf);
          remaining -= 4;
        }

        if (remaining > 0)
          this._stream.Position += remaining;
        if (addSize > 0)
          this._stream.Position += addSize;
      }
    }
  }

  private static bool TryReadExact(Stream stream, byte[] buffer, int offset, int count) {
    var totalRead = 0;
    while (totalRead < count) {
      var read = stream.Read(buffer, offset + totalRead, count - totalRead);
      if (read == 0) return false;
      totalRead += read;
    }
    return true;
  }

  private static void ReadExact(Stream stream, byte[] buffer, int offset, int count) {
    var totalRead = 0;
    while (totalRead < count) {
      var read = stream.Read(buffer, offset + totalRead, count - totalRead);
      if (read == 0) throw new EndOfStreamException();
      totalRead += read;
    }
  }

  private void ReadFileEntry(RarHeader header) {
    var fileHeader = RarFileHeader.Read(header.RawHeaderData!, header);

    // Record data offset (current position, after header)
    var dataOffset = this._stream.Position;
    var dataSize = header.DataSize;

    // Build public entry
    var entry = new RarEntry {
      Name = fileHeader.FileName,
      Size = fileHeader.UnpackedSize,
      CompressedSize = dataSize,
      IsDirectory = fileHeader.IsDirectory,
      CompressionMethod = fileHeader.CompressionMethod,
      IsSolid = fileHeader.IsSolid,
      IsEncrypted = fileHeader.IsEncrypted,
    };

    if (fileHeader.HasMtime)
      entry.ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(fileHeader.Mtime);

    if (fileHeader.HasCrc)
      entry.Crc = fileHeader.DataCrc;

    this._entries.Add(entry);
    this._entryDetails.Add((dataOffset, dataSize, fileHeader));

    // Skip past data area
    if (dataSize > 0)
      this._stream.Position = dataOffset + dataSize;
  }

  private void ParseServiceHeader(RarHeader header, long headerStartPos) {
    if (header.RawHeaderData == null) {
      if (header.HasDataArea && header.DataSize > 0)
        this._stream.Position += header.DataSize;
      return;
    }

    // Try to read the service name from the header body
    ReadOnlySpan<byte> data = header.RawHeaderData;
    var offset = 0;

    // Skip type + flags vints
    _ = RarVint.Read(data[offset..], out var consumed); offset += consumed;
    _ = RarVint.Read(data[offset..], out consumed); offset += consumed;
    if (header.HasExtraArea) { _ = RarVint.Read(data[offset..], out consumed); offset += consumed; }
    if (header.HasDataArea) { _ = RarVint.Read(data[offset..], out consumed); offset += consumed; }

    // Remaining bytes are the service name
    if (offset < data.Length) {
      var name = System.Text.Encoding.UTF8.GetString(data[offset..]);
      if (name == RarConstants.RecoveryRecordName && header.HasDataArea && header.DataSize > 0) {
        this.HasRecoveryRecord = true;
        this._recoveryHeaderOffset = headerStartPos;
        this._recoveryDataOffset = this._stream.Position;
        this._recoveryDataSize = header.DataSize;
      }
    }

    // Skip data area
    if (header.HasDataArea && header.DataSize > 0)
      this._stream.Position += header.DataSize;
  }

  private void ParseEncryptionHeader(RarHeader header) {
    if (header.RawHeaderData == null) return;
    ReadOnlySpan<byte> data = header.RawHeaderData;
    var offset = 0;

    // Skip type + flags vints (already parsed)
    _ = RarVint.Read(data[offset..], out var consumed); offset += consumed; // type
    _ = RarVint.Read(data[offset..], out consumed); offset += consumed; // flags

    // Skip extra area size and data area size if present
    if (header.HasExtraArea) { _ = RarVint.Read(data[offset..], out consumed); offset += consumed; }
    if (header.HasDataArea) { _ = RarVint.Read(data[offset..], out consumed); offset += consumed; }

    // Encryption version (vint, must be 0 for AES-256)
    var encVersion = (int)RarVint.Read(data[offset..], out consumed); offset += consumed;
    if (encVersion != RarConstants.EncryptionVersionAes256) return;

    // Encryption flags (vint)
    var encFlags = (int)RarVint.Read(data[offset..], out consumed); offset += consumed;

    // KDF count (1 byte, log2 of iteration count)
    if (offset >= data.Length) return;
    this._encryptionKdfCount = 1 << (data[offset] + 1);
    offset += 1;

    // Salt (16 bytes)
    if (offset + 16 > data.Length) return;
    this._encryptionSalt = data.Slice(offset, 16).ToArray();

    // Skip data area if present
    if (header.HasDataArea && header.DataSize > 0)
      this._stream.Position += header.DataSize;
  }

  private byte[] DeriveKey() {
    if (this._derivedKey != null)
      return this._derivedKey;

    if (this._password == null)
      throw new InvalidOperationException("Encrypted RAR archive requires a password.");
    if (this._encryptionSalt == null)
      throw new InvalidDataException("No encryption header found in RAR archive.");

    this._derivedKey = KeyDerivation.Rar5DeriveKey(this._password, this._encryptionSalt, this._encryptionKdfCount);
    return this._derivedKey;
  }

  private byte[] DecryptData(byte[] encryptedData, byte[] iv) {
    var key = DeriveKey();
    // RAR5 uses AES-256-CBC with no padding. Data is padded to 16-byte boundary.
    return AesCryptor.DecryptCbcNoPadding(encryptedData, key, iv);
  }
}
