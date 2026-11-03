using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FileFormat.Zpaq;

/// <summary>
/// The encryption of zpaq 7.x archives (<c>-key</c>): the file starts with a 32-byte random salt,
/// everything after it is XORed with AES-256 in counter mode.
/// </summary>
/// <remarks>
/// <para>Converted from libzpaq 7.15 (Matt Mahoney, public domain): <c>stretchKey</c>, <c>scrypt</c>,
/// <c>smix</c>, <c>blockmix</c>, <c>salsa8</c> and <c>AES_CTR::encrypt</c>, and zpaq.cpp's
/// <c>InputArchive</c>. The key is scrypt(SHA-256(password), salt, N = 16384, r = 8, p = 1),
/// 32 bytes; scrypt is RFC 7914, PBKDF2-HMAC-SHA256 with one iteration around the Salsa20/8 mix.
/// Counter block <c>i</c> (big-endian) is the first eight salt bytes followed by the 64-bit block
/// number <c>offset / 16</c>, where the offset counts from the start of the file, salt included.</para>
/// <para>An index written with <c>-index</c> uses the first part's salt with its first byte XORed
/// with <c>'7' ^ 'z'</c>; each file carries its own salt, so reading needs no special case.</para>
/// </remarks>
internal static class ZpaqCrypto {
  /// <summary>Length of the salt at the start of an encrypted archive or index.</summary>
  public const int SaltSize = 32;

