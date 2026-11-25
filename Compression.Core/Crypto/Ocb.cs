using System.Numerics;
using System.Security.Cryptography;

namespace Compression.Core.Crypto;

/// <summary>
/// OCB authenticated encryption (RFC 7253, OCB3) over a 128-bit block cipher with 128-bit tags
/// and nonces of 1 to 15 bytes: ciphertext and plaintext have the same length; the tag is
/// returned or checked separately.
/// </summary>
public sealed class Ocb {
  /// <summary>Block and tag size in bytes.</summary>
  public const int BlockSize = 16;

  private readonly Action<Span<byte>> _encrypt;
  private readonly Action<Span<byte>> _decrypt;
  private readonly byte[] _lStar = new byte[BlockSize];
  private readonly byte[] _lDollar = new byte[BlockSize];
  private readonly List<byte[]> _l = [];

  /// <summary>OCB over a cipher that encrypts and decrypts one 16-byte block in place.</summary>
  public Ocb(Action<Span<byte>> encrypt, Action<Span<byte>> decrypt) {
    ArgumentNullException.ThrowIfNull(encrypt);
    ArgumentNullException.ThrowIfNull(decrypt);
    this._encrypt = encrypt;
    this._decrypt = decrypt;
    encrypt(this._lStar); // L_* = ENCIPHER(K, zeros(128))
    Double(this._lStar, this._lDollar);
    var l0 = new byte[BlockSize];
    Double(this._lDollar, l0);
    this._l.Add(l0);
  }

  /// <summary>OCB with AES under a 16-, 24- or 32-byte key.</summary>
  public static Ocb WithAes(ReadOnlySpan<byte> key) {
    var aes = new AesBlockCipher(key);
    return new Ocb(b => aes.Encrypt(b), b => aes.Decrypt(b));
  }

  private static void Double(ReadOnlySpan<byte> s, Span<byte> output) {
    var carry = s[0] >> 7;
    for (var i = 0; i < BlockSize - 1; ++i)
      output[i] = (byte)((s[i] << 1) | (s[i + 1] >> 7));
    output[BlockSize - 1] = (byte)((s[BlockSize - 1] << 1) ^ (carry * 0x87));
  }

  private byte[] L(int i) {
    while (this._l.Count <= i) {
      var next = new byte[BlockSize];
      Double(this._l[^1], next);
      this._l.Add(next);
    }
    return this._l[i];
  }

  private static void Xor(Span<byte> target, ReadOnlySpan<byte> other) {
    for (var i = 0; i < target.Length; ++i)
      target[i] ^= other[i];
  }

  /// <summary>Offset_0 for <paramref name="nonce"/> (section 4.2, "Nonce-dependent and per-encryption variables").</summary>
  private byte[] InitialOffset(ReadOnlySpan<byte> nonce) {
    if (nonce.Length is 0 or > 15)
      throw new ArgumentException("An OCB nonce is 1 to 15 bytes.", nameof(nonce));
    // Nonce = num2str(TAGLEN mod 128, 7) || zeros(120 - bitlen(N)) || 1 || N, with TAGLEN = 128
    Span<byte> full = stackalloc byte[BlockSize];
    nonce.CopyTo(full[(BlockSize - nonce.Length)..]);
    full[BlockSize - 1 - nonce.Length] |= 1;
    var bottom = full[BlockSize - 1] & 0x3F;
    full[BlockSize - 1] &= 0xC0;
    var ktop = full.ToArray();
    this._encrypt(ktop);
    // Stretch = Ktop || (Ktop[1..64] xor Ktop[9..72]); Offset_0 = Stretch[1+bottom..128+bottom]
    Span<byte> stretch = stackalloc byte[BlockSize + 8 + 1];
    ktop.CopyTo(stretch);
    for (var i = 0; i < 8; ++i)
      stretch[BlockSize + i] = (byte)(ktop[i] ^ ktop[i + 1]);
    var offset = new byte[BlockSize];
    var bytes = bottom / 8;
    var bits = bottom % 8;
    for (var i = 0; i < BlockSize; ++i)
      offset[i] = bits == 0
        ? stretch[i + bytes]
        : (byte)((stretch[i + bytes] << bits) | (stretch[i + bytes + 1] >> (8 - bits)));
    return offset;
  }

