#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.BitLocker;

/// <summary>How to create a BitLocker volume.</summary>
public sealed record BitLockerCreateOptions {
  /// <summary>User password protector; <see langword="null"/> for a volume opened by its recovery password only.</summary>
  public string? Password { get; init; }

  /// <summary>Recovery password protector (48 digits); <see langword="null"/> to generate one.</summary>
  public string? RecoveryPassword { get; init; }

  /// <summary>Encryption method code: 0x8004 AES-XTS-128 (default), 0x8005 AES-XTS-256, 0x8002/0x8003 AES-CBC-128/256, 0x8000/0x8001 AES-CBC-128/256 + Elephant.</summary>
  public ushort EncryptionMethod { get; init; } = 0x8004;

  public string? Description { get; init; }
  public Guid? VolumeGuid { get; init; }
  public DateTime? CreationTime { get; init; }
}

/// <summary>What was created: the identifiers and the recovery password to keep.</summary>
public sealed record BitLockerCreateResult(
  Guid VolumeGuid,
  string RecoveryPassword,
  Guid? PasswordProtectorId,
  Guid RecoveryProtectorId,
  long VolumeSize,
  IReadOnlyList<long> MetadataOffsets,
  long VolumeHeaderOffset,
  long VolumeHeaderSize);

/// <summary>
/// Writes a BitLocker volume around a plaintext volume image so that cryptsetup's decrypted view
/// is that image followed by zeros. The image's first 8 KiB (its boot region) move, encrypted, to
/// a volume-header backup; the -FVE-FS- boot sector takes their place. Everything BitLocker adds
/// lives past the end of the image — three 64 KiB FVE metadata copies with the header backup
/// after the first, then 64 KiB of encrypted zeros (cryptsetup's layout must end in a crypt
/// segment) — so no sector the inner filesystem can use is a metadata sector that would read as
/// zeros and swallow writes.
/// </summary>
/// <remarks>Specification: <c>docs/BITLOCKER-ON-DISK.md</c>.</remarks>
public static class BitLockerWriter {
  public const int Sector = 512;
  public const int VolumeHeaderBytes = 8192;
  private const int Area = BitLockerVolume.MetadataAreaSize;
  private const ushort PasswordStretchMethod = 0x1001, RecoveryStretchMethod = 0x1000;

  /// <summary>Encrypts <paramref name="payload"/> (whole 512-byte sectors, more than 8 KiB) into a new volume on <paramref name="output"/>.</summary>
  public static BitLockerCreateResult Write(Stream output, ReadOnlySpan<byte> payload, BitLockerCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(options);
    if (payload.Length % Sector != 0 || payload.Length <= VolumeHeaderBytes)
      throw new ArgumentException("A BitLocker volume image must be whole 512-byte sectors and larger than 8 KiB.", nameof(payload));
    var method = BitLockerVolume.Method(options.EncryptionMethod)
      ?? throw new ArgumentException($"Unknown BitLocker encryption method 0x{options.EncryptionMethod:X4}.", nameof(options));
    var recovery = options.RecoveryPassword ?? BitLockerKeys.NewRecoveryPassword();
    var recoveryKey = BitLockerKeys.ParseRecoveryPassword(recovery)
      ?? throw new ArgumentException("The recovery password must be 8 groups of 6 digits, each divisible by 11.", nameof(options));

    // Layout past the image, 64 KiB aligned: metadata 0, header backup, metadata 1, metadata 2, crypt tail.
    long imageEnd = payload.Length;
    var meta0 = AlignUp(imageEnd, Area);
    var headerOffset = meta0 + Area;
    var meta1 = headerOffset + VolumeHeaderBytes;
    var meta2 = meta1 + Area;
    var tail = meta2 + Area;
    var volumeSize = tail + Area;
    long[] offsets = [meta0, meta1, meta2];

    var volumeGuid = options.VolumeGuid ?? Guid.NewGuid();
    var created = (options.CreationTime ?? DateTime.UtcNow).ToUniversalTime().ToFileTimeUtc();
    var nonces = new NonceSource(created);

    var storedFvek = NewFvek(method.Mode, method.KeyBits);
    var fvek = PackedFvek(storedFvek, method.Mode, method.KeyBits);
    var vmk = RandomNumberGenerator.GetBytes(32);
    try {
      // Entries: description, password VMK, recovery VMK, FVEK, volume header.
      var entries = new List<byte[]>();
      var description = options.Description ?? $"CompressionWorkbench {DateTime.FromFileTimeUtc(created):yyyy-MM-dd}";
      entries.Add(Entry(BitLockerVolume.EntryDescription, BitLockerVolume.ValueString, 1, Encoding.Unicode.GetBytes(description + "\0")));
      Guid? passwordId = null;
      if (options.Password is not null) {
        passwordId = Guid.NewGuid();
        var salt = RandomNumberGenerator.GetBytes(BitLockerVolume.SaltLength);
        var key = BitLockerKeys.StretchPassword(options.Password, salt);
        entries.Add(VmkEntry(passwordId.Value, 0x2000, PasswordStretchMethod, salt, Seal(vmk, BitLockerKeys.VmkDatumMethod, key, nonces), created));
        CryptographicOperations.ZeroMemory(key);
      }
      var recoveryId = Guid.NewGuid();
      {
        var salt = RandomNumberGenerator.GetBytes(BitLockerVolume.SaltLength);
        var key = BitLockerKeys.StretchRecoveryKey(recoveryKey, salt);
        entries.Add(VmkEntry(recoveryId, 0x0800, RecoveryStretchMethod, salt, Seal(vmk, BitLockerKeys.VmkDatumMethod, key, nonces), created));
        CryptographicOperations.ZeroMemory(key);
      }
      entries.Add(Entry(BitLockerVolume.EntryFvek, BitLockerVolume.ValueEncryptedKey, 1, WrappedBody(Seal(storedFvek, options.EncryptionMethod, vmk, nonces))));
      var header = new byte[16];
      BinaryPrimitives.WriteInt64LittleEndian(header, headerOffset);
      BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), VolumeHeaderBytes);
      entries.Add(Entry(BitLockerVolume.EntryVolumeHeader, BitLockerVolume.ValueOffsetSize, 1, header));

