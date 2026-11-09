#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.BitLocker;

/// <summary>What protects a volume master key (the FVE VMK entry's protection field).</summary>
public enum BitLockerProtection {
  ClearKey,
  Tpm,
  StartupKey,
  TpmAndPin,
  RecoveryPassword,
  Password,
  SmartCard,
  Unknown,
}

/// <summary>The volume's encryption type, from the GUID in its boot sector.</summary>
public enum BitLockerEncryptionType {
  Normal,
  EncryptOnWrite,
  Unknown,
}

/// <summary>The sector transform a BitLocker volume uses.</summary>
public enum BitLockerCipherMode {
  /// <summary>AES-CBC with the Elephant diffuser (Windows Vista/7).</summary>
  CbcElephant,
  /// <summary>AES-CBC, IV = AES(K, byte offset) (Windows 8 and later "without diffuser").</summary>
  CbcEboiv,
  /// <summary>AES-XTS, tweak = sector number (Windows 10 1511 and later).</summary>
  XtsPlain64,
}

/// <summary>An AES-CCM wrapped key datum: 12-byte nonce, 16-byte tag, ciphertext.</summary>
public sealed record BitLockerWrappedKey(byte[] Nonce, byte[] Tag, byte[] Ciphertext);

/// <summary>
/// One key protector: a VMK entry with what it is protected by, the salt its key derivation
/// uses, the wrapped VMK and, for a clear-key (suspended) protector, the key that unwraps it.
/// </summary>
public sealed record BitLockerKeyProtector(
  Guid Id,
  BitLockerProtection Protection,
  ushort RawProtection,
  string? Name,
  byte[] Salt,
  BitLockerWrappedKey? WrappedVmk,
  byte[]? ClearKey);

/// <summary>
/// A BitLocker (BDE/BITLK) volume as cryptsetup reads it: the boot sector (<c>-FVE-FS-</c>, or
/// <c>MSWIN4.1</c> for BitLocker To Go), the first FVE metadata copy whose CRC-32 holds, its
/// entries (key protectors, the wrapped FVEK, the relocated volume header, the description) and
/// the validation datum that authenticates the metadata once a VMK is open.
/// </summary>
/// <remarks>Specification: <c>docs/BITLOCKER-ON-DISK.md</c>.</remarks>
public sealed partial class BitLockerVolume {
  public const string Signature = "-FVE-FS-";
  public const string ToGoSignature = "MSWIN4.1";
  public const int HeaderGuidOffset = 160;
  public const int ToGoHeaderGuidOffset = 424;
  public const int MetadataAreaSize = 64 * 1024;
  public const int BlockHeaderLength = 64;
  public const int MetadataHeaderLength = 48;
  public const int EntryHeaderLength = 8;
  public const int VmkHeaderLength = 28;
  public const int KeyDatumHeaderLength = 12;
  public const int NonceLength = 12;
  public const int TagLength = 16;
  public const int SaltLength = 16;
  public const int ValidationDataLength = 72;
  public const ushort NormalState = 0x0004;

  /// <summary>Boot-sector GUID of a fully encrypted (normal) volume.</summary>
  public static readonly byte[] NormalGuid = [0x3b, 0xd6, 0x67, 0x49, 0x29, 0x2e, 0xd8, 0x4a, 0x83, 0x99, 0xf6, 0xa3, 0x39, 0xe3, 0xd0, 0x01];

  /// <summary>Boot-sector GUID of an encrypt-on-write ("used space only", still converting) volume.</summary>
  public static readonly byte[] EncryptOnWriteGuid = [0x3b, 0x4d, 0xa8, 0x92, 0x80, 0xdd, 0x0e, 0x4d, 0x9e, 0x4e, 0xb1, 0xe3, 0x28, 0x4e, 0xae, 0xd8];

  internal static readonly byte[] BootCodeV1 = [0xeb, 0x52, 0x90];
  internal static readonly byte[] BootCodeV2 = [0xeb, 0x58, 0x90];

