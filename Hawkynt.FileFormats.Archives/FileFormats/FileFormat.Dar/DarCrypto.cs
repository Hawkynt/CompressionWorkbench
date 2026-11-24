using System.Buffers.Binary;
using System.Security.Cryptography;
using Compression.Core.Crypto;

namespace FileFormat.Dar;

/// <summary>
/// The key-derivation parameters a DAR version header records (flag 0x0400,
/// <c>docs/DAR-ON-DISK.md</c> section 12): the salt, the iteration count and the hash letter
/// (<c>a</c> argon2id, <c>5</c> SHA-512, <c>1</c> SHA-1, <c>m</c> MD5).
/// </summary>
internal sealed record DarKdf(byte[] Salt, ulong Iterations, char Hash);

/// <summary>
/// dar's encryption layer (section 12): the key derivation, the elastic buffers and the per-block
/// CBC encryption with ESSIV initialisation vectors.
/// </summary>
internal static class DarCrypto {
  /// <summary>dar's default clear bytes per encrypted block (its <c>-#</c> option); not recorded in the archive.</summary>
  public const int DefaultClearBlockSize = 10240;

  /// <summary>The largest elastic buffer dar draws at the start and at the end of the encrypted zone.</summary>
  public const int ElasticDraw = 51200;

  /// <summary>Argon2id memory dar asks for, in KiB.</summary>
  public const int Argon2MemoryKiB = 100;

  /// <summary>Whether <paramref name="cipher"/> is a cipher letter this implementation knows.</summary>
  public static bool IsKnownCipher(char cipher) => cipher is 's' or 'b' or 'a' or 't' or 'p' or 'c';

  /// <summary>A display name for a cipher letter.</summary>
  public static string CipherName(char cipher) => cipher switch {
    's' => "scrambling",
    'b' => "blowfish",
    'a' => "aes256",
    't' => "twofish256",
    'p' => "serpent256",
    'c' => "camellia256",
    _ => $"'{cipher}'",
  };

  /// <summary>The cipher letter for a name dar's <c>-K algo:</c> accepts.</summary>
  public static char CipherFor(string? name) => (name ?? "").Trim().ToLowerInvariant() switch {
    "" or "aes" or "aes256" => 'a',
    "bf" or "blowfish" => 'b',
    "twofish" or "twofish256" => 't',
    "serpent" or "serpent256" => 'p',
    "camellia" or "camellia256" => 'c',
    "scram" or "scrambling" => 's',
    var other => throw new NotSupportedException($"DAR encryption '{other}' is not supported (aes256, blowfish, twofish256, serpent256, camellia256, scrambling)."),
  };

  /// <summary>The KDF hash letter for a name dar's <c>--kdf-param</c> accepts.</summary>
  public static char KdfHashFor(string? name) => (name ?? "").Trim().ToLowerInvariant() switch {
    "" or "argon2" => 'a',
    "sha512" => '5',
    "sha1" => '1',
    "md5" => 'm',
    var other => throw new NotSupportedException($"DAR key derivation hash '{other}' is not supported (argon2, sha512, sha1, md5)."),
  };

  /// <summary>Bytes of key the derivation produces: 56 for Blowfish, 32 for the 256-bit ciphers.</summary>
  public static int KeyLength(char cipher) => cipher == 'b' ? 56 : 32;

  /// <summary>Bytes of salt dar draws: the cipher's nominal key length (16 for Blowfish, 32 otherwise).</summary>
  public static int SaltLength(char cipher) => cipher == 'b' ? 16 : 32;