      var validationNonce = nonces.Next();
      var block = MetadataBlock(entries, offsets, headerOffset, volumeSize, volumeGuid, options.EncryptionMethod, nonces.Counter, created);
      var validation = BitLockerKeys.Wrap(SHA256.HashData(block), BitLockerKeys.ValidationDatumMethod, vmk, validationNonce);
      var area = MetadataArea(block, validation);

      using var cipher = new BitLockerSectorCipher(method.Mode, fvek, Sector);
      WriteVolume(output, payload, cipher, BootSector(offsets), area, offsets, headerOffset, volumeSize);
      return new BitLockerCreateResult(volumeGuid, recovery, passwordId, recoveryId, volumeSize, offsets, headerOffset, VolumeHeaderBytes);
    } finally {
      CryptographicOperations.ZeroMemory(vmk);
      CryptographicOperations.ZeroMemory(fvek);
      CryptographicOperations.ZeroMemory(storedFvek);
      CryptographicOperations.ZeroMemory(recoveryKey);
    }
  }

  private static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

  /// <summary>
  /// The FVEK as stored in its datum: XTS and CBC keys as they are, 128-bit Elephant as
  /// CBC key ‖ 16 zero bytes ‖ diffuser key ‖ 16 zero bytes (512 bits, as the method stores it).
  /// </summary>
  private static byte[] NewFvek(BitLockerCipherMode mode, int keyBits) {
    if (mode == BitLockerCipherMode.CbcElephant && keyBits == 256) {
      var stored = new byte[64];
      RandomNumberGenerator.Fill(stored.AsSpan(0, 16));
      RandomNumberGenerator.Fill(stored.AsSpan(32, 16));
      return stored;
    }
    return RandomNumberGenerator.GetBytes(keyBits / 8);
  }

  private static byte[] PackedFvek(byte[] stored, BitLockerCipherMode mode, int keyBits) {
    if (mode != BitLockerCipherMode.CbcElephant || keyBits != 256) return stored.ToArray();
    var packed = new byte[32];
    stored.AsSpan(0, 16).CopyTo(packed);
    stored.AsSpan(32, 16).CopyTo(packed.AsSpan(16));
    return packed;
  }

  /// <summary>Nonces as Windows forms them: the creation FILETIME, then a counter the metadata header records the next value of.</summary>
  private sealed class NonceSource(long filetime) {
    public uint Counter { get; private set; } = 1;

    public byte[] Next() {
      var nonce = new byte[BitLockerVolume.NonceLength];
      BinaryPrimitives.WriteInt64LittleEndian(nonce, filetime);
      BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), this.Counter++);
      return nonce;
    }
  }

  private static BitLockerWrappedKey Seal(ReadOnlySpan<byte> payload, uint method, ReadOnlySpan<byte> key, NonceSource nonces)
    => BitLockerKeys.Wrap(payload, method, key, nonces.Next());

  private static byte[] WrappedBody(BitLockerWrappedKey wrapped)
    => [.. wrapped.Nonce, .. wrapped.Tag, .. wrapped.Ciphertext];

  private static byte[] Entry(ushort type, ushort value, ushort version, ReadOnlySpan<byte> body) {
    var entry = new byte[BitLockerVolume.EntryHeaderLength + body.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(entry, checked((ushort)entry.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), type);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4), value);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(6), version);
    body.CopyTo(entry.AsSpan(BitLockerVolume.EntryHeaderLength));
    return entry;
  }

  /// <summary>A VMK entry: GUID, modification time, protection, the stretch-key salt and the wrapped VMK.</summary>
  private static byte[] VmkEntry(Guid id, ushort protection, ushort stretchMethod, byte[] salt, BitLockerWrappedKey wrapped, long modified) {
    var stretch = new byte[4 + BitLockerVolume.SaltLength];
    BinaryPrimitives.WriteUInt32LittleEndian(stretch, stretchMethod);
    salt.CopyTo(stretch.AsSpan(4));
    var header = new byte[BitLockerVolume.VmkHeaderLength];
    id.TryWriteBytes(header);
    BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), modified);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), protection);
    byte[] body = [
      .. header,
      .. Entry(BitLockerVolume.EntryProperty, BitLockerVolume.ValueStretchKey, 1, stretch),
      .. Entry(BitLockerVolume.EntryProperty, BitLockerVolume.ValueEncryptedKey, 1, WrappedBody(wrapped)),
    ];
    return Entry(BitLockerVolume.EntryVmk, BitLockerVolume.ValueVmk, 1, body);
  }

  /// <summary>The FVE metadata block: 64-byte block header, 48-byte metadata header, entries, padded to 16 bytes.</summary>
  private static byte[] MetadataBlock(List<byte[]> entries, long[] offsets, long headerOffset, long volumeSize, Guid volumeGuid,
      ushort encryption, uint nextNonce, long created) {
    var metadataSize = BitLockerVolume.MetadataHeaderLength + entries.Sum(static e => e.Length);
    var blockSize = (int)AlignUp(BitLockerVolume.BlockHeaderLength + metadataSize, 16);
    var block = new byte[blockSize];
    var b = block.AsSpan();
    BitLockerVolume.Signature8.CopyTo(b);
    BinaryPrimitives.WriteUInt16LittleEndian(b[8..], (ushort)(blockSize >> 4));
    BinaryPrimitives.WriteUInt16LittleEndian(b[10..], 2);
    BinaryPrimitives.WriteUInt16LittleEndian(b[12..], BitLockerVolume.NormalState);
    BinaryPrimitives.WriteUInt16LittleEndian(b[14..], BitLockerVolume.NormalState);
    BinaryPrimitives.WriteInt64LittleEndian(b[16..], volumeSize);
    BinaryPrimitives.WriteUInt32LittleEndian(b[28..], VolumeHeaderBytes / Sector);
    for (var i = 0; i < 3; ++i) BinaryPrimitives.WriteInt64LittleEndian(b[(32 + 8 * i)..], offsets[i]);
    BinaryPrimitives.WriteInt64LittleEndian(b[56..], headerOffset);

    BinaryPrimitives.WriteUInt32LittleEndian(b[64..], (uint)metadataSize);
    BinaryPrimitives.WriteUInt32LittleEndian(b[68..], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(b[72..], BitLockerVolume.MetadataHeaderLength);
    BinaryPrimitives.WriteUInt32LittleEndian(b[76..], (uint)metadataSize);
    volumeGuid.TryWriteBytes(b[80..]);
    BinaryPrimitives.WriteUInt32LittleEndian(b[96..], nextNonce);
    BinaryPrimitives.WriteUInt16LittleEndian(b[100..], encryption);
    BinaryPrimitives.WriteUInt16LittleEndian(b[102..], encryption);
    BinaryPrimitives.WriteInt64LittleEndian(b[104..], created);
    var at = BitLockerVolume.BlockHeaderLength + BitLockerVolume.MetadataHeaderLength;
    foreach (var e in entries) {
      e.CopyTo(b[at..]);
      at += e.Length;
    }
    return block;
  }

  /// <summary>
  /// One 64 KiB metadata area: the block, then the validation block — its size (the rest of the
  /// area), version 2, the block's CRC-32 and the AES-CCM datum holding its SHA-256.
  /// </summary>
  private static byte[] MetadataArea(byte[] block, BitLockerWrappedKey validation) {
    var area = new byte[Area];
    block.CopyTo(area, 0);
    var v = area.AsSpan(block.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(v, (ushort)(Area - block.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(v[2..], 2);
    BinaryPrimitives.WriteUInt32LittleEndian(v[4..], Crc32.Compute(block));
    BinaryPrimitives.WriteUInt16LittleEndian(v[8..], 8 + BitLockerVolume.ValidationDataLength);
    BinaryPrimitives.WriteUInt16LittleEndian(v[10..], BitLockerVolume.EntryProperty);
    BinaryPrimitives.WriteUInt16LittleEndian(v[12..], BitLockerVolume.ValueEncryptedKey);
    BinaryPrimitives.WriteUInt16LittleEndian(v[14..], 1);
    WrappedBody(validation).CopyTo(v[16..]);
    return area;
  }

  /// <summary>
  /// The -FVE-FS- boot sector: the BIOS parameter block values Windows writes (512-byte sectors,
  /// media F8, 63 sectors × 255 heads, the FAT32 extension fields), no boot code, the normal-volume
  /// GUID and the three metadata offsets at 160, and the 55 AA signature.
  /// </summary>
  private static byte[] BootSector(long[] offsets) {
    var s = new byte[Sector];
    BitLockerVolume.BootCodeV2.CopyTo(s, 0);
    BitLockerVolume.Signature8.CopyTo(s.AsSpan(3));
    BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(11), Sector);
    s[13] = 8;
    s[21] = 0xF8;
    BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(24), 63);
    BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(26), 255);
    BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(36), 0x1FE0);
    BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(48), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(50), 6);
    s[64] = 0x80;
    s[66] = 0x29;
    "NO NAME    FAT32   "u8.CopyTo(s.AsSpan(71));
    BitLockerVolume.NormalGuid.CopyTo(s, BitLockerVolume.HeaderGuidOffset);
    for (var i = 0; i < 3; ++i)
      BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(BitLockerVolume.HeaderGuidOffset + 16 + 8 * i), offsets[i]);
    s[510] = 0x55;
    s[511] = 0xAA;
    return s;
  }

  private static void WriteVolume(Stream output, ReadOnlySpan<byte> payload, BitLockerSectorCipher cipher, byte[] boot, byte[] area,
      long[] offsets, long headerOffset, long volumeSize) {
    const int chunk = 1 << 20;
    var buffer = new byte[chunk];

    // Boot region: the -FVE-FS- sector, zeros to the end of the relocated header.
    output.Write(boot);
    output.Write(new byte[VolumeHeaderBytes - Sector]);
    long position = VolumeHeaderBytes;

    // The image after its boot region, encrypted in place; then encrypted zeros up to metadata 0.
    void Encrypted(ReadOnlySpan<byte> plain, long at) {
      for (var done = 0; done < plain.Length; done += chunk) {
        var n = Math.Min(chunk, plain.Length - done);
        plain.Slice(done, n).CopyTo(buffer);
        cipher.Encrypt(buffer.AsSpan(0, n), (at + done) / Sector);
        output.Write(buffer, 0, n);
      }
    }
    void EncryptedZeros(long from, long to) {
      for (var at = from; at < to; at += chunk) {
        var n = (int)Math.Min(chunk, to - at);
        buffer.AsSpan(0, n).Clear();
        cipher.Encrypt(buffer.AsSpan(0, n), at / Sector);
        output.Write(buffer, 0, n);
      }
    }

    Encrypted(payload[VolumeHeaderBytes..], position);
    position = payload.Length;
    EncryptedZeros(position, offsets[0]);

    output.Write(area);
    // The header backup: the image's boot region, encrypted with IVs of where it now lies.
    payload[..VolumeHeaderBytes].CopyTo(buffer);
    cipher.Encrypt(buffer.AsSpan(0, VolumeHeaderBytes), headerOffset / Sector);
    output.Write(buffer, 0, VolumeHeaderBytes);
    output.Write(area);
    output.Write(area);
    EncryptedZeros(offsets[2] + Area, volumeSize);
    Array.Clear(buffer);
    output.Flush();
  }
}