  // FVE entry types and value types.
  internal const ushort EntryProperty = 0x0000, EntryVmk = 0x0002, EntryFvek = 0x0003, EntryStartupKey = 0x0006,
    EntryDescription = 0x0007, EntryVolumeHeader = 0x000f, EntryVolumeGuid = 0x0019;
  internal const ushort ValueErased = 0x0000, ValueKey = 0x0001, ValueString = 0x0002, ValueStretchKey = 0x0003,
    ValueUseKey = 0x0004, ValueEncryptedKey = 0x0005, ValueTpmKey = 0x0006, ValueValidation = 0x0007, ValueVmk = 0x0008,
    ValueExternalKey = 0x0009, ValueOffsetSize = 0x000f, ValueRecoveryTime = 0x0015, ValueGuid = 0x0017, ValueHint = 0x0018;

  private BitLockerVolume() { }

  public bool IsToGo { get; private init; }
  public int SectorSize { get; private init; }
  public BitLockerEncryptionType EncryptionType { get; private init; }
  public int ValidCopy { get; private init; }
  public ushort CurrentState { get; private init; }
  public ushort NextState { get; private init; }

  /// <summary>Whether both state fields say "normal" (0x0004); cryptsetup activates nothing else.</summary>
  public bool StateIsNormal => this.CurrentState == NormalState && this.NextState == NormalState;

  public ulong VolumeSize { get; private init; }
  public int MetadataVersion { get; private init; }
  public ushort EncryptionMethod { get; private init; }
  public BitLockerCipherMode CipherMode { get; private init; }

  /// <summary>Key size in bits as cryptsetup reports it (XTS and Elephant count both keys).</summary>
  public int KeyBits { get; private init; }

  public Guid VolumeGuid { get; private init; }
  public DateTime CreationTime { get; private init; }
  public string? Description { get; private init; }
  public IReadOnlyList<long> MetadataOffsets { get; private init; } = [];
  public long VolumeHeaderOffset { get; private init; }
  public long VolumeHeaderSize { get; private init; }

  /// <summary>SHA-256 of the FVE metadata block the validation datum authenticates.</summary>
  public byte[] MetadataHash { get; private init; } = [];

  public IReadOnlyList<BitLockerKeyProtector> Protectors { get; private init; } = [];
  public BitLockerWrappedKey? WrappedFvek { get; private init; }
  public BitLockerWrappedKey Validation { get; private init; } = null!;

  /// <summary>cryptsetup's cipher-mode name.</summary>
  public string CipherModeName => ModeName(this.CipherMode);

  public static string ModeName(BitLockerCipherMode mode) => mode switch {
    BitLockerCipherMode.CbcElephant => "cbc-elephant",
    BitLockerCipherMode.CbcEboiv => "cbc-eboiv",
    _ => "xts-plain64",
  };

  /// <summary>Encryption method code → sector transform and key bits (cryptsetup's table).</summary>
  public static (BitLockerCipherMode Mode, int KeyBits)? Method(ushort code) => code switch {
    0x8000 => (BitLockerCipherMode.CbcElephant, 256),
    0x8001 => (BitLockerCipherMode.CbcElephant, 512),
    0x8002 => (BitLockerCipherMode.CbcEboiv, 128),
    0x8003 => (BitLockerCipherMode.CbcEboiv, 256),
    0x8004 => (BitLockerCipherMode.XtsPlain64, 256),
    0x8005 => (BitLockerCipherMode.XtsPlain64, 512),
    _ => null,
  };

  public static BitLockerProtection ProtectionOf(ushort raw) => raw switch {
    0x0000 => BitLockerProtection.ClearKey,
    0x0100 => BitLockerProtection.Tpm,
    0x0200 => BitLockerProtection.StartupKey,
    0x0500 => BitLockerProtection.TpmAndPin,
    0x0800 => BitLockerProtection.RecoveryPassword,
    0x1000 => BitLockerProtection.SmartCard,
    0x2000 => BitLockerProtection.Password,
    _ => BitLockerProtection.Unknown,
  };

  /// <summary>
  /// Whether <paramref name="bootSector"/> (at least 440 bytes) starts a BitLocker volume: the
  /// <c>-FVE-FS-</c> signature, or <c>MSWIN4.1</c> with one of the two BitLocker GUIDs at 424.
  /// A To Go volume's boot sector is a valid FAT32 one, so only the GUID tells it from FAT.
  /// </summary>
  public static bool IsBitLocker(ReadOnlySpan<byte> bootSector) {
    if (bootSector.Length < 11) return false;
    var oem = bootSector.Slice(3, 8);
    if (oem.SequenceEqual(Signature8)) return true;
    if (!oem.SequenceEqual(ToGoSignature8) || bootSector.Length < ToGoHeaderGuidOffset + 16) return false;
    var guid = bootSector.Slice(ToGoHeaderGuidOffset, 16);
    return guid.SequenceEqual(NormalGuid) || guid.SequenceEqual(EncryptOnWriteGuid);
  }