  /// <summary>Derives the cipher key from the password through the recorded KDF.</summary>
  /// <exception cref="NotSupportedException">Unknown hash letter, or an iteration count out of range.</exception>
  public static byte[] DeriveKey(ReadOnlySpan<byte> password, char cipher, DarKdf kdf) {
    if (kdf.Iterations is 0 or > int.MaxValue)
      throw new NotSupportedException($"DAR key derivation iteration count {kdf.Iterations} is out of range.");
    var iterations = (int)kdf.Iterations;
    var length = KeyLength(cipher);
    return kdf.Hash switch {
      'a' => Argon2.Hash(Argon2.Variant.Argon2id, password, kdf.Salt, iterations, Argon2MemoryKiB, 1, length),
      '5' => Rfc2898DeriveBytes.Pbkdf2(password, kdf.Salt, iterations, HashAlgorithmName.SHA512, length),
      '1' => Rfc2898DeriveBytes.Pbkdf2(password, kdf.Salt, iterations, HashAlgorithmName.SHA1, length),
      'm' => KeyDerivation.Pbkdf2(MD5.HashData, 64, password, kdf.Salt, iterations, length),
      var h => throw new NotSupportedException($"DAR key derivation hash '{h}' is not supported."),
    };
  }

  /// <summary>
  /// A fresh salt and the key it gives. A Blowfish key libgcrypt rejects as weak is drawn again,
  /// as dar does, so that dar can open what is written.
  /// </summary>
  public static (DarKdf Kdf, byte[] Key) NewKey(ReadOnlySpan<byte> password, char cipher, char hash, ulong iterations) {
    for (var attempt = 0; ; ++attempt) {
      var kdf = new DarKdf(RandomNumberGenerator.GetBytes(SaltLength(cipher)), iterations, hash);
      var key = DeriveKey(password, cipher, kdf);
      if (cipher != 'b' || !new Blowfish(key).HasWeakKey)
        return (kdf, key);
      if (attempt == 5)
        throw new CryptographicException("No strong Blowfish key came out of six salts.");
    }
  }

  // ── Elastic buffers ─────────────────────────────────────────────────────────

  private const byte SingleMark = (byte)'X';
  private const byte LowMark = 0xFF;
  private const byte HighMark = 0xFE;
  private const int ElasticBase = 254;

  /// <summary>
  /// Fills <paramref name="buffer"/> with an elastic buffer of its length: <c>X</c> for one byte,
  /// <c>FF FE</c> for two, otherwise filler around <c>FF</c>, the length in base 254 (least
  /// significant digit first) and <c>FE</c>. Filler never holds <c>X</c>, <c>FE</c> or <c>FF</c>.
  /// </summary>
  public static void WriteElastic(Span<byte> buffer) {
    var size = buffer.Length;
    if (size == 0)
      throw new ArgumentException("An elastic buffer holds at least one byte.", nameof(buffer));
    if (size == 1) {
      buffer[0] = SingleMark;
      return;
    }
    if (size == 2) {
      buffer[0] = LowMark;
      buffer[1] = HighMark;
      return;
    }
    Span<byte> digits = stackalloc byte[8];
    var count = 0;
    for (var v = size; v != 0; v /= ElasticBase)
      digits[count++] = (byte)(v % ElasticBase);
    var at = count + 2 < size ? RandomNumberGenerator.GetInt32(size - (count + 2)) : 0;
    Filler(buffer[..at]);
    buffer[at++] = LowMark;
    digits[..count].CopyTo(buffer[at..]);
    at += count;
    buffer[at++] = HighMark;
    Filler(buffer[at..]);
  }

  private static void Filler(Span<byte> span) {
    RandomNumberGenerator.Fill(span);
    for (var i = 0; i < span.Length; ++i)
      while (span[i] is SingleMark or LowMark or HighMark)
        span[i] = (byte)RandomNumberGenerator.GetInt32(256);
  }

  /// <summary>An elastic buffer of <paramref name="size"/> bytes.</summary>
  public static byte[] Elastic(int size) {
    var buffer = new byte[size];
    WriteElastic(buffer);
    return buffer;
  }