  /// <summary>Derives the AES-256 key from a password and an archive's salt.</summary>
  public static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt) {
    ArgumentNullException.ThrowIfNull(password);
    // zpaq hashes the -key argument's bytes, which are UTF-8 on every platform it runs on.
    var hashed = SHA256.HashData(Encoding.UTF8.GetBytes(password));
    return Scrypt(hashed, salt, 1 << 14, 8, 1, 32);
  }

  /// <summary>scrypt (RFC 7914): PBKDF2-HMAC-SHA256 (one iteration), ROMix with Salsa20/8, PBKDF2 again.</summary>
  public static byte[] Scrypt(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int n, int r, int p, int length) {
    if (n < 2 || (n & (n - 1)) != 0)
      throw new ArgumentOutOfRangeException(nameof(n), "N must be a power of two.");
    ArgumentOutOfRangeException.ThrowIfLessThan(r, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(p, 1);
    var b = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, p * 128 * r);
    for (var i = 0; i < p; ++i)
      SMix(b.AsSpan(i * 128 * r, 128 * r), r, n);
    return Rfc2898DeriveBytes.Pbkdf2(password, b, 1, HashAlgorithmName.SHA256, length);
  }

  private static void SMix(Span<byte> b, int r, int n) {
    var words = 32 * r;
    var x = new uint[words];
    var v = new uint[words * n];
    var y = new uint[words];
    for (var i = 0; i < words; ++i)
      x[i] = BinaryPrimitives.ReadUInt32LittleEndian(b[(i * 4)..]);
    for (var i = 0; i < n; ++i) {
      x.CopyTo(v, i * words);
      BlockMix(x, y, r);
    }
    for (var i = 0; i < n; ++i) {
      var j = (int)(x[(2 * r - 1) * 16] & (uint)(n - 1));
      for (var k = 0; k < words; ++k)
        x[k] ^= v[j * words + k];
      BlockMix(x, y, r);
    }
    for (var i = 0; i < words; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(b[(i * 4)..], x[i]);
  }

  /// <summary>BlockMix-Salsa20/8: even output blocks first, then the odd ones.</summary>
  private static void BlockMix(uint[] b, uint[] y, int r) {
    Span<uint> x = stackalloc uint[16];
    b.AsSpan((2 * r - 1) * 16, 16).CopyTo(x);
    for (var i = 0; i < 2 * r; ++i) {
      for (var j = 0; j < 16; ++j)
        x[j] ^= b[i * 16 + j];
      Salsa8(x);
      x.CopyTo(y.AsSpan(i * 16, 16));
    }
    for (var i = 0; i < r; ++i)
      y.AsSpan(i * 32, 16).CopyTo(b.AsSpan(i * 16, 16));
    for (var i = 0; i < r; ++i)
      y.AsSpan(i * 32 + 16, 16).CopyTo(b.AsSpan((i + r) * 16, 16));
  }

  private static void Salsa8(Span<uint> b) {
    Span<uint> x = stackalloc uint[16];
    b.CopyTo(x);
    for (var i = 0; i < 4; ++i) {
      x[4] ^= uint.RotateLeft(x[0] + x[12], 7); x[8] ^= uint.RotateLeft(x[4] + x[0], 9);
      x[12] ^= uint.RotateLeft(x[8] + x[4], 13); x[0] ^= uint.RotateLeft(x[12] + x[8], 18);
      x[9] ^= uint.RotateLeft(x[5] + x[1], 7); x[13] ^= uint.RotateLeft(x[9] + x[5], 9);
      x[1] ^= uint.RotateLeft(x[13] + x[9], 13); x[5] ^= uint.RotateLeft(x[1] + x[13], 18);
      x[14] ^= uint.RotateLeft(x[10] + x[6], 7); x[2] ^= uint.RotateLeft(x[14] + x[10], 9);
      x[6] ^= uint.RotateLeft(x[2] + x[14], 13); x[10] ^= uint.RotateLeft(x[6] + x[2], 18);
      x[3] ^= uint.RotateLeft(x[15] + x[11], 7); x[7] ^= uint.RotateLeft(x[3] + x[15], 9);
      x[11] ^= uint.RotateLeft(x[7] + x[3], 13); x[15] ^= uint.RotateLeft(x[11] + x[7], 18);
      x[1] ^= uint.RotateLeft(x[0] + x[3], 7); x[2] ^= uint.RotateLeft(x[1] + x[0], 9);
      x[3] ^= uint.RotateLeft(x[2] + x[1], 13); x[0] ^= uint.RotateLeft(x[3] + x[2], 18);
      x[6] ^= uint.RotateLeft(x[5] + x[4], 7); x[7] ^= uint.RotateLeft(x[6] + x[5], 9);
      x[4] ^= uint.RotateLeft(x[7] + x[6], 13); x[5] ^= uint.RotateLeft(x[4] + x[7], 18);
      x[11] ^= uint.RotateLeft(x[10] + x[9], 7); x[8] ^= uint.RotateLeft(x[11] + x[10], 9);
      x[9] ^= uint.RotateLeft(x[8] + x[11], 13); x[10] ^= uint.RotateLeft(x[9] + x[8], 18);
      x[12] ^= uint.RotateLeft(x[15] + x[14], 7); x[13] ^= uint.RotateLeft(x[12] + x[15], 9);
      x[14] ^= uint.RotateLeft(x[13] + x[12], 13); x[15] ^= uint.RotateLeft(x[14] + x[13], 18);
    }
    for (var i = 0; i < 16; ++i)
      b[i] += x[i];
  }
}

/// <summary>
/// A read-only view of an encrypted zpaq archive with the salt removed and the AES-256-CTR key
/// stream taken off: position 0 is the first archive byte, file offset 32.
/// </summary>
internal sealed class ZpaqDecryptingStream : Stream {
  private readonly Stream _inner;
  private readonly bool _leaveOpen;
  private readonly Aes _aes;
  private readonly byte[] _iv = new byte[8];
  private long _position;

  public ZpaqDecryptingStream(Stream inner, string password, bool leaveOpen) {
    if (!inner.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(inner));
    this._inner = inner;
    this._leaveOpen = leaveOpen;
    var salt = new byte[ZpaqCrypto.SaltSize];
    inner.Position = 0;
    if (inner.ReadAtLeast(salt, salt.Length, throwOnEndOfStream: false) < salt.Length)
      throw new InvalidDataException("ZPAQ: an encrypted archive is shorter than its salt.");
    salt.AsSpan(0, 8).CopyTo(this._iv);
    this._aes = Aes.Create();
    this._aes.Key = ZpaqCrypto.DeriveKey(password, salt);
  }

