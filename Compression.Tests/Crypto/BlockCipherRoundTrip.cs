namespace Compression.Tests.Crypto;

/// <summary>Random encrypt/decrypt round trips shared by the block-cipher tests.</summary>
internal static class BlockCipherRoundTrip {
  internal delegate void BlockTransform(Span<byte> block);

  /// <summary>Encrypts and decrypts random blocks under random keys, one cipher instance per key.</summary>
  internal static void Assert(Random random, int keyLength, int blockSize, Func<byte[], (BlockTransform Encrypt, BlockTransform Decrypt)> create) {
    for (var trial = 0; trial < 16; ++trial) {
      var key = new byte[keyLength];
      random.NextBytes(key);
      var (encrypt, decrypt) = create(key);
      for (var b = 0; b < 8; ++b) {
        var original = new byte[blockSize];
        random.NextBytes(original);
        var block = (byte[])original.Clone();
        encrypt(block);
        NUnit.Framework.Assert.That(block, Is.Not.EqualTo(original));
        decrypt(block);
        NUnit.Framework.Assert.That(block, Is.EqualTo(original), $"key {Convert.ToHexStringLower(key)}");
      }
    }
  }
}