  /// <summary>
  /// The length of the elastic buffer that ends <paramref name="data"/>, read backwards: its last
  /// mark, then the base-254 digits up to the first mark.
  /// </summary>
  /// <exception cref="InvalidDataException">No coherent elastic buffer ends the data.</exception>
  public static int ElasticSizeAtEnd(ReadOnlySpan<byte> data) {
    var pos = data.Length - 1;
    while (pos >= 0 && data[pos] is not (SingleMark or HighMark))
      --pos;
    if (pos < 0)
      throw new InvalidDataException("DAR elastic buffer has no end mark.");
    if (data[pos] == SingleMark)
      return pos == data.Length - 1 ? 1 : throw new InvalidDataException("DAR elastic buffer is incoherent.");
    long size = 0;
    var digits = 0;
    while (--pos >= 0 && data[pos] != LowMark) {
      size = size * ElasticBase + data[pos];
      if (++digits > 4)
        throw new InvalidDataException("DAR elastic buffer is too long or incoherent.");
    }
    if (pos < 0)
      throw new InvalidDataException("DAR elastic buffer has no start mark.");
    if (digits == 0)
      return 2;
    if (size < 3 || size > data.Length)
      throw new InvalidDataException("DAR elastic buffer is incoherent.");
    return (int)size;
  }

  // ── Ciphers ─────────────────────────────────────────────────────────────────

  /// <summary>The block cipher of <paramref name="cipher"/> under <paramref name="key"/>, CBC with ESSIV vectors.</summary>
  public static DarBlockCipher OpenCipher(char cipher, byte[] key) => cipher switch {
    'b' => DarBlockCipher.Create(new Blowfish(key), key),
    'a' => DarBlockCipher.Create(new AesBlockCipher(key), key),
    't' => DarBlockCipher.Create(new Twofish(key), key),
    'p' => DarBlockCipher.Create(new Serpent(key), key),
    'c' => DarBlockCipher.Create(new Camellia(key), key),
    _ => throw new NotSupportedException($"DAR cipher {CipherName(cipher)} is not a block cipher."),
  };
}

/// <summary>
/// One of dar's block ciphers in its archive mode: each clear block <c>n</c> (counting from 0 at
/// the start of the encrypted zone) is padded with an elastic buffer to the next whole cipher block
/// beyond it, then CBC-encrypted under an initialisation vector that is block number <c>n</c>,
/// big-endian in one cipher block, encrypted with the ESSIV key: AES-256 under SHA-256 of the key,
/// or for Blowfish, Blowfish under SHA-1 of the key.
/// </summary>
internal sealed class DarBlockCipher {
  private readonly Action<Span<byte>> _encrypt;
  private readonly Action<Span<byte>> _decrypt;
  private readonly Action<Span<byte>> _essiv;

  private DarBlockCipher(int blockSize, Action<Span<byte>> encrypt, Action<Span<byte>> decrypt, Action<Span<byte>> essiv) {
    this.BlockSize = blockSize;
    this._encrypt = encrypt;
    this._decrypt = decrypt;
    this._essiv = essiv;
  }

  /// <summary>The cipher's block size: 8 for Blowfish, 16 otherwise.</summary>
  public int BlockSize { get; }

  public static DarBlockCipher Create(Blowfish cipher, byte[] key) {
    var essiv = new Blowfish(SHA1.HashData(key));
    return new(8, cipher.Encrypt, cipher.Decrypt, essiv.Encrypt);
  }

  public static DarBlockCipher Create(AesBlockCipher cipher, byte[] key) => new(16, b => cipher.Encrypt(b), b => cipher.Decrypt(b), AesEssiv(key));
  public static DarBlockCipher Create(Twofish cipher, byte[] key) => new(16, cipher.Encrypt, cipher.Decrypt, AesEssiv(key));
  public static DarBlockCipher Create(Serpent cipher, byte[] key) => new(16, cipher.Encrypt, cipher.Decrypt, AesEssiv(key));
  public static DarBlockCipher Create(Camellia cipher, byte[] key) => new(16, cipher.Encrypt, cipher.Decrypt, AesEssiv(key));