  /// <summary>HASH(K, A) (section 4.1).</summary>
  private byte[] Hash(ReadOnlySpan<byte> associated) {
    var sum = new byte[BlockSize];
    Span<byte> offset = stackalloc byte[BlockSize];
    Span<byte> block = stackalloc byte[BlockSize];
    var full = associated.Length / BlockSize;
    for (var i = 1; i <= full; ++i) {
      Xor(offset, this.L(BitOperations.TrailingZeroCount(i)));
      associated.Slice((i - 1) * BlockSize, BlockSize).CopyTo(block);
      Xor(block, offset);
      this._encrypt(block);
      Xor(sum, block);
    }
    var rest = associated[(full * BlockSize)..];
    if (rest.Length > 0) {
      Xor(offset, this._lStar);
      block.Clear();
      rest.CopyTo(block);
      block[rest.Length] = 0x80;
      Xor(block, offset);
      this._encrypt(block);
      Xor(sum, block);
    }
    return sum;
  }

  /// <summary>Encrypts <paramref name="plaintext"/> into <paramref name="ciphertext"/> (same length) and writes the 16-byte tag.</summary>
  public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associated, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag)
    => this.Process(true, nonce, associated, plaintext, ciphertext, tag);

  /// <summary>Decrypts <paramref name="ciphertext"/> into <paramref name="plaintext"/> and checks <paramref name="tag"/>.</summary>
  /// <exception cref="CryptographicException">The tag does not match; <paramref name="plaintext"/> is cleared.</exception>
  public void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associated, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext) {
    Span<byte> expected = stackalloc byte[BlockSize];
    this.Process(false, nonce, associated, ciphertext, plaintext, expected);
    if (tag.Length != BlockSize || !CryptographicOperations.FixedTimeEquals(expected, tag)) {
      plaintext[..ciphertext.Length].Clear();
      throw new CryptographicException("The OCB authentication tag does not match (wrong key or damaged data).");
    }
  }

  private void Process(bool encrypting, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associated, ReadOnlySpan<byte> input, Span<byte> output, Span<byte> tag) {
    if (output.Length < input.Length)
      throw new ArgumentException("The OCB output is shorter than its input.", nameof(output));
    var offset = this.InitialOffset(nonce);
    Span<byte> checksum = stackalloc byte[BlockSize];
    checksum.Clear();
    Span<byte> block = stackalloc byte[BlockSize];
    var full = input.Length / BlockSize;
    for (var i = 1; i <= full; ++i) {
      Xor(offset, this.L(BitOperations.TrailingZeroCount(i)));
      var source = input.Slice((i - 1) * BlockSize, BlockSize);
      if (encrypting)
        Xor(checksum, source);
      source.CopyTo(block);
      Xor(block, offset);
      if (encrypting)
        this._encrypt(block);
      else
        this._decrypt(block);
      Xor(block, offset);
      block.CopyTo(output.Slice((i - 1) * BlockSize, BlockSize));
      if (!encrypting)
        Xor(checksum, block);
    }
    var rest = input[(full * BlockSize)..];
    if (rest.Length > 0) {
      Xor(offset, this._lStar);
      offset.CopyTo(block);
      this._encrypt(block); // Pad = ENCIPHER(K, Offset_*)
      var target = output.Slice(full * BlockSize, rest.Length);
      for (var i = 0; i < rest.Length; ++i)
        target[i] = (byte)(rest[i] ^ block[i]);
      ReadOnlySpan<byte> clear = encrypting ? rest : target;
      for (var i = 0; i < clear.Length; ++i)
        checksum[i] ^= clear[i];
      checksum[clear.Length] ^= 0x80;
    }
    // Tag = ENCIPHER(K, Checksum xor Offset xor L_$) xor HASH(K, A)
    Xor(checksum, offset);
    Xor(checksum, this._lDollar);
    this._encrypt(checksum);
    Xor(checksum, this.Hash(associated));
    checksum.CopyTo(tag);
  }
}
