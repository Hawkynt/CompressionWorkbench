using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Crypto;
using Compression.Registry;

namespace FileFormat.InnoSetup;

/// <summary>
/// XChaCha20 as Inno Setup 6.4+ uses it: HChaCha20 derives a subkey from the key and the first
/// 16 nonce bytes, then ChaCha20 (20 rounds, 64-bit block counter from 0) runs with the last 8
/// nonce bytes. Converted from innoextract's crypto/xchacha20.cpp (zlib licence).
/// </summary>
internal sealed class InnoXChaCha20 {
  private readonly uint[] _state = new uint[16];
  private readonly uint[] _block = new uint[16];
  private int _position = 64;

  public InnoXChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce) {
    if (key.Length != 32 || nonce.Length != 24)
      throw new ArgumentException("XChaCha20 takes a 32-byte key and a 24-byte nonce.");
    Span<uint> h = stackalloc uint[16];
    Init(h, key);
    for (var i = 0; i < 4; ++i)
      h[12 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[(i * 4)..]);
    Rounds(h);
    Span<byte> subkey = stackalloc byte[32];
    for (var i = 0; i < 4; ++i) {
      BinaryPrimitives.WriteUInt32LittleEndian(subkey[(i * 4)..], h[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(subkey[(16 + i * 4)..], h[12 + i]);
    }
    Init(this._state, subkey);
    this._state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[16..]);
    this._state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[20..]);
  }

  /// <summary>The HChaCha20 subkey alone (for the test vectors).</summary>
  internal static byte[] HChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce16) {
    Span<uint> h = stackalloc uint[16];
    Init(h, key);
    for (var i = 0; i < 4; ++i)
      h[12 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce16[(i * 4)..]);
    Rounds(h);
    var subkey = new byte[32];
    for (var i = 0; i < 4; ++i) {
      BinaryPrimitives.WriteUInt32LittleEndian(subkey.AsSpan(i * 4), h[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(subkey.AsSpan(16 + i * 4), h[12 + i]);
    }
    return subkey;
  }

  /// <summary>XORs the key stream into <paramref name="data"/>, continuing where the last call stopped.</summary>
  public void Transform(Span<byte> data) {
    for (var i = 0; i < data.Length; ++i, ++this._position) {
      if (this._position == 64) {
        this._state.CopyTo(this._block, 0);
        Rounds(this._block);
        for (var k = 0; k < 16; ++k)
          this._block[k] += this._state[k];
        if (++this._state[12] == 0)
          ++this._state[13];
        this._position = 0;
      }
      data[i] ^= (byte)(this._block[this._position >> 2] >> ((this._position & 3) * 8));
    }
  }

  private static void Init(Span<uint> state, ReadOnlySpan<byte> key) {
    state[0] = 0x61707865;
    state[1] = 0x3320646e;
    state[2] = 0x79622d32;
    state[3] = 0x6b206574;
    for (var i = 0; i < 8; ++i)
      state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
    state[12] = state[13] = 0;
  }

  private static void Rounds(Span<uint> x) {
    for (var i = 0; i < 10; ++i) {
      Quarter(x, 0, 4, 8, 12);
      Quarter(x, 1, 5, 9, 13);
      Quarter(x, 2, 6, 10, 14);
      Quarter(x, 3, 7, 11, 15);
      Quarter(x, 0, 5, 10, 15);
      Quarter(x, 1, 6, 11, 12);
      Quarter(x, 2, 7, 8, 13);
      Quarter(x, 3, 4, 9, 14);
    }
  }

  private static void Quarter(Span<uint> x, int a, int b, int c, int d) {
    x[a] += x[b]; x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 16);
    x[c] += x[d]; x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 12);
    x[a] += x[b]; x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 8);
    x[c] += x[d]; x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 7);
  }
}

/// <summary>How an installer protects its files, and what checks a password against it.</summary>
internal enum InnoPasswordCheck { None, Crc32, Md5, Sha1, XChaCha20 }

/// <summary>
/// The password material of an installer and the keys derived from a password (innoextract
/// setup/info.cpp, stream/chunk.cpp; issrc Shared.EncryptionFunc.pas for 6.5+).
/// </summary>
internal sealed class InnoPasswordData {
  /// <summary>The kind of the stored password check.</summary>
  public InnoPasswordCheck Check { get; set; }
  /// <summary>The stored check: CRC32 (4), MD5 (16), SHA-1 (20) or the encrypted zero (4, XChaCha20).</summary>
  public byte[] Expected { get; set; } = [];
  /// <summary>Before 6.4: the 8-byte salt (from 4.2.2). 6.4+: the 16-byte PBKDF2 salt.</summary>
  public byte[] Salt { get; set; } = [];
  /// <summary>6.4+: PBKDF2-SHA256 iterations.</summary>
  public int Iterations { get; set; }
  /// <summary>6.4+: the 24-byte XChaCha20 base nonce.</summary>
  public byte[] BaseNonce { get; set; } = [];

