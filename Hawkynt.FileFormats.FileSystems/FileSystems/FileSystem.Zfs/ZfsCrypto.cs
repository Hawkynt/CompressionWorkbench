using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FileSystem.Zfs;

/// <summary>
/// Key material for a natively encrypted ZFS dataset, as <c>zfs load-key</c> reads it from its
/// key location: a passphrase, 64 hexadecimal digits, or 32 raw bytes — which one the dataset's
/// <c>keyformat</c> says.
/// </summary>
public sealed class ZfsKeyMaterial {
  private ZfsKeyMaterial(byte[] bytes) => this.Bytes = bytes;

  internal byte[] Bytes { get; }

  /// <summary>Key material given as text (a passphrase, or a hex key).</summary>
  public static ZfsKeyMaterial FromText(string text) {
    ArgumentNullException.ThrowIfNull(text);
    return new(Encoding.UTF8.GetBytes(text));
  }

  /// <summary>Key material given as bytes (a raw key, or a key file's contents).</summary>
  public static ZfsKeyMaterial FromBytes(ReadOnlySpan<byte> bytes) => new(bytes.ToArray());
}

/// <summary>The key could not open the dataset's master key: the wrong passphrase or key.</summary>
internal sealed class ZfsWrongKeyException(string message) : IOException(message);

/// <summary>
/// A dataset's unwrapped keys and the block cryptography that uses them
/// (<c>docs/ZFS-ON-DISK.md</c>, "Native encryption"): AES-GCM or AES-CCM with a key derived by
/// HKDF-SHA-512 from the master key and each block's salt, HMAC-SHA-512 for authenticated
/// blocks and the object set MACs, SHA-512 over the child MACs for indirect blocks.
/// </summary>
internal sealed class ZfsDatasetKey {
  public const int MacLength = 16, IvLength = 12, SaltLength = 8, HmacKeyLength = 64, WrappingKeyLength = 32;
  private const ulong KeyFormatRaw = 1, KeyFormatHex = 2, KeyFormatPassphrase = 3;

  private readonly Dictionary<ulong, byte[]> _blockKeys = [];

  private ZfsDatasetKey(ulong crypt, ulong version, ulong guid, byte[] master, byte[] hmac) {
    this.Crypt = crypt;
    this.Version = version;
    this.Guid = guid;
    this.MasterKey = master;
    this.HmacKey = hmac;
  }

  /// <summary>The suite: 3-5 AES-128/192/256-CCM, 6-8 AES-128/192/256-GCM.</summary>
  public ulong Crypt { get; }
  public ulong Version { get; }
  public ulong Guid { get; }
  public byte[] MasterKey { get; }
  public byte[] HmacKey { get; }

  public static int KeyLength(ulong crypt) => crypt switch {
    3 or 6 => 16,
    4 or 7 => 24,
    5 or 8 => 32,
    _ => throw new NotSupportedException($"ZFS: encryption suite {crypt} is not known."),
  };

  private static bool IsCcm(ulong crypt) => crypt is 3 or 4 or 5;

  /// <summary>The wrapping key a key format turns key material into (PBKDF2-HMAC-SHA-1 for a passphrase).</summary>
  public static byte[] WrappingKey(ulong keyFormat, ZfsKeyMaterial material, ulong pbkdf2Salt, ulong pbkdf2Iterations) {
    switch (keyFormat) {
      case KeyFormatRaw:
        if (material.Bytes.Length != WrappingKeyLength)
          throw new ZfsWrongKeyException($"ZFS: a raw key is {WrappingKeyLength} bytes, not {material.Bytes.Length}.");
        return material.Bytes.ToArray();
      case KeyFormatHex: {
        var text = Encoding.ASCII.GetString(material.Bytes).Trim();
        try {
          var key = Convert.FromHexString(text);
          if (key.Length == WrappingKeyLength) return key;
        } catch (FormatException) {
        }
        throw new ZfsWrongKeyException($"ZFS: a hex key is {2 * WrappingKeyLength} hexadecimal digits.");
      }
      case KeyFormatPassphrase: {
        Span<byte> salt = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(salt, pbkdf2Salt);
        return Rfc2898DeriveBytes.Pbkdf2(material.Bytes, salt, checked((int)pbkdf2Iterations), HashAlgorithmName.SHA1, WrappingKeyLength);
      }
      default:
        throw new NotSupportedException($"ZFS: key format {keyFormat} is not known.");
    }
  }

