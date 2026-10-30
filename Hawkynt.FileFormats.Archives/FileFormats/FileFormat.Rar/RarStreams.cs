using System.Security.Cryptography;

namespace FileFormat.Rar;

/// <summary>
/// Read-only stream over the packed parts of one RAR entry: a file split across volumes
/// stores one part in each, and the parts join into one packed stream (encrypted files
/// included — RAR keeps one AES-CBC chain across the parts).
/// </summary>
internal sealed class RarPartsStream : Stream {
  private readonly IReadOnlyList<Stream> _volumes;
  private readonly IReadOnlyList<RarPart> _parts;
  private int _part;
  private long _inPart;
  private long _position;

  public RarPartsStream(IReadOnlyList<Stream> volumes, IReadOnlyList<RarPart> parts) {
    this._volumes = volumes;
    this._parts = parts;
    this.Length = parts.Sum(p => p.Length);
  }

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => false;
  public override long Length { get; }
  public override long Position { get => this._position; set => throw new NotSupportedException(); }

  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  public override int Read(Span<byte> buffer) {
    var total = 0;
    while (buffer.Length > 0 && this._part < this._parts.Count) {
      var part = this._parts[this._part];
      var left = part.Length - this._inPart;
      if (left <= 0) {
        ++this._part;
        this._inPart = 0;
        continue;
      }
      var volume = this._volumes[part.Volume];
      volume.Position = part.Offset + this._inPart;
      var n = volume.Read(buffer[..(int)Math.Min(buffer.Length, left)]);
      if (n <= 0)
        throw new InvalidDataException("RAR volume ends inside the packed data of a file.");
      this._inPart += n;
      this._position += n;
      total += n;
      buffer = buffer[n..];
    }
    return total;
  }

  public override void Flush() { }
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>One stretch of packed data in one volume.</summary>
internal readonly record struct RarPart(int Volume, long Offset, long Length);

/// <summary>
/// Streaming AES-CBC (no padding) over a source whose length is a multiple of 16; used for
/// RAR 3.x/4.x (AES-128) and RAR 5 (AES-256) file data.
/// </summary>
internal sealed class RarCbcDecryptStream : Stream {
  private readonly Stream _source;
  private readonly ICryptoTransform _decryptor;
  private readonly Aes _aes;
  private readonly byte[] _in = new byte[1 << 16];
  private readonly byte[] _out = new byte[1 << 16];
  private int _outPos;
  private int _outLen;
  private long _remaining;

  public RarCbcDecryptStream(Stream source, long length, byte[] key, byte[] iv) {
    this._source = source;
    this._remaining = length & ~15L;
    this._aes = Aes.Create();
    this._aes.Mode = CipherMode.CBC;
    this._aes.Padding = PaddingMode.None;
    this._decryptor = this._aes.CreateDecryptor(key, iv);
  }

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => false;
  public override long Length => throw new NotSupportedException();
  public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  public override int Read(Span<byte> buffer) {
    if (this._outPos == this._outLen) {
      if (this._remaining <= 0)
        return 0;
      var want = (int)Math.Min(this._in.Length, this._remaining);
      var got = 0;
      while (got < want) {
        var n = this._source.Read(this._in, got, want - got);
        if (n <= 0) break;
        got += n;
      }
      got &= ~15;
      if (got == 0)
        throw new InvalidDataException("Encrypted RAR data ends early.");
      this._remaining -= got;
      this._outLen = this._decryptor.TransformBlock(this._in, 0, got, this._out, 0);
      this._outPos = 0;
    }
    var take = Math.Min(buffer.Length, this._outLen - this._outPos);
    this._out.AsSpan(this._outPos, take).CopyTo(buffer);
    this._outPos += take;
    return take;
  }

  protected override void Dispose(bool disposing) {
    if (disposing) {
      this._decryptor.Dispose();
      this._aes.Dispose();
    }
    base.Dispose(disposing);
  }

  public override void Flush() { }
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Write-through stream that counts and hashes what passes, optionally discarding it.</summary>
internal sealed class RarHashingStream(Stream? inner, bool crc, bool blake, bool rar14 = false) : Stream {
  private readonly Compression.Core.Checksums.Crc32 _crc = new();
  private readonly Compression.Core.Checksums.Blake2sp? _blake = blake ? new() : null;

  public long Written { get; private set; }
  public uint Crc => this._crc.Value;
  public ushort Checksum14 { get; private set; }
  public byte[]? Blake2sp => this._blake?.Finish();

  public override bool CanRead => false;
  public override bool CanSeek => false;
  public override bool CanWrite => true;
  public override long Length => this.Written;
  public override long Position { get => this.Written; set => throw new NotSupportedException(); }

  public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

  public override void Write(ReadOnlySpan<byte> buffer) {
    if (crc) this._crc.Update(buffer);
    if (rar14) this.Checksum14 = Rar14Parser.Checksum(this.Checksum14, buffer);
    this._blake?.Update(buffer);
    inner?.Write(buffer);
    this.Written += buffer.Length;
  }

  public override void Flush() => inner?.Flush();
  public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Reads exactly <c>length</c> bytes of a source, then reports end of stream.</summary>
internal sealed class RarLimitStream(Stream source, long length) : Stream {
  private long _left = length;

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => false;
  public override long Length => throw new NotSupportedException();
  public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  public override int Read(Span<byte> buffer) {
    if (this._left <= 0) return 0;
    var n = source.Read(buffer[..(int)Math.Min(buffer.Length, this._left)]);
    this._left -= n;
    return n;
  }

  public override void Flush() { }
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