  /// <summary>The nonce-slice value of the special contexts (issrc TSpecialCryptContextType): password test -1, header blocks -2 and -3.</summary>
  public const int PasswordTestSlice = -1, HeaderBlock1Slice = -2, HeaderBlock2Slice = -3;

  /// <summary>The password as the installer hashes it: UTF-16LE for Unicode installers, the setup's codepage otherwise.</summary>
  public static byte[] Encode(string password, bool unicode, Encoding ansi) =>
    unicode ? Encoding.Unicode.GetBytes(password) : ansi.GetBytes(password);

  /// <summary>6.4+: the 32-byte key PBKDF2-SHA256 derives from the password (UTF-16LE).</summary>
  public byte[] DeriveKey(byte[] encodedPassword) =>
    Rfc2898DeriveBytes.Pbkdf2(encodedPassword, this.Salt, this.Iterations, HashAlgorithmName.SHA256, 32);

  /// <summary>
  /// 6.4+: the cipher for a chunk (or special context): the base nonce with its first 8 bytes
  /// XORed with the chunk's start offset and the next 4 with its first slice.
  /// </summary>
  public InnoXChaCha20 Cipher(byte[] key, long startOffset, int firstSlice) {
    var nonce = this.BaseNonce.ToArray();
    BinaryPrimitives.WriteInt64LittleEndian(nonce, BinaryPrimitives.ReadInt64LittleEndian(nonce) ^ startOffset);
    BinaryPrimitives.WriteInt32LittleEndian(nonce.AsSpan(8), BinaryPrimitives.ReadInt32LittleEndian(nonce.AsSpan(8)) ^ firstSlice);
    return new InnoXChaCha20(key, nonce);
  }

  /// <summary>
  /// The key for the files (6.4+: the derived key; earlier: the encoded password), after checking
  /// the password against the stored check.
  /// </summary>
  /// <exception cref="ArchivePasswordException">No password, or the wrong one.</exception>
  public byte[] Key(string? password, bool unicode, Encoding ansi) {
    if (string.IsNullOrEmpty(password))
      throw new ArchivePasswordException("Inno Setup: the installer encrypts its files; a password is needed.", passwordWasSupplied: false);
    var encoded = Encode(password, unicode || this.Check == InnoPasswordCheck.XChaCha20, ansi);
    bool ok;
    byte[] key;
    if (this.Check == InnoPasswordCheck.XChaCha20) {
      key = this.DeriveKey(encoded);
      var test = new byte[4];
      this.Cipher(key, 0, PasswordTestSlice).Transform(test);
      ok = test.AsSpan().SequenceEqual(this.Expected);
    } else {
      key = encoded;
      // the check hashes "PasswordCheckHash" + salt (from 4.2.2) + password
      byte[] salted = this.Salt.Length == 0 ? encoded : [.. "PasswordCheckHash"u8, .. this.Salt, .. encoded];
      ok = this.Check switch {
        InnoPasswordCheck.Sha1 => SHA1.HashData(salted).AsSpan().SequenceEqual(this.Expected),
        InnoPasswordCheck.Md5 => MD5.HashData(salted).AsSpan().SequenceEqual(this.Expected),
        InnoPasswordCheck.Crc32 => BinaryPrimitives.ReadUInt32LittleEndian(this.Expected) == Compression.Core.Checksums.Crc32.Compute(encoded),
        _ => true,
      };
    }
    if (!ok)
      throw new ArchivePasswordException("Inno Setup: the password is wrong for this installer.", passwordWasSupplied: true);
    return key;
  }

  /// <summary>Before 6.4: the ARC4 cipher of a chunk, keyed with MD5 or SHA-1 of its salt and the password, the first 1000 bytes of key stream dropped.</summary>
  public static Rc4 Arc4(InnoEncryption encryption, ReadOnlySpan<byte> chunkSalt, byte[] encodedPassword) {
    byte[] material = [.. chunkSalt, .. encodedPassword];
    var rc4 = new Rc4(encryption == InnoEncryption.Arc4Sha1 ? SHA1.HashData(material) : MD5.HashData(material));
    rc4.Transform(new byte[1000]);
    return rc4;
  }
}

/// <summary>A read-only stream that decrypts another as it is read.</summary>
internal sealed class InnoDecryptingStream(Stream inner, Action<Span<byte>> transform) : Stream {
  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => false;
  public override long Length => throw new NotSupportedException();
  public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
  public override void Flush() { }
  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));
  public override int Read(Span<byte> buffer) {
    var n = inner.Read(buffer);
    transform(buffer[..n]);
    return n;
  }
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
