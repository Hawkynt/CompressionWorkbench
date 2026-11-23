using System.Buffers.Binary;
using System.Security.Cryptography;
using Compression.Core.Crypto;
using FileFormat.Luks;

namespace FileSystem.Apfs;

/// <summary>
/// Software encryption of an APFS volume: the container and volume key bags, password
/// unlock of the volume encryption key (VEK), and the AES-XTS sector cipher.
/// </summary>
/// <remarks>
/// <para>Converted from libfsapfs (https://github.com/libyal/libfsapfs, revision
/// 4456366ccaf586a7ff54c8cbf6a42f38a9ac8f4b, LGPL-3.0-or-later; see
/// THIRD-PARTY-NOTICE.libfsapfs.txt): the key bag layout, the packed key objects and the
/// unlock sequence. Structure names follow Apple's File System Reference.</para>
/// <list type="number">
///   <item>The container key bag (nx_keylocker) is AES-XTS-128 encrypted with the container
///   UUID as both keys; the volume key bag, named by the container bag's
///   KB_TAG_VOLUME_UNLOCK_RECORDS entry for the volume, likewise with the volume UUID. The
///   tweak is the 512-byte sector number from the start of the container.</item>
///   <item>Each password entry of the volume bag (tag 3) holds a wrapped key encryption key
///   (KEK): PBKDF2-HMAC-SHA256 of the password over its salt and iteration count gives the
///   key that RFC 3394 unwraps it with.</item>
///   <item>The KEK unwraps the volume encryption key held in the container bag's
///   KB_TAG_VOLUME_KEY entry for the volume. The 32-byte VEK is the AES-XTS-128 key pair for
///   the volume's file-system B-tree nodes and file data.</item>
/// </list>
/// <para>A wrapped key's metadata flag 0x2 marks the 128-bit CoreStorage-compatible mode:
/// 16-byte keys, the second XTS half taken from SHA-256 over the key and the entry's
/// identifier.</para>
/// </remarks>
internal static class ApfsEncryption {
  private const ushort TagVolumeKey = 2;
  private const ushort TagVolumeUnlockRecords = 3;
  private static readonly byte[] KeyWrapIv = [0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6];

  internal readonly record struct KeyBagEntry(Guid Uuid, ushort Tag, byte[] Data);

  /// <summary>A packed key object: the wrapped key and how to unwrap it.</summary>
  internal sealed record WrappedKey(uint Flags, byte[] Identifier, byte[] Wrapped, ulong Iterations, byte[] Salt);

  /// <summary>Builds the AES-XTS cipher a pair of 16-byte keys (or one UUID used twice) gives.</summary>
  public static LuksCipher Cipher(ReadOnlySpan<byte> key32) => new("aes", "xts-plain64", key32);

  /// <summary>The cipher of a key bag: the UUID (on-disk byte order) as both XTS keys.</summary>
  public static LuksCipher UuidCipher(ReadOnlySpan<byte> uuid) {
    Span<byte> key = stackalloc byte[32];
    uuid.CopyTo(key);
    uuid.CopyTo(key[16..]);
    return Cipher(key);
  }