  /// <summary>Opens the master and HMAC keys the wrapping key sealed (with the key's guid, suite and version as associated data).</summary>
  public static ZfsDatasetKey Unwrap(byte[] wrappingKey, ulong crypt, ulong version, ulong guid,
      byte[] wrappedMaster, byte[] wrappedHmac, byte[] iv, byte[] mac) {
    var keyLength = KeyLength(crypt);
    // the master key is stored in a 32-byte field whatever the suite's key length
    if (wrappedMaster.Length < keyLength || wrappedHmac.Length != HmacKeyLength || iv.Length != IvLength || mac.Length != MacLength)
      throw new InvalidDataException("ZFS: a DSL crypto key has fields of the wrong length.");
    wrappedMaster = wrappedMaster[..keyLength];
    var aad = new byte[version == 0 ? 8 : 24];
    BinaryPrimitives.WriteUInt64LittleEndian(aad, guid);
    if (version != 0) {
      BinaryPrimitives.WriteUInt64LittleEndian(aad.AsSpan(8), crypt);
      BinaryPrimitives.WriteUInt64LittleEndian(aad.AsSpan(16), version);
    }
    var plain = new byte[keyLength + HmacKeyLength];
    try {
      Decrypt(crypt, wrappingKey, iv, [.. wrappedMaster, .. wrappedHmac], mac, plain, aad);
    } catch (AuthenticationTagMismatchException) {
      throw new ZfsWrongKeyException("ZFS: the key does not open this dataset's master key (wrong passphrase or key).");
    }
    return new(crypt, version, guid, plain[..keyLength], plain[keyLength..]);
  }

  /// <summary>AES-GCM or AES-CCM decryption with a 16-byte tag; throws when the tag does not hold.</summary>
  public static void Decrypt(ulong crypt, byte[] key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> tag,
      Span<byte> plain, ReadOnlySpan<byte> aad) {
    if (IsCcm(crypt)) {
      using var ccm = new AesCcm(key);
      ccm.Decrypt(iv, cipher, tag, plain, aad);
    } else {
      using var gcm = new AesGcm(key, MacLength);
      gcm.Decrypt(iv, cipher, tag, plain, aad);
    }
  }

  /// <summary>AES-GCM or AES-CCM encryption with a 16-byte tag.</summary>
  public static void Encrypt(ulong crypt, byte[] key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> plain, Span<byte> cipher,
      Span<byte> tag, ReadOnlySpan<byte> aad) {
    if (IsCcm(crypt)) {
      using var ccm = new AesCcm(key);
      ccm.Encrypt(iv, plain, cipher, tag, aad);
    } else {
      using var gcm = new AesGcm(key, MacLength);
      gcm.Encrypt(iv, plain, cipher, tag, aad);
    }
  }

  /// <summary>The block key for a salt: HKDF-SHA-512 of the master key, no HKDF salt, the block salt as info.</summary>
  public byte[] BlockKey(ReadOnlySpan<byte> salt) {
    var id = BinaryPrimitives.ReadUInt64LittleEndian(salt);
    if (this._blockKeys.TryGetValue(id, out var key)) return key;
    key = HKDF.DeriveKey(HashAlgorithmName.SHA512, this.MasterKey, this.MasterKey.Length, [], salt.ToArray());
    if (this._blockKeys.Count > 64) this._blockKeys.Clear();
    return this._blockKeys[id] = key;
  }

  /// <summary>HMAC-SHA-512 with the dataset's HMAC key.</summary>
  public byte[] Hmac(ReadOnlySpan<byte> data) => HMACSHA512.HashData(this.HmacKey, data);
}

/// <summary>The block-level rules of native encryption.</summary>
internal static class ZfsCryptoBlocks {
  /// <summary>Legacy object types whose blocks are encrypted (the rest of a protected dataset is only authenticated).</summary>
  private static readonly HashSet<byte> EncryptedTypes = [9, 10, 18, 19, 20, 22, 23, 25, 26, 33, 34, 35, 38, 39, 44, 45, 46, 47, 49];

  public const byte TypeDnode = 10, TypeObjset = 11;
  private const ulong DnodeSpillFlag = 4;

  /// <summary>DMU_OT_IS_ENCRYPTED: new-style types carry the bit 0x20, legacy ones are listed.</summary>
  public static bool IsEncryptedType(byte type) => (type & 0x80) != 0 ? (type & 0x20) != 0 : EncryptedTypes.Contains(type);