  private static Action<Span<byte>> AesEssiv(byte[] key) {
    var essiv = new AesBlockCipher(SHA256.HashData(key));
    return b => essiv.Encrypt(b);
  }

  /// <summary>
  /// The ESSIV cipher of a key the way dar sets up its self-test: Blowfish under SHA-1 of
  /// <paramref name="key"/>, used directly (no key derivation).
  /// </summary>
  internal static DarBlockCipher BlowfishForTest(byte[] key) => Create(new Blowfish(key), key);

  /// <summary>Bytes an encrypted block takes for <paramref name="clearSize"/> clear bytes.</summary>
  public int EncryptedSize(int clearSize) => (clearSize / this.BlockSize + 1) * this.BlockSize;

  /// <summary>The initialisation vector of block <paramref name="number"/>.</summary>
  public void InitialisationVector(ulong number, Span<byte> iv) {
    iv.Clear();
    BinaryPrimitives.WriteUInt64BigEndian(iv[^8..], number);
    this._essiv(iv);
  }

  /// <summary>Encrypts clear block <paramref name="number"/> into <paramref name="output"/>, which must hold <see cref="EncryptedSize"/> bytes.</summary>
  public void Encrypt(ulong number, ReadOnlySpan<byte> clear, Span<byte> output) {
    var size = this.EncryptedSize(clear.Length);
    clear.CopyTo(output);
    DarCrypto.WriteElastic(output[clear.Length..size]);
    Span<byte> chain = stackalloc byte[this.BlockSize];
    this.InitialisationVector(number, chain);
    for (var at = 0; at < size; at += this.BlockSize) {
      var block = output.Slice(at, this.BlockSize);
      for (var i = 0; i < block.Length; ++i)
        block[i] ^= chain[i];
      this._encrypt(block);
      block.CopyTo(chain);
    }
  }

  /// <summary>Decrypts block <paramref name="number"/> in place and returns how many clear bytes it holds.</summary>
  /// <exception cref="InvalidDataException">Not a whole number of cipher blocks, or no elastic buffer ends it (wrong key or damage).</exception>
  public int Decrypt(ulong number, Span<byte> data) {
    if (data.Length == 0 || data.Length % this.BlockSize != 0)
      throw new InvalidDataException("DAR encrypted block is not a whole number of cipher blocks.");
    Span<byte> chain = stackalloc byte[this.BlockSize];
    Span<byte> next = stackalloc byte[this.BlockSize];
    this.InitialisationVector(number, chain);
    for (var at = 0; at < data.Length; at += this.BlockSize) {
      var block = data.Slice(at, this.BlockSize);
      block.CopyTo(next);
      this._decrypt(block);
      for (var i = 0; i < block.Length; ++i)
        block[i] ^= chain[i];
      next.CopyTo(chain);
    }
    return data.Length - DarCrypto.ElasticSizeAtEnd(data);
  }
}

/// <summary>
/// The clear stream of a block-encrypted zone, read-only and seekable: clear position <c>p</c>
/// lies in block <c>p / clear block size</c>, which starts at that many encrypted block sizes past
/// the zone's start.
/// </summary>
internal sealed class DarDecryptingStream : Stream {
  private readonly Stream _source;
  private readonly long _start;
  private readonly DarBlockCipher _cipher;
  private readonly int _clearBlock;
  private readonly int _encryptedBlock;
  private readonly long _blocks;
  private readonly int _lastEncrypted;
  private readonly byte[] _buffer;
  private long _cached = -1;
  private int _cachedLength;
  private long _position;

