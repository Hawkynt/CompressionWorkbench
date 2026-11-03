using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Mpq;

/// <summary>
/// MPQ crypto primitives shared by <see cref="MpqReader"/> and <see cref="MpqWriter"/>: the
/// crypt table, the MPQ string hash (table slots, name checks and encryption keys), block
/// encryption, and the file-key derivation and recovery used for encrypted files.
/// </summary>
/// <remarks>
/// The hash works on the name's bytes and upper-cases ASCII letters only, with '/' hashed as
/// '\', exactly as Storm and StormLib do (StormLib <c>SBaseCommon.cpp</c>, MIT). Names are taken
/// as UTF-8 when they come from a .NET string.
/// </remarks>
internal static class MpqCrypto {
  /// <summary>Hash type for the table-position hash (for filename → slot).</summary>
  internal const uint HashTypeOffset = 0x000;
  /// <summary>Hash type for the per-entry "hash A" verification field.</summary>
  internal const uint HashTypeNameA = 0x100;
  /// <summary>Hash type for the per-entry "hash B" verification field.</summary>
  internal const uint HashTypeNameB = 0x200;
  /// <summary>Hash type used when generating an encryption key from a string (e.g. "(hash table)").</summary>
  internal const uint HashTypeFileKey = 0x300;

  /// <summary>Key of the hash table, <c>HashString("(hash table)", HashTypeFileKey)</c>.</summary>
  internal const uint HashTableKey = 0xC3AF3770;
  /// <summary>Key of the block table, <c>HashString("(block table)", HashTypeFileKey)</c>.</summary>
  internal const uint BlockTableKey = 0xEC83B3A3;

  /// <summary>File flag: the file key is adjusted by its offset and size.</summary>
  internal const uint FlagKeyV2 = 0x00020000;

  internal static readonly uint[] CryptTable = BuildCryptTable();

  internal static uint HashString(string str, uint hashType) => HashString(Encoding.UTF8.GetBytes(str), hashType);

  internal static uint HashString(ReadOnlySpan<byte> name, uint hashType) {
    uint seed1 = 0x7FED7FED;
    uint seed2 = 0xEEEEEEEE;
    foreach (var b in name) {
      uint ch = b switch {
        >= (byte)'a' and <= (byte)'z' => (uint)(b - 0x20),
        (byte)'/' => '\\',
        _ => b,
      };
      seed1 = CryptTable[hashType + ch] ^ (seed1 + seed2);
      seed2 = ch + seed1 + seed2 + (seed2 << 5) + 3;
    }
    return seed1;
  }

  /// <summary>The encryption key of a file: the hash of its plain name, adjusted for <see cref="FlagKeyV2"/>.</summary>
  internal static uint FileKey(ReadOnlySpan<byte> name, uint blockOffset, uint fileSize, uint flags) {
    var plain = name;
    var slash = plain.LastIndexOfAny((byte)'\\', (byte)'/');
    if (slash >= 0)
      plain = plain[(slash + 1)..];
    var key = HashString(plain, HashTypeFileKey);
    if ((flags & FlagKeyV2) != 0)
      key = (key + blockOffset) ^ fileSize;
    return key;
  }

  /// <summary>
  /// Recovers the key of an encrypted sector-offset table from what its first two entries must
  /// be: the table's own length (allowing up to three padding bytes) and a second offset at most
  /// one sector further. Returns the file key (the table key plus one), or 0 when none fits.
  /// </summary>
  internal static uint DetectKeyBySectorTable(ReadOnlySpan<byte> encrypted, uint tableLength, uint sectorSize) {
    if (encrypted.Length < 8)
      return 0;
    var e0 = BinaryPrimitives.ReadUInt32LittleEndian(encrypted);
    var e1 = BinaryPrimitives.ReadUInt32LittleEndian(encrypted[4..]);
    for (var decrypted0 = tableLength; decrypted0 < tableLength + 4; ++decrypted0) {
      var key = DetectKey(e0, e1, decrypted0, v => v <= sectorSize + decrypted0);
      if (key != 0)
        return key + 1;
    }
    return 0;
  }

