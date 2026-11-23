using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Crypto;
using Compression.Registry;

namespace FileFormat.StuffItX;

/// <summary>
/// The password protection of StuffIt X streams: one or more cipher layers (algorithm key 4 of an
/// element), keyed from one MD5-based derivation and guarded by a two-byte password check.
/// </summary>
/// <remarks>
/// <para>
/// Each key-4 record names a cipher (0 AES with a 16-, 24- or 32-byte key, 1 Blowfish with 5 to 56
/// bytes, 2 DES with 8, 3 RC4) and its key length. The keys are consecutive slices of one derived
/// string as long as all key lengths together; a second derivation of that string, two bytes long,
/// is the check value that opens the coded stream. After it come the layers, the last record's
/// outermost: each starts with a prefix, the IV of a block cipher (16 bytes for AES, 8 otherwise) or,
/// for RC4, as many bytes as the key, appended to the key to form the RC4 key. The block ciphers run
/// in full-block cipher feedback; a short last block takes the leading keystream bytes.
/// </para>
/// <para>
/// The derivation runs 256 rounds over a single MD5 state that is never reset. A round produces as
/// many 16-byte pieces as the length rounded up to 16 needs: for each, "StuffIt", the piece's offset
/// as two big-endian bytes and the round's input are absorbed, and the piece is the digest the state
/// would give if it ended there. Taking that digest pads the running state (0x80 and zeros up to 56
/// mod 64) but compresses the bit length only in a scratch copy of the block, so the padding stays
/// part of the message. The first round's input is the UTF-8 password, each later round's the
/// previous round's output.
/// </para>
/// <para>Converted from KaitoKit's <c>StuffItXCrypto.swift</c> (MIT, see THIRD-PARTY-NOTICE.KaitoKit.txt).</para>
/// </remarks>
internal sealed class StuffItXCrypto {
  private const int MaxTotalKeyLength = 65536;

  /// <summary>One cipher layer: the cipher number and its key.</summary>
  internal sealed record Layer(long Cipher, byte[] Key) {
    /// <summary>The IV (block ciphers) or salt (RC4) at the start of this layer.</summary>
    public int PrefixSize => this.Cipher switch { 0 => 16, 3 => this.Key.Length, _ => 8 };
  }

  private readonly byte[] _verifier;

  /// <summary>The layers, in record order (the first is the innermost).</summary>
  internal IReadOnlyList<Layer> Layers { get; }

  /// <summary>Derives every layer's key from <paramref name="password"/>.</summary>
  /// <exception cref="NotSupportedException">A record names an unknown cipher.</exception>
  /// <exception cref="InvalidDataException">A key length does not fit its cipher.</exception>
  internal StuffItXCrypto(string password, IReadOnlyList<(long Cipher, long KeyLength)> records) {
    var total = Validate(records);
    var material = Derive(Encoding.UTF8.GetBytes(password), total);
    this._verifier = Derive(material, 2);
    var layers = new List<Layer>(records.Count);
    var offset = 0;
    foreach (var (cipher, length) in records) {
      layers.Add(new Layer(cipher, material.AsSpan(offset, (int)length).ToArray()));
      offset += (int)length;
    }
    this.Layers = layers;
  }

  /// <summary>The name of the cipher layers, for listings (e.g. "AES-256").</summary>
  internal static string Describe(IReadOnlyList<(long Cipher, long KeyLength)> records)
    => string.Join("+", records.Select(r => r.Cipher switch {
      0 => $"AES-{r.KeyLength * 8}",
      1 => "Blowfish",
      2 => "DES",
      3 => "RC4",
      var c => $"Cipher {c}",
    }));

  /// <summary>Checks the key lengths against their ciphers and returns their sum.</summary>
  internal static int Validate(IReadOnlyList<(long Cipher, long KeyLength)> records) {
    long total = 0;
    foreach (var (cipher, length) in records) {
      var fits = cipher switch {
        0 => length is 16 or 24 or 32,
        1 => length is >= 5 and <= 56,
        2 => length == 8,
        3 => length is >= 1 and <= 1024,
        _ => throw new NotSupportedException($"StuffIt X encryption {cipher} is not supported."),
      };
      if (!fits) throw new InvalidDataException($"StuffIt X cipher {cipher} cannot take a {length}-byte key.");
      total += length;
      if (total > MaxTotalKeyLength) throw new InvalidDataException("StuffIt X cipher keys are too long.");
    }
    return (int)total;
  }