  /// <param name="source">The archive level.</param>
  /// <param name="start">Where the encrypted zone starts (the initial offset).</param>
  /// <param name="end">Where it ends (the version trailer).</param>
  /// <param name="cipher">The keyed cipher.</param>
  /// <param name="clearBlock">Clear bytes per block (dar's <c>-#</c>, 10240 by default).</param>
  /// <exception cref="InvalidDataException">The zone does not divide into encrypted blocks, or its last block does not decrypt.</exception>
  public DarDecryptingStream(Stream source, long start, long end, DarBlockCipher cipher, int clearBlock) {
    this._source = source;
    this._start = start;
    this._cipher = cipher;
    this._clearBlock = clearBlock;
    this._encryptedBlock = cipher.EncryptedSize(clearBlock);
    this._buffer = new byte[this._encryptedBlock];
    var zone = end - start;
    if (zone <= 0)
      throw new InvalidDataException("The DAR encrypted zone is empty.");
    this._blocks = (zone + this._encryptedBlock - 1) / this._encryptedBlock;
    this._lastEncrypted = (int)(zone - (this._blocks - 1) * this._encryptedBlock);
    this.Length = (this._blocks - 1) * clearBlock + this.Load(this._blocks - 1);
  }

  private int Load(long block) {
    if (block == this._cached)
      return this._cachedLength;
    var size = block == this._blocks - 1 ? this._lastEncrypted : this._encryptedBlock;
    this._source.Position = this._start + block * this._encryptedBlock;
    this._source.ReadExactly(this._buffer, 0, size);
    this._cached = -1;
    var clear = this._cipher.Decrypt((ulong)block, this._buffer.AsSpan(0, size));
    if (clear > this._clearBlock || block < this._blocks - 1 && clear != this._clearBlock)
      throw new InvalidDataException($"DAR encrypted block {block} does not decrypt to a whole clear block (wrong password or damage).");
    this._cached = block;
    this._cachedLength = clear;
    return clear;
  }

  public override bool CanRead => true;
  public override bool CanSeek => true;
  public override bool CanWrite => false;
  public override long Length { get; }

  public override long Position {
    get => this._position;
    set => this._position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
  }

  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  public override int Read(Span<byte> buffer) {
    var total = 0;
    while (!buffer.IsEmpty && this._position < this.Length) {
      var block = this._position / this._clearBlock;
      var within = (int)(this._position % this._clearBlock);
      var available = this.Load(block) - within;
      var n = Math.Min(available, buffer.Length);
      this._buffer.AsSpan(within, n).CopyTo(buffer);
      buffer = buffer[n..];
      this._position += n;
      total += n;
    }
    return total;
  }

  public override long Seek(long offset, SeekOrigin origin) => this.Position = origin switch {
    SeekOrigin.Begin => offset,
    SeekOrigin.Current => this._position + offset,
    _ => this.Length + offset,
  };

  public override void Flush() { }
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// dar's scrambling, read back: every byte from the initial offset to the version trailer had
/// password byte <c>position mod password length</c> added to it, the position counted on the
/// archive level. Positions keep their archive-level meaning.
/// </summary>
internal sealed class DarUnscramblingStream(Stream source, long start, long end, byte[] password) : Stream {
  private long _position;

  public override bool CanRead => true;
  public override bool CanSeek => true;
  public override bool CanWrite => false;
  public override long Length => end;

  public override long Position {
    get => this._position;
    set => this._position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
  }

  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  public override int Read(Span<byte> buffer) {
    var n = (int)Math.Max(0, Math.Min(buffer.Length, end - this._position));
    if (n == 0)
      return 0;
    source.Position = this._position;
    source.ReadExactly(buffer[..n]);
    for (var i = 0; i < n; ++i) {
      var at = this._position + i;
      if (at >= start)
        buffer[i] -= password[(int)(at % password.Length)];
    }
    this._position += n;
    return n;
  }

  public override long Seek(long offset, SeekOrigin origin) => this.Position = origin switch {
    SeekOrigin.Begin => offset,
    SeekOrigin.Current => this._position + offset,
    _ => end + offset,
  };

  public override void Flush() { }
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
