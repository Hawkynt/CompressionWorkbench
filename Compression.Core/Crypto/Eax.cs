using System.Security.Cryptography;

namespace Compression.Core.Crypto;

/// <summary>
/// Encrypts 128-bit blocks in ECB fashion: <paramref name="output"/> receives the encryption of
/// every 16-byte block of <paramref name="input"/> (same length, a multiple of 16).
/// </summary>
public delegate void BlockEncryptor128(ReadOnlySpan<byte> input, Span<byte> output);

/// <summary>
/// EAX authenticated encryption (Bellare, Rogaway and Wagner, "The EAX Mode of Operation", FSE
/// 2004) over any 128-bit block cipher: CTR encryption started at the OMAC of the nonce, and a
/// 16-byte tag that is the XOR of the OMACs of nonce, header and ciphertext. Data may arrive in
/// pieces of any length; the result equals one call over their concatenation.
/// </summary>
public sealed class Eax {
  /// <summary>The block and tag size in bytes.</summary>
  public const int BlockSize = 16;

  private readonly BlockEncryptor128 _encrypt;
  private readonly byte[] _nonceTag;
  private readonly byte[] _headerTag;
  private readonly Omac _message;
  private readonly byte[] _counter = new byte[BlockSize];
  private readonly byte[] _keyStream = new byte[BlockSize * 64];
  private int _keyStreamUsed;
  private int _keyStreamLength;

  /// <summary>Starts an EAX message under <paramref name="encrypt"/> with the given nonce and header (associated data).</summary>
  public Eax(BlockEncryptor128 encrypt, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> header = default) {
    ArgumentNullException.ThrowIfNull(encrypt);
    this._encrypt = encrypt;
    var subkeys = Omac.Subkeys(encrypt);
    this._nonceTag = Omac.Compute(encrypt, subkeys, 0, nonce);
    this._headerTag = Omac.Compute(encrypt, subkeys, 1, header);
    this._message = new Omac(encrypt, subkeys, 2);
    this._nonceTag.CopyTo(this._counter, 0);
  }

  /// <summary>EAX with AES under a 16-, 24- or 32-byte key.</summary>
  public static Eax WithAes(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> header = default) {
    var aes = Aes.Create();
    aes.Key = key.ToArray();
    return new Eax((input, output) => aes.EncryptEcb(input, output, PaddingMode.None), nonce, header);
  }

  /// <summary>EAX with Twofish under a 16-, 24- or 32-byte key.</summary>
  public static Eax WithTwofish(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> header = default) {
    var cipher = new Twofish(key);
    return new Eax(InPlace(cipher.Encrypt), nonce, header);
  }

  /// <summary>EAX with Serpent under a 16-, 24- or 32-byte key.</summary>
  public static Eax WithSerpent(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> header = default) {
    var cipher = new Serpent(key);
    return new Eax(InPlace(cipher.Encrypt), nonce, header);
  }

  /// <summary>Adapts a cipher that encrypts one block in place.</summary>
  public static BlockEncryptor128 InPlace(SpanAction encryptBlock) => (input, output) => {
    input.CopyTo(output);
    for (var i = 0; i + BlockSize <= output.Length; i += BlockSize) encryptBlock(output.Slice(i, BlockSize));
  };

  /// <summary>Encrypts one block in place.</summary>
  public delegate void SpanAction(Span<byte> block);

  /// <summary>Encrypts <paramref name="data"/> in place and feeds the ciphertext to the tag.</summary>
  public void Encrypt(Span<byte> data) {
    this.ApplyKeyStream(data);
    this._message.Update(data);
  }

  /// <summary>Feeds <paramref name="data"/> (ciphertext) to the tag and decrypts it in place.</summary>
  public void Decrypt(Span<byte> data) {
    this._message.Update(data);
    this.ApplyKeyStream(data);
  }

  /// <summary>The 16-byte tag over everything encrypted or decrypted so far; ends the message.</summary>
  public byte[] Tag() {
    var tag = this._message.Final();
    for (var i = 0; i < BlockSize; ++i) tag[i] ^= (byte)(this._nonceTag[i] ^ this._headerTag[i]);
    return tag;
  }