  internal static ReadOnlySpan<byte> Signature8 => "-FVE-FS-"u8;
  internal static ReadOnlySpan<byte> ToGoSignature8 => "MSWIN4.1"u8;

  /// <summary>Reads the boot sector and the first valid FVE metadata copy.</summary>
  /// <exception cref="InvalidDataException">Not a BitLocker volume, or no metadata copy validates.</exception>
  /// <exception cref="NotSupportedException">BitLocker version 1 (Vista beta) or an unknown encryption method.</exception>
  public static BitLockerVolume Read(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new NotSupportedException("BitLocker volumes need a seekable stream.");
    var boot = new byte[512];
    if (stream.Length < 512) throw new InvalidDataException("BitLocker: too short for a boot sector.");
    ReadAt(stream, 0, boot);

    bool toGo;
    int guidOffset;
    if (boot.AsSpan(3, 8).SequenceEqual(Signature8)) {
      toGo = false;
      guidOffset = HeaderGuidOffset;
    } else if (boot.AsSpan(3, 8).SequenceEqual(ToGoSignature8)) {
      toGo = true;
      guidOffset = ToGoHeaderGuidOffset;
    } else {
      throw new InvalidDataException("BitLocker: no -FVE-FS- or MSWIN4.1 signature.");
    }
    if (boot.AsSpan(0, 3).SequenceEqual(BootCodeV1))
      throw new NotSupportedException("BitLocker version 1 (Windows Vista beta) volumes are not supported.");
    if (!boot.AsSpan(0, 3).SequenceEqual(BootCodeV2))
      throw new InvalidDataException("BitLocker: unknown boot code.");

    int sectorSize = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
    if (sectorSize == 0) sectorSize = 512;
    if (sectorSize is not (512 or 4096))
      throw new InvalidDataException($"BitLocker: unsupported sector size {sectorSize}.");

    var guid = boot.AsSpan(guidOffset, 16);
    var type = guid.SequenceEqual(NormalGuid) ? BitLockerEncryptionType.Normal
      : guid.SequenceEqual(EncryptOnWriteGuid) ? BitLockerEncryptionType.EncryptOnWrite
      : BitLockerEncryptionType.Unknown;
    if (toGo && type == BitLockerEncryptionType.Unknown)
      throw new InvalidDataException("BitLocker: MSWIN4.1 boot sector without a BitLocker GUID (a plain FAT volume).");
    var offsets = new long[3];
    for (var i = 0; i < 3; ++i)
      offsets[i] = (long)BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(guidOffset + 16 + 8 * i));

    // The first copy whose header, validation size/version and CRC-32 hold wins.
    byte[]? block = null;
    byte[] validation = [];
    var valid = -1;
    for (var i = 0; i < 3 && valid < 0; ++i) {
      if (TryReadCopy(stream, offsets[i]) is { } copy) {
        (block, validation) = copy;
        valid = i;
      }
    }
    if (block is null)
      throw new InvalidDataException("BitLocker: no FVE metadata copy validates (signature, version or CRC-32).");