  /// <summary>Salt (DVA[2] word 0) and IV (DVA[2] word 1, then the upper half of the fill count) of an encrypted block.</summary>
  public static (byte[] Salt, byte[] Iv) Parameters(ReadOnlySpan<byte> bp) {
    var salt = bp.Slice(0x20, 8).ToArray();
    var iv = new byte[ZfsDatasetKey.IvLength];
    bp.Slice(0x28, 8).CopyTo(iv);
    bp.Slice(0x58 + 4, 4).CopyTo(iv.AsSpan(8));
    return (salt, iv);
  }

  /// <summary>A block's MAC: checksum words 2 and 3 (none for an object set block).</summary>
  public static byte[] Mac(ReadOnlySpan<byte> bp) {
    var type = bp[0x36];
    return type == TypeObjset ? new byte[16] : bp.Slice(0x70, 16).ToArray();
  }

  /// <summary>
  /// What a MAC covers of a block pointer (<c>blkptr_auth_buf_t</c>): its properties with the
  /// non-portable fields cleared (all of them for a hole; byte order, compression and physical
  /// size above level 0; dedup and checksum always), its MAC, and (version 1) 8 zero bytes.
  /// </summary>
  public static void AuthBuffer(ReadOnlySpan<byte> bp, ulong version, Span<byte> destination, out int length) {
    var prop = BinaryPrimitives.ReadUInt64LittleEndian(bp[0x30..]);
    var hole = (prop >> 39 & 1) == 0 && BinaryPrimitives.ReadUInt64LittleEndian(bp) == 0 && BinaryPrimitives.ReadUInt64LittleEndian(bp[8..]) == 0;
    if (version == 0) {
      prop &= ~(1UL << 62);                                         // dedup
      prop &= ~(0xFFUL << 40);                                      // checksum
      prop &= ~(0xFFFFUL << 16);                                    // psize = 512
    } else if (hole) {
      prop = 0;
    } else {
      if ((prop >> 56 & 0x1F) != 0) {
        prop &= ~(1UL << 63);                                       // byte order
        prop &= ~(0x7FUL << 32);                                    // compression
        prop &= ~(0xFFFFUL << 16);                                  // psize = 512
      }
      prop &= ~(1UL << 62);
      prop &= ~(0xFFUL << 40);
    }
    BinaryPrimitives.WriteUInt64LittleEndian(destination, prop);
    Mac(bp).CopyTo(destination[8..]);
    length = version == 0 ? 24 : 32;
    if (version != 0) destination.Slice(24, 8).Clear();
  }

  /// <summary>The checksum-of-MACs an indirect block carries in checksum words 2-3: SHA-512 over each child's auth buffer.</summary>
  public static byte[] IndirectMac(ReadOnlySpan<byte> block, ulong version) {
    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
    Span<byte> bab = stackalloc byte[32];
    for (var at = 0; at + 128 <= block.Length; at += 128) {
      AuthBuffer(block.Slice(at, 128), version, bab, out var length);
      sha.AppendData(bab[..length]);
    }
    return sha.GetHashAndReset()[..16];
  }