  private void ApplyKeyStream(Span<byte> data) {
    while (data.Length > 0) {
      if (this._keyStreamUsed == this._keyStreamLength) this.RefillKeyStream(data.Length);
      var n = Math.Min(data.Length, this._keyStreamLength - this._keyStreamUsed);
      var ks = this._keyStream.AsSpan(this._keyStreamUsed, n);
      for (var i = 0; i < n; ++i) data[i] ^= ks[i];
      this._keyStreamUsed += n;
      data = data[n..];
    }
  }

  private void RefillKeyStream(int wanted) {
    var blocks = Math.Clamp((wanted + BlockSize - 1) / BlockSize, 1, this._keyStream.Length / BlockSize);
    var counters = new byte[blocks * BlockSize];
    for (var b = 0; b < blocks; ++b) {
      this._counter.CopyTo(counters, b * BlockSize);
      for (var i = BlockSize - 1; i >= 0 && ++this._counter[i] == 0; --i) { }
    }
    this._encrypt(counters, this._keyStream.AsSpan(0, counters.Length));
    this._keyStreamUsed = 0;
    this._keyStreamLength = counters.Length;
  }

  /// <summary>OMAC1 (CMAC, NIST SP 800-38B) of the tweak block [0…0 t] followed by the data, incrementally.</summary>
  private sealed class Omac {
    private readonly BlockEncryptor128 _encrypt;
    private readonly (byte[] K1, byte[] K2) _subkeys;
    private readonly byte[] _state = new byte[BlockSize];
    private readonly byte[] _pending = new byte[BlockSize];
    private int _pendingLength;

    public Omac(BlockEncryptor128 encrypt, (byte[] K1, byte[] K2) subkeys, byte tweak) {
      this._encrypt = encrypt;
      this._subkeys = subkeys;
      this._pending[BlockSize - 1] = tweak;
      this._pendingLength = BlockSize;
    }

    public static (byte[] K1, byte[] K2) Subkeys(BlockEncryptor128 encrypt) {
      var l = new byte[BlockSize];
      encrypt(new byte[BlockSize], l);
      var k1 = Double(l);
      return (k1, Double(k1));
    }

    private static byte[] Double(byte[] x) {
      var r = new byte[BlockSize];
      for (var i = 0; i < BlockSize; ++i) r[i] = (byte)((x[i] << 1) | (i + 1 < BlockSize ? x[i + 1] >> 7 : 0));
      if ((x[0] & 0x80) != 0) r[BlockSize - 1] ^= 0x87;
      return r;
    }

    public static byte[] Compute(BlockEncryptor128 encrypt, (byte[] K1, byte[] K2) subkeys, byte tweak, ReadOnlySpan<byte> data) {
      var omac = new Omac(encrypt, subkeys, tweak);
      omac.Update(data);
      return omac.Final();
    }

    public void Update(ReadOnlySpan<byte> data) {
      while (data.Length > 0) {
        if (this._pendingLength == BlockSize) {
          // a full block is absorbed only once more data follows: the last block is treated specially
          this.Absorb(this._pending);
          this._pendingLength = 0;
        }
        var n = Math.Min(BlockSize - this._pendingLength, data.Length);
        data[..n].CopyTo(this._pending.AsSpan(this._pendingLength));
        this._pendingLength += n;
        data = data[n..];
      }
    }

    private void Absorb(ReadOnlySpan<byte> block) {
      Span<byte> x = stackalloc byte[BlockSize];
      for (var i = 0; i < BlockSize; ++i) x[i] = (byte)(this._state[i] ^ block[i]);
      this._encrypt(x, this._state);
    }

    public byte[] Final() {
      var last = new byte[BlockSize];
      this._pending.AsSpan(0, this._pendingLength).CopyTo(last);
      byte[] k;
      if (this._pendingLength == BlockSize) k = this._subkeys.K1;
      else {
        last[this._pendingLength] = 0x80;
        k = this._subkeys.K2;
      }
      for (var i = 0; i < BlockSize; ++i) last[i] ^= k[i];
      this.Absorb(last);
      return (byte[])this._state.Clone();
    }
  }
}
