namespace FileFormat.Mpq;

/// <summary>
/// MPQ crypto primitives shared by <see cref="MpqReader"/> and
/// <see cref="MpqWriter"/>: the precomputed crypt table, the MPQ string hash
/// (used both to address the hash table and to derive table-encryption keys),
/// and symmetric block encryption/decryption used on the hash and block tables.
/// </summary>
internal static class MpqCrypto {
  /// <summary>Hash type for the table-position hash (for filename → slot).</summary>
  internal const uint HashTypeOffset = 0x000;
  /// <summary>Hash type for the per-entry "hash A" verification field.</summary>
  internal const uint HashTypeNameA = 0x100;
  /// <summary>Hash type for the per-entry "hash B" verification field.</summary>
  internal const uint HashTypeNameB = 0x200;
  /// <summary>Hash type used when generating an encryption key from a string (e.g. "(hash table)").</summary>
  internal const uint HashTypeFileKey = 0x300;

  internal static readonly uint[] CryptTable = BuildCryptTable();

  internal static uint HashString(string str, uint hashType) {
    uint seed1 = 0x7FED7FED;
    uint seed2 = 0xEEEEEEEE;
    foreach (var c in str.ToUpperInvariant()) {
      var ch = (uint)c;
      seed1 = CryptTable[hashType + ch] ^ (seed1 + seed2);
      seed2 = ch + seed1 + seed2 + (seed2 << 5) + 3;
    }
    return seed1;
  }

  /// <summary>
  /// Decrypts a block in place. Each iteration: read uint32, XOR against
  /// (key+seed), write back, then evolve seed using the *plaintext* (= post-XOR)
  /// value. Symmetric counterpart of <see cref="EncryptBlock"/>.
  /// </summary>
  internal static void DecryptBlock(byte[] data, uint key) {
    uint seed = 0xEEEEEEEE;
    for (var i = 0; i + 4 <= data.Length; i += 4) {
      seed += CryptTable[0x400 + (key & 0xFF)];
      var encrypted = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i));
      var plaintext = encrypted ^ (key + seed);
      System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i), plaintext);
      key = ((~key << 0x15) + 0x11111111) | (key >> 0x0B);
      seed = plaintext + seed + (seed << 5) + 3;
    }
  }

  /// <summary>
  /// Encrypts a block in place. Mirror of <see cref="DecryptBlock"/>: same key
  /// schedule, but the seed evolves using the *plaintext input* (since after
  /// the XOR we already have ciphertext).
  /// </summary>
  internal static void EncryptBlock(byte[] data, uint key) {
    uint seed = 0xEEEEEEEE;
    for (var i = 0; i + 4 <= data.Length; i += 4) {
      seed += CryptTable[0x400 + (key & 0xFF)];
      var plaintext = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i));
      var encrypted = plaintext ^ (key + seed);
      System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i), encrypted);
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