    // The valid copy must sit where it says it does; its offsets then replace the boot sector's.
    for (var i = 0; i < 3; ++i) {
      var stated = (long)BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(32 + 8 * i));
      if (i == valid && stated != offsets[i])
        throw new InvalidDataException("BitLocker: the valid FVE metadata copy is not where it says it is.");
      offsets[i] = stated;
    }

    var hash = SHA256.HashData(block);
    if (!TryParseValidation(validation, out var validationKey))
      throw new InvalidDataException("BitLocker: the FVE metadata validation datum is malformed.");

    var encryption = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(100));
    var method = Method(encryption)
      ?? throw new NotSupportedException($"BitLocker: unknown or unsupported encryption method 0x{encryption:X4}.");

    var metadataSize = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(64));
    if (metadataSize < MetadataHeaderLength + 4 || metadataSize > MetadataAreaSize)
      throw new InvalidDataException("BitLocker: implausible FVE metadata size.");
    var entriesLength = (int)metadataSize - MetadataHeaderLength;
    if (BlockHeaderLength + MetadataHeaderLength + entriesLength > block.Length)
      throw new InvalidDataException("BitLocker: the FVE entries run past the metadata block.");
    var entries = block.AsSpan(BlockHeaderLength + MetadataHeaderLength, entriesLength);

    var protectors = new List<BitLockerKeyProtector>();
    BitLockerWrappedKey? fvek = null;
    string? description = null;
    long headerOffset = 0, headerSize = 0;
    for (var start = 0; entries.Length - start >= 4;) {
      var size = BinaryPrimitives.ReadUInt16LittleEndian(entries[start..]);
      if (size == 0) break;
      if (size > entries.Length - start) throw new InvalidDataException("BitLocker: an FVE entry runs past the metadata.");
      var entry = entries.Slice(start, size);
      var entryType = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
      switch (entryType) {
        case EntryVmk:
          protectors.Add(ParseVmk(entry));
          break;
        case EntryFvek when fvek is null:
          if (size < EntryHeaderLength + NonceLength + TagLength)
            throw new InvalidDataException("BitLocker: the FVEK entry is too short.");
          fvek = Wrapped(entry[EntryHeaderLength..]);
          break;
        case EntryVolumeHeader:
          if (entries.Length - start < EntryHeaderLength + 16)
            throw new InvalidDataException("BitLocker: the volume-header entry is too short.");
          headerOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[EntryHeaderLength..]);
          headerSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[(EntryHeaderLength + 8)..]);
          break;
        case EntryDescription when description is null:
          if (size < EntryHeaderLength) throw new InvalidDataException("BitLocker: the description entry is too short.");
          description = Utf16(entry[EntryHeaderLength..]);
          break;
      }
      start += size;
    }

    var filetime = (long)BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(104));
    return new BitLockerVolume {
      IsToGo = toGo,
      SectorSize = sectorSize,
      EncryptionType = type,
      ValidCopy = valid,
      CurrentState = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(12)),
      NextState = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(14)),
      VolumeSize = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(16)),
      MetadataVersion = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(10)),
      EncryptionMethod = encryption,
      CipherMode = method.Mode,
      KeyBits = method.KeyBits,
      VolumeGuid = new Guid(block.AsSpan(80, 16)),
      CreationTime = filetime is > 0 and < 2650467743999999999 ? DateTime.FromFileTimeUtc(filetime) : DateTime.UnixEpoch,
      Description = description,
      MetadataOffsets = offsets,
      VolumeHeaderOffset = headerOffset,
      VolumeHeaderSize = headerSize,
      MetadataHash = hash,
      Protectors = protectors,
      WrappedFvek = fvek,
      Validation = validationKey,
    };
  }

  private static (byte[] Block, byte[] Validation)? TryReadCopy(Stream stream, long offset) {
    if (offset < 0 || offset > stream.Length - (BlockHeaderLength + MetadataHeaderLength)) return null;
    var head = new byte[BlockHeaderLength + MetadataHeaderLength];
    ReadAt(stream, offset, head);
    if (!head.AsSpan(0, 8).SequenceEqual(Signature8) || BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10)) != 2)
      return null;
    var blockSize = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(8)) << 4;
    if (blockSize > MetadataAreaSize || blockSize < head.Length) return null;
    // The validation block follows the metadata block; only its fixed part is read.
    const int validationLength = 8 + 8 + ValidationDataLength;
    if (offset + blockSize + validationLength > stream.Length) return null;
    var validation = new byte[validationLength];
    ReadAt(stream, offset + blockSize, validation);
    var validationSize = BinaryPrimitives.ReadUInt16LittleEndian(validation);
    var validationVersion = BinaryPrimitives.ReadUInt16LittleEndian(validation.AsSpan(2));
    if (validationSize < 8 || validationVersion > 2) return null;
    var block = new byte[blockSize];
    ReadAt(stream, offset, block);
    if (Crc32.Compute(block) != BinaryPrimitives.ReadUInt32LittleEndian(validation.AsSpan(4))) return null;
    return (block, validation);
  }

  private static bool TryParseValidation(ReadOnlySpan<byte> v, out BitLockerWrappedKey key) {
    key = null!;
    if (BinaryPrimitives.ReadUInt16LittleEndian(v) < v.Length) return false;
    if (BinaryPrimitives.ReadUInt16LittleEndian(v[8..]) != 8 + ValidationDataLength ||
        BinaryPrimitives.ReadUInt16LittleEndian(v[10..]) != 0 ||
        BinaryPrimitives.ReadUInt16LittleEndian(v[12..]) != ValueEncryptedKey)
      return false;
    key = Wrapped(v.Slice(16, ValidationDataLength));
    return true;
  }

  private static BitLockerWrappedKey Wrapped(ReadOnlySpan<byte> body) => new(
    body[..NonceLength].ToArray(),
    body.Slice(NonceLength, TagLength).ToArray(),
    body[(NonceLength + TagLength)..].ToArray());

  /// <summary>
  /// A VMK entry: GUID, modification time, protection, then nested property entries (stretch-key
  /// salt, the wrapped VMK, a clear key, a name). Entries a supported protector should not carry
  /// make the whole volume unreadable, as in cryptsetup; unsupported protectors are only listed.
  /// </summary>
  private static BitLockerKeyProtector ParseVmk(ReadOnlySpan<byte> entry) {
    if (entry.Length < EntryHeaderLength + VmkHeaderLength)
      throw new InvalidDataException("BitLocker: a VMK entry is too short.");
    var header = entry.Slice(EntryHeaderLength, VmkHeaderLength);
    var id = new Guid(header[..16]);
    var raw = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
    var protection = ProtectionOf(raw);
    var supported = protection is BitLockerProtection.Password or BitLockerProtection.RecoveryPassword
      or BitLockerProtection.StartupKey or BitLockerProtection.ClearKey;

    var salt = new byte[SaltLength];
    BitLockerWrappedKey? wrapped = null;
    byte[]? clearKey = null;
    string? name = null;
    var data = entry;
    for (var start = EntryHeaderLength + VmkHeaderLength; data.Length - start >= 6;) {
      var size = BinaryPrimitives.ReadUInt16LittleEndian(data[start..]);
      if (size == 0) break;
      if (size > data.Length - start) throw new InvalidDataException("BitLocker: a VMK property runs past its entry.");
      var type = BinaryPrimitives.ReadUInt16LittleEndian(data[(start + 2)..]);
      var value = BinaryPrimitives.ReadUInt16LittleEndian(data[(start + 4)..]);
      var body = data.Slice(start + EntryHeaderLength, size - Math.Min(size, (ushort)EntryHeaderLength));
      if (type != EntryProperty && supported)
        throw new InvalidDataException($"BitLocker: unexpected entry type {type} inside a supported VMK.");
      switch (value) {
        case ValueStretchKey:
          if (data.Length - start < EntryHeaderLength + SaltLength + 4)
            throw new InvalidDataException("BitLocker: a stretch-key property is too short.");
          data.Slice(start + EntryHeaderLength + 4, SaltLength).CopyTo(salt);
          break;
        case ValueEncryptedKey:
          if (size < EntryHeaderLength + NonceLength + TagLength)
            throw new InvalidDataException("BitLocker: a wrapped VMK is too short.");
          wrapped ??= Wrapped(body);
          break;
        case ValueKey:
          if (size < EntryHeaderLength + 4) throw new InvalidDataException("BitLocker: a clear key is too short.");
          clearKey ??= body[4..].ToArray();
          break;
        case ValueRecoveryTime or ValueHint:
          break;
        case ValueString:
          var text = Utf16(body);
          if (name is null) name = text;
          else if (supported) throw new InvalidDataException($"BitLocker: unexpected string '{text}' inside a supported VMK.");
          break;
        case ValueUseKey when protection == BitLockerProtection.StartupKey:
        case ValueTpmKey when protection == BitLockerProtection.Tpm:
          break;
        default:
          if (supported) throw new InvalidDataException($"BitLocker: unexpected property value {value} inside a supported VMK.");
          break;
      }
      start += size;
    }
    return new BitLockerKeyProtector(id, protection, raw, name, salt, wrapped, clearKey);
  }

  private static string Utf16(ReadOnlySpan<byte> bytes) {
    var text = Encoding.Unicode.GetString(bytes[..(bytes.Length & ~1)]);
    var nul = text.IndexOf('\0');
    return nul >= 0 ? text[..nul] : text;
  }

  internal static void ReadAt(Stream stream, long offset, Span<byte> buffer) {
    stream.Position = offset;
    stream.ReadExactly(buffer);
  }
}