  /// <summary>
  /// Decrypts a block of dnodes: only the bonus buffers of dnodes whose bonus type is encrypted
  /// are ciphertext, one stream in dnode order; the rest — each dnode's first 64 bytes (flags
  /// masked to the spill bit, used bytes zeroed), its block pointers' auth buffers and the
  /// plaintext bonus buffers — is the associated data.
  /// </summary>
  public static byte[] DecryptDnodeBlock(ZfsDatasetKey key, byte[] block, ReadOnlySpan<byte> bp) {
    var (salt, iv) = Parameters(bp);
    var mac = Mac(bp);
    var plain = block.ToArray();
    var aad = new MemoryStream();
    var spans = new List<(int Offset, int Length)>();
    Span<byte> bab = stackalloc byte[32];
    var slots = block.Length / ZfsConstants.DnodeSlotSize;
    for (var i = 0; i < slots;) {
      var at = i * ZfsConstants.DnodeSlotSize;
      var type = block[at];
      var nblkptr = block[at + 3];
      var bonusType = block[at + 4];
      var flags = block[at + 7];
      var extra = block[at + 0x0C];
      var bonusLength = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(at + 10));
      var end = at + (extra + 1) * ZfsConstants.DnodeSlotSize;
      if (end > block.Length) throw new InvalidDataException("ZFS: a dnode runs past its block.");
      var core = block.AsSpan(at, 64).ToArray();
      core[7] &= (byte)DnodeSpillFlag;
      core.AsSpan(0x18, 8).Clear();
      aad.Write(core);
      for (var b = 0; b < nblkptr; ++b) {
        AuthBuffer(block.AsSpan(at + 64 + b * 128, 128), key.Version, bab, out var length);
        aad.Write(bab[..length]);
      }
      var spill = (flags & DnodeSpillFlag) != 0;
      if (spill) {
        AuthBuffer(block.AsSpan(end - 128, 128), key.Version, bab, out var length);
        aad.Write(bab[..length]);
      }
      var bonusAt = at + 64 + nblkptr * 128;
      var bonusMax = (spill ? end - 128 : end) - bonusAt;
      if (bonusMax < 0) throw new InvalidDataException("ZFS: a dnode's block pointers overrun it.");
      if (type != 0 && IsEncryptedType(bonusType) && bonusLength != 0)
        spans.Add((bonusAt, bonusMax));
      else
        aad.Write(block, bonusAt, bonusMax);
      i += extra + 1;
    }
    // (with no encrypted bonus the cipher text is empty and the MAC covers the associated data alone)
    var cipher = new byte[spans.Sum(s => s.Length)];
    var taken = 0;
    foreach (var (offset, length) in spans) {
      block.AsSpan(offset, length).CopyTo(cipher.AsSpan(taken));
      taken += length;
    }
    var decrypted = new byte[cipher.Length];
    ZfsDatasetKey.Decrypt(key.Crypt, key.BlockKey(salt), iv, cipher, mac, decrypted, aad.ToArray());
    taken = 0;
    foreach (var (offset, length) in spans) {
      decrypted.AsSpan(taken, length).CopyTo(plain.AsSpan(offset));
      taken += length;
    }
    return plain;
  }

  /// <summary>
  /// The object set MACs (<c>os_portable_mac</c>, <c>os_local_mac</c>): HMAC-SHA-512 over the type,
  /// the portable flags and the meta-dnode; and over the other flags and the accounting dnodes
  /// (zeros when user accounting is incomplete or absent). Both truncated to 32 bytes. The type
  /// is hashed big-endian, the flags little-endian, as OpenZFS does on a little-endian host.
  /// </summary>
  public static (byte[] Portable, byte[] Local) ObjsetMacs(ZfsDatasetKey key, ReadOnlySpan<byte> block) {
    using var portable = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA512, key.HmacKey);
    // os_type goes in most significant byte first (OpenZFS swaps it on little-endian hosts)
    Span<byte> type = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(type, BinaryPrimitives.ReadUInt64LittleEndian(block[0x2C0..]));
    portable.AppendData(type);
    portable.AppendData(new byte[8]);                              // portable flags: none
    DnodeMacInput(portable, block[..512], key.Version);
    var portableMac = portable.GetHashAndReset()[..32];

    var flags = BinaryPrimitives.ReadUInt64LittleEndian(block[0x2C8..]);
    var local = new byte[32];
    var hasUser = block.Length >= 2048 && (block[1024] != 0 || block[1536] != 0);
    var hasProject = block.Length >= 4096 && block[2048] != 0;
    if ((flags & 1) == 0 || block.Length <= 1024 || !hasUser) return (portableMac, local);
    using var mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA512, key.HmacKey);
    mac.AppendData(block.Slice(0x2C8, 8));                         // every flag is non-portable
    if (block[1024] != 0) DnodeMacInput(mac, block.Slice(1024, 512), key.Version);
    if (block[1536] != 0) DnodeMacInput(mac, block.Slice(1536, 512), key.Version);
    if (hasProject) DnodeMacInput(mac, block.Slice(2048, 512), key.Version);
    return (portableMac, mac.GetHashAndReset()[..32]);
  }

  private static void DnodeMacInput(IncrementalHash hash, ReadOnlySpan<byte> dnode, ulong version) {
    var core = dnode[..64].ToArray();
    core[7] &= (byte)DnodeSpillFlag;
    core.AsSpan(0x18, 8).Clear();
    hash.AppendData(core);
    Span<byte> bab = stackalloc byte[32];
    var nblkptr = dnode[3];
    for (var b = 0; b < nblkptr; ++b) {
      AuthBuffer(dnode.Slice(64 + b * 128, 128), version, bab, out var length);
      hash.AppendData(bab[..length]);
    }
    if ((dnode[7] & DnodeSpillFlag) != 0) {
      AuthBuffer(dnode.Slice(dnode.Length - 128, 128), version, bab, out var length);
      hash.AppendData(bab[..length]);
    }
  }
}