  /// <summary>Recovers a file key from the known first two plaintext words of its first sector.</summary>
  internal static uint DetectKeyByContent(ReadOnlySpan<byte> encrypted, uint fileSize) {
    if (encrypted.Length < 8)
      return 0;
    var e0 = BinaryPrimitives.ReadUInt32LittleEndian(encrypted);
    var e1 = BinaryPrimitives.ReadUInt32LittleEndian(encrypted[4..]);
    // RIFF/WAVE, an MZ executable, an XML document (StormLib's three guesses).
    foreach (var (first, second) in new[] { (0x46464952u, fileSize - 8), (0x00905A4Du, 3u), (0x6D783F3Cu, 0x6576206Cu) }) {
      var key = DetectKey(e0, e1, first, v => v == second);
      if (key != 0)
        return key;
    }
    return 0;
  }

  private static uint DetectKey(uint encrypted0, uint encrypted1, uint decrypted0, Func<uint, bool> acceptSecond) {
    var key1PlusKey2 = (encrypted0 ^ decrypted0) - 0xEEEEEEEE;
    for (var i = 0u; i < 0x100; ++i) {
      var key1 = key1PlusKey2 - CryptTable[0x400 + i];
      var key2 = 0xEEEEEEEE + CryptTable[0x400 + (key1 & 0xFF)];
      if ((encrypted0 ^ (key1 + key2)) != decrypted0)
        continue;
      var saved = key1;
      key1 = ((~key1 << 0x15) + 0x11111111) | (key1 >> 0x0B);
      key2 = decrypted0 + key2 + (key2 << 5) + 3;
      key2 += CryptTable[0x400 + (key1 & 0xFF)];
      if (acceptSecond(encrypted1 ^ (key1 + key2)))
        return saved;
    }
    return 0;
  }

  /// <summary>
  /// Decrypts a block in place. Each iteration: read uint32, XOR against
  /// (key+seed), write back, then evolve seed using the *plaintext* (= post-XOR)
  /// value. Symmetric counterpart of <see cref="EncryptBlock(byte[], uint)"/>.
  /// </summary>
  internal static void DecryptBlock(byte[] data, uint key) => DecryptBlock(data.AsSpan(), key);

  /// <inheritdoc cref="DecryptBlock(byte[], uint)"/>
  internal static void DecryptBlock(Span<byte> data, uint key) {
    uint seed = 0xEEEEEEEE;
    for (var i = 0; i + 4 <= data.Length; i += 4) {
      seed += CryptTable[0x400 + (key & 0xFF)];
      var encrypted = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
      var plaintext = encrypted ^ (key + seed);
      BinaryPrimitives.WriteUInt32LittleEndian(data[i..], plaintext);
      key = ((~key << 0x15) + 0x11111111) | (key >> 0x0B);
      seed = plaintext + seed + (seed << 5) + 3;
    }
  }

  /// <summary>
  /// Encrypts a block in place. Mirror of <see cref="DecryptBlock(byte[], uint)"/>: same key
  /// schedule, but the seed evolves using the *plaintext input* (since after
  /// the XOR we already have ciphertext).
  /// </summary>
  internal static void EncryptBlock(byte[] data, uint key) => EncryptBlock(data.AsSpan(), key);

  /// <inheritdoc cref="EncryptBlock(byte[], uint)"/>
  internal static void EncryptBlock(Span<byte> data, uint key) {
    uint seed = 0xEEEEEEEE;
    for (var i = 0; i + 4 <= data.Length; i += 4) {
      seed += CryptTable[0x400 + (key & 0xFF)];
      var plaintext = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
      var encrypted = plaintext ^ (key + seed);
      BinaryPrimitives.WriteUInt32LittleEndian(data[i..], encrypted);
      key = ((~key << 0x15) + 0x11111111) | (key >> 0x0B);
      seed = plaintext + seed + (seed << 5) + 3;
    }
  }

  private static uint[] BuildCryptTable() {
    var table = new uint[0x500];
    uint seed = 0x00100001;
    for (var i = 0; i < 256; i++) {
      var index = i;
      for (var j = 0; j < 5; j++) {
        seed = (seed * 125 + 3) % 0x2AAAAB;
        var temp1 = (seed & 0xFFFF) << 16;
        seed = (seed * 125 + 3) % 0x2AAAAB;
        var temp2 = seed & 0xFFFF;
        table[index] = temp1 | temp2;
        index += 256;
      }
    }
    return table;
  }
}