  public override bool CanRead => true;
  public override bool CanSeek => true;
  public override bool CanWrite => false;
  public override long Length => Math.Max(0, this._inner.Length - ZpaqCrypto.SaltSize);
  public override long Position { get => this._position; set => this._position = value; }
  public override void Flush() { }
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  public override long Seek(long offset, SeekOrigin origin) => this._position = origin switch {
    SeekOrigin.Begin => offset,
    SeekOrigin.Current => this._position + offset,
    _ => this.Length + offset,
  };

  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  public override int Read(Span<byte> buffer) {
    var physical = this._position + ZpaqCrypto.SaltSize;
    this._inner.Position = physical;
    var n = this._inner.Read(buffer);
    if (n <= 0)
      return 0;
    // XOR with AES(iv || block number), block number = file offset / 16 (big-endian).
    Span<byte> counter = stackalloc byte[16];
    Span<byte> stream = stackalloc byte[16];
    this._iv.CopyTo(counter);
    var done = 0;
    while (done < n) {
      var at = physical + done;
      BinaryPrimitives.WriteUInt64BigEndian(counter[8..], (ulong)(at / 16));
      this._aes.EncryptEcb(counter, stream, PaddingMode.None);
      var skip = (int)(at % 16);
      var take = Math.Min(16 - skip, n - done);
      for (var k = 0; k < take; ++k)
        buffer[done + k] ^= stream[skip + k];
      done += take;
    }
    this._position += n;
    return n;
  }

  protected override void Dispose(bool disposing) {
    if (disposing) {
      this._aes.Dispose();
      if (!this._leaveOpen)
        this._inner.Dispose();
    }
    base.Dispose(disposing);
  }
}

/// <summary>A read-only, seekable concatenation of the parts of a multi-part archive.</summary>
/// <param name="parts">The parts, in order.</param>
/// <param name="owned">The parts this view disposes with itself (those it opened).</param>
internal sealed class ZpaqConcatStream(IReadOnlyList<Stream> parts, IReadOnlyList<Stream> owned) : Stream {
  private readonly long[] _starts = Starts(parts);
  private long _position;

  private static long[] Starts(IReadOnlyList<Stream> parts) {
    var starts = new long[parts.Count + 1];
    for (var i = 0; i < parts.Count; ++i)
      starts[i + 1] = starts[i] + parts[i].Length;
    return starts;
  }

  public override bool CanRead => true;
  public override bool CanSeek => true;
  public override bool CanWrite => false;
  public override long Length => this._starts[^1];
  public override long Position { get => this._position; set => this._position = value; }
  public override void Flush() { }
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  public override long Seek(long offset, SeekOrigin origin) => this._position = origin switch {
    SeekOrigin.Begin => offset,
    SeekOrigin.Current => this._position + offset,
    _ => this.Length + offset,
  };

  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  public override int Read(Span<byte> buffer) {
    if (this._position >= this.Length || buffer.IsEmpty)
      return 0;
    var part = Array.BinarySearch(this._starts, this._position);
    if (part < 0)
      part = ~part - 1;
    while (part < parts.Count - 1 && this._starts[part + 1] <= this._position)
      ++part;
    var inner = parts[part];
    inner.Position = this._position - this._starts[part];
    var n = inner.Read(buffer[..(int)Math.Min(buffer.Length, this._starts[part + 1] - this._position)]);
    this._position += n;
    return n;
  }

  protected override void Dispose(bool disposing) {
    if (disposing)
      foreach (var p in owned)
        p.Dispose();
    base.Dispose(disposing);
  }
}
