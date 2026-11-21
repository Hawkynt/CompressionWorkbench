using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Crypto;

namespace FileFormat.Zpaq;

/// <summary>
/// The encryption of zpaq 7.x archives (<c>-key</c>): the file starts with a 32-byte random salt,
/// everything after it is XORed with AES-256 in counter mode.
/// </summary>
/// <remarks>
/// <para>Converted from libzpaq 7.15 (Matt Mahoney, public domain): <c>stretchKey</c> and
/// <c>AES_CTR::encrypt</c>, and zpaq.cpp's <c>InputArchive</c>. The key is scrypt(SHA-256(password),
/// salt, N = 16384, r = 8, p = 1), 32 bytes, with scrypt from <see cref="Compression.Core.Crypto.Scrypt"/> (RFC 7914).
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
    return Compression.Core.Crypto.Scrypt.Derive(hashed, salt, 1 << 14, 8, 1, 32);
  }

  /// <summary>
  /// XORs <paramref name="data"/>, which lies at <paramref name="fileOffset"/> of the (multi-part)
  /// archive, with the AES-256-CTR key stream of <paramref name="key"/> and the salt's first eight
  /// bytes: encryption and decryption are the same operation.
  /// </summary>
  public static void Transform(byte[] key, ReadOnlySpan<byte> salt, long fileOffset, Span<byte> data) {
    using var aes = ManagedAes.Create();
    aes.Key = key;
    Span<byte> counter = stackalloc byte[16];
    Span<byte> stream = stackalloc byte[16];
    salt[..8].CopyTo(counter);
    var done = 0;
    while (done < data.Length) {
      var at = fileOffset + done;
      BinaryPrimitives.WriteUInt64BigEndian(counter[8..], (ulong)(at / 16));
      aes.EncryptEcb(counter, stream, PaddingMode.None);
      var skip = (int)(at % 16);
      var take = Math.Min(16 - skip, data.Length - done);
      for (var k = 0; k < take; ++k)
        data[done + k] ^= stream[skip + k];
      done += take;
    }
  }

  /// <summary>The salt of an index written for an archive with salt <paramref name="archiveSalt"/>, and back.</summary>
  public static byte[] IndexSalt(ReadOnlySpan<byte> archiveSalt) {
    var salt = archiveSalt.ToArray();
    salt[0] ^= (byte)('7' ^ 'z');
    return salt;
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
    this._aes = ManagedAes.Create();
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