  /// <summary>
  /// Decrypts the key bag <paramref name="raw" /> read from <paramref name="firstBlock" />
  /// and returns its entries. <paramref name="expectedType" /> is "keys" (container) or
  /// "recs" (volume).
  /// </summary>
  public static List<KeyBagEntry> ReadKeyBag(byte[] raw, ulong firstBlock, uint blockSize, ReadOnlySpan<byte> uuid, uint expectedType) {
    using (var cipher = UuidCipher(uuid))
      cipher.Decrypt(raw, firstBlock * (blockSize / 512));
    if ((BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(24)) & 0xFFFF) != (expectedType & 0xFFFF)
        && BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(24)) != expectedType)
      throw new InvalidDataException("APFS: key bag did not decrypt to a key bag object (wrong UUID or damaged).");
    // kb_locker_t at 32: version u16, nkeys u16, nbytes u32, padding 8.
    var version = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(32));
    if (version != 2)
      throw new NotSupportedException($"APFS: key bag version {version} is not supported.");
    var count = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(34));
    var entries = new List<KeyBagEntry>(count);
    var at = 48;
    for (var i = 0; i < count; i++) {
      if (at + 24 > raw.Length) throw new InvalidDataException("APFS: key bag entry runs past the key bag.");
      var uuid16 = raw.AsSpan(at, 16);
      var tag = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at + 16));
      var len = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at + 18));
      if (at + 24 + len > raw.Length) throw new InvalidDataException("APFS: key bag entry data runs past the key bag.");
      entries.Add(new KeyBagEntry(new Guid(uuid16, bigEndian: true), tag, raw.AsSpan(at + 24, len).ToArray()));
      at += (24 + len + 15) & ~15;
    }
    return entries;
  }

  /// <summary>The volume key bag's extent (paddr, block count) from the container bag.</summary>
  public static (ulong Block, ulong Count)? VolumeKeyBagExtent(List<KeyBagEntry> container, Guid volume) {
    foreach (var e in container)
      if (e.Uuid == volume && e.Tag == TagVolumeUnlockRecords && e.Data.Length >= 16)
        return (BinaryPrimitives.ReadUInt64LittleEndian(e.Data), BinaryPrimitives.ReadUInt64LittleEndian(e.Data.AsSpan(8)));
    return null;
  }

  /// <summary>
  /// Unlocks the volume encryption key with <paramref name="password" />; null when no
  /// password entry of the volume key bag accepts it.
  /// </summary>
  public static byte[]? UnlockVolumeKey(List<KeyBagEntry> container, List<KeyBagEntry> volume, Guid volumeUuid, string password) {
    WrappedKey? vek = null;
    foreach (var e in container)
      if (e.Uuid == volumeUuid && e.Tag == TagVolumeKey)
        vek = ParseWrappedKey(e.Data);
    if (vek is null)
      throw new InvalidDataException("APFS: the container key bag holds no volume key for this volume.");
    foreach (var e in volume) {
      if (e.Tag != TagVolumeUnlockRecords) continue;
      WrappedKey kek;
      try { kek = ParseWrappedKey(e.Data); } catch (InvalidDataException) { continue; }
      if (kek.Iterations == 0 || kek.Salt.Length == 0) continue;
      var kekKey = UnwrapWithPassword(kek, password);
      if (kekKey is null) continue;
      var result = UnwrapWithKey(vek, kekKey);
      if (result is not null) return result;
    }
    return null;
  }

  /// <summary>
  /// Packed object (tag 0x30) holding a wrapped-key object (tag 0xA3): 0x81 identifier,
  /// 0x82 metadata (flags first), 0x83 wrapped key (40 bytes), 0x84 iterations
  /// (big-endian), 0x85 salt.
  /// </summary>
  public static WrappedKey ParseWrappedKey(ReadOnlySpan<byte> data) {
    var at = 0;
    var (tag, len) = ReadPacked(data, ref at);
    if (tag != 0x30) throw new InvalidDataException("APFS: key bag entry is not a packed key object.");
    var end = at + len;
    ReadOnlySpan<byte> inner = default;
    while (at + 2 <= end) {
      if (data[at] == 0 && data[at + 1] == 0) break;
      var (t, l) = ReadPacked(data, ref at);
      if (at + l > end) throw new InvalidDataException("APFS: packed key value runs past its object.");
      if (t == 0xA3) inner = data.Slice(at, l);
      at += l;
    }
    if (inner.IsEmpty) throw new InvalidDataException("APFS: packed key object holds no wrapped key.");

    uint flags = 0;
    byte[] identifier = [], wrapped = [], salt = [];
    ulong iterations = 0;
    var metadataSize = 0;
    var p = 0;
    while (p + 2 <= inner.Length) {
      if (inner[p] == 0 && inner[p + 1] == 0) break;
      var (t, l) = ReadPacked(inner, ref p);
      if (p + l > inner.Length) throw new InvalidDataException("APFS: wrapped key value runs past its object.");
      var v = inner.Slice(p, l);
      switch (t) {
        case 0x81 when l == 16: identifier = v.ToArray(); break;
        case 0x82 when l >= 8: flags = BinaryPrimitives.ReadUInt32LittleEndian(v); metadataSize = l; break;
        case 0x83: wrapped = v.ToArray(); break;
        case 0x84 when l <= 8:
          foreach (var b in v) iterations = (iterations << 8) | b;
          break;
        case 0x85 when l == 16: salt = v.ToArray(); break;
      }
      p += l;
    }
    // In the 22-byte metadata variant the leading word is not the cipher flag; its
    // 40-byte wrapped key is AES-256.
    if (metadataSize == 22) flags = 0;
    if (wrapped.Length is not (24 or 40))
      throw new InvalidDataException("APFS: wrapped key has an unexpected length.");
    return new WrappedKey(flags, identifier, wrapped, iterations, salt);
  }

  private static (int Tag, int Length) ReadPacked(ReadOnlySpan<byte> data, ref int at) {
    if (at + 2 > data.Length) throw new InvalidDataException("APFS: packed value header is truncated.");
    var tag = data[at];
    var size = data[at + 1];
    at += 2;
    if ((size & 0x80) == 0) return (tag, size);
    var n = size & 0x7F;
    if (n is 0 or > 4 || at + n > data.Length) throw new InvalidDataException("APFS: packed value size is malformed.");
    var length = 0;
    for (var i = 0; i < n; i++) length = (length << 8) | data[at + i];
    at += n;
    return (tag, length);
  }

  private static (int KeySize, int WrappedSize)? Sizes(uint flags) => flags switch {
    0 or 16 => (32, 40),
    2 => (16, 24),
    _ => null,
  };

  /// <summary>PBKDF2-HMAC-SHA256(password, salt, iterations), then RFC 3394 unwrap; null on a wrong password.</summary>
  public static byte[]? UnwrapWithPassword(WrappedKey kek, string password) {
    if (Sizes(kek.Flags) is not { } sizes || kek.Iterations > int.MaxValue) return null;
    var key = Rfc2898DeriveBytes.Pbkdf2(password, kek.Salt, (int)kek.Iterations, HashAlgorithmName.SHA256, sizes.KeySize);
    return Unwrap(key, kek, sizes.WrappedSize, sizes.KeySize, deriveSecondHalf: false);
  }

  /// <summary>RFC 3394 unwrap of <paramref name="wrapped" /> with an unlocked key; null when the integrity check fails.</summary>
  public static byte[]? UnwrapWithKey(WrappedKey wrapped, byte[] key) {
    if (Sizes(wrapped.Flags) is not { } sizes) return null;
    var k = key.AsSpan(0, Math.Min(key.Length, sizes.KeySize)).ToArray();
    if (k.Length != sizes.KeySize) return null;
    return Unwrap(k, wrapped, sizes.WrappedSize, sizes.KeySize, deriveSecondHalf: true);
  }

  private static byte[]? Unwrap(byte[] key, WrappedKey wrapped, int wrappedSize, int keySize, bool deriveSecondHalf) {
    if (wrapped.Wrapped.Length < wrappedSize) return null;
    var plain = AesKeyUnwrap(key, wrapped.Wrapped.AsSpan(0, wrappedSize));
    if (plain is null) return null;
    var unlocked = new byte[32];
    plain.AsSpan(0, keySize).CopyTo(unlocked);
    if (deriveSecondHalf && wrapped.Flags == 2) {
      // 128-bit mode: the second XTS key is SHA-256(key || identifier)[0..16].
      Span<byte> material = stackalloc byte[32];
      plain.AsSpan(0, 16).CopyTo(material);
      wrapped.Identifier.AsSpan(0, Math.Min(16, wrapped.Identifier.Length)).CopyTo(material[16..]);
      SHA256.HashData(material)[..16].CopyTo(unlocked, 16);
    }
    return unlocked;
  }

  /// <summary>RFC 3394 AES key unwrap; null when the integrity value is not A6A6A6A6A6A6A6A6.</summary>
  internal static byte[]? AesKeyUnwrap(byte[] kek, ReadOnlySpan<byte> wrapped) {
    var n = wrapped.Length / 8 - 1;
    if (n < 2 || wrapped.Length % 8 != 0) return null;
    Span<byte> a = stackalloc byte[8];
    wrapped[..8].CopyTo(a);
    var r = new byte[n * 8];
    wrapped[8..].CopyTo(r);
    using var aes = ManagedAes.Create();
    aes.Key = kek;
    Span<byte> block = stackalloc byte[16];
    for (var j = 5; j >= 0; j--) {
      for (var i = n; i >= 1; i--) {
        var t = (ulong)(n * j + i);
        var av = BinaryPrimitives.ReadUInt64BigEndian(a) ^ t;
        BinaryPrimitives.WriteUInt64BigEndian(block, av);
        r.AsSpan((i - 1) * 8, 8).CopyTo(block[8..]);
        aes.DecryptEcb(block, block, PaddingMode.None);
        block[..8].CopyTo(a);
        block[8..].CopyTo(r.AsSpan((i - 1) * 8, 8));
      }
    }
    return a.SequenceEqual(KeyWrapIv) ? r : null;
  }
}