  /// <summary>The derivation: 256 rounds over one continuing MD5 state.</summary>
  internal static byte[] Derive(byte[] source, int length) {
    if (length is < 1 or > MaxTotalKeyLength) throw new ArgumentOutOfRangeException(nameof(length));
    var rounded = (length + 15) & ~15;
    var md5 = new ContinuingMd5();
    Span<byte> offsetBytes = stackalloc byte[2];
    for (var round = 0; round < 256; ++round) {
      var output = new byte[rounded];
      for (var offset = 0; offset < rounded; offset += 16) {
        md5.Absorb("StuffIt"u8);
        offsetBytes[0] = (byte)(offset >> 8);
        offsetBytes[1] = (byte)offset;
        md5.Absorb(offsetBytes);
        md5.Absorb(source);
        md5.Snapshot(output.AsSpan(offset, 16));
      }
      source = output;
    }
    return source.AsSpan(0, length).ToArray();
  }

  /// <summary>Checks the password and strips every layer off a coded stream.</summary>
  /// <exception cref="ArchivePasswordException">The password check fails.</exception>
  /// <exception cref="InvalidDataException">The stream is shorter than its prefixes.</exception>
  internal byte[] Decrypt(byte[] payload) {
    var minimum = 2 + this.Layers.Sum(l => l.PrefixSize);
    if (payload.Length < minimum) throw new InvalidDataException("StuffIt X encrypted stream is truncated.");
    if (payload[0] != this._verifier[0] || payload[1] != this._verifier[1])
      throw new ArchivePasswordException("The password for this StuffIt X archive is wrong.", true);
    var data = payload.AsSpan(2).ToArray();
    for (var i = this.Layers.Count - 1; i >= 0; --i) data = Strip(this.Layers[i], data);
    return data;
  }

  /// <summary>Removes one layer: its prefix, then the deciphered rest.</summary>
  private static byte[] Strip(Layer layer, byte[] data) {
    var prefixSize = layer.PrefixSize;
    if (data.Length < prefixSize) throw new InvalidDataException("StuffIt X encrypted stream is truncated.");
    var prefix = data.AsSpan(0, prefixSize);
    var body = data.AsSpan(prefixSize).ToArray();
    if (layer.Cipher == 3) {
      // RC4 keys with key ‖ salt; only the first 256 bytes of a key ever reach the schedule
      var key = new byte[layer.Key.Length + prefixSize];
      layer.Key.CopyTo(key, 0);
      prefix.CopyTo(key.AsSpan(layer.Key.Length));
      new Rc4(key.AsSpan(0, Math.Min(key.Length, 256))).Transform(body);
      return body;
    }
    Action<Span<byte>> encrypt = layer.Cipher switch {
      0 => Aes(layer.Key),
      1 => new Blowfish(layer.Key).Encrypt,
      2 => new TripleDes([.. layer.Key, .. layer.Key]).Encrypt,
      var c => throw new NotSupportedException($"StuffIt X encryption {c} is not supported."),
    };
    DecryptCfb(encrypt, prefix, body);
    return body;
  }

  private static Action<Span<byte>> Aes(byte[] key) {
    var aes = new AesBlockCipher(key);
    return aes.Encrypt;
  }

  /// <summary>Full-block cipher feedback: each block's keystream is the encrypted previous ciphertext block.</summary>
  private static void DecryptCfb(Action<Span<byte>> encrypt, ReadOnlySpan<byte> iv, Span<byte> data) {
    var size = iv.Length;
    Span<byte> feedback = stackalloc byte[size];
    Span<byte> cipher = stackalloc byte[size];
    iv.CopyTo(feedback);
    for (var offset = 0; offset < data.Length; offset += size) {
      var n = Math.Min(size, data.Length - offset);
      var block = data.Slice(offset, n);
      block.CopyTo(cipher);
      encrypt(feedback);
      for (var i = 0; i < n; ++i) block[i] ^= feedback[i];
      cipher.CopyTo(feedback);
    }
  }

  /// <summary>An MD5 state that can report a digest and keep going (see the remarks above).</summary>
  private sealed class ContinuingMd5 {
    private readonly uint[] _state = new uint[4];
    private readonly byte[] _buffer = new byte[64];
    private readonly byte[] _scratch = new byte[64];
    private long _count;

    public ContinuingMd5() => Md5.Initialize(this._state);

    public void Absorb(ReadOnlySpan<byte> data) {
      foreach (var b in data) {
        this._buffer[(int)(this._count & 63)] = b;
        if ((++this._count & 63) == 0) Md5.Compress(this._state, this._buffer);
      }
    }

    public void Snapshot(Span<byte> digest) {
      var bits = (ulong)this._count * 8;
      this.Absorb([0x80]);
      while ((this._count & 63) != 56) this.Absorb([0]);
      this._buffer.CopyTo(this._scratch, 0);
      for (var i = 0; i < 8; ++i) this._scratch[56 + i] = (byte)(bits >> (8 * i));
      Md5.Compress(this._state, this._scratch);
      for (var i = 0; i < 4; ++i)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(digest[(i * 4)..], this._state[i]);
    }
  }
}
