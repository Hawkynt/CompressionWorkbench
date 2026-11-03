using Compression.Core.Dictionary.Lz4;
using Compression.Core.Dictionary.Lzo;
using FileFormat.Zlib;

namespace FileFormat.Dar;

/// <summary>
/// Read-only, forward-only streams that peel one layer each off a file's stored data
/// (<c>docs/DAR-ON-DISK.md</c>, sections 6 to 8), so a member of any size is extracted in constant
/// memory: <see cref="Range"/> or <see cref="Unescape"/> (the stored bytes), <see cref="Blocks"/>
/// (dar's block framing) and <see cref="Unsparse"/> (hole records).
/// </summary>
internal static class DarDataStreams {
  private const int Window = 64 * 1024;

  /// <summary>Base for the forward-only readers below.</summary>
  internal abstract class ReadOnlyForward : Stream {
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));
    public abstract override int Read(Span<byte> buffer);
  }

  /// <summary><paramref name="length"/> bytes of <paramref name="source"/> from <paramref name="offset"/>.</summary>
  internal sealed class Range(Stream source, long offset, long length) : ReadOnlyForward {
    private long _done;

    public override int Read(Span<byte> buffer) {
      var want = (int)Math.Min(buffer.Length, length - this._done);
      if (want <= 0)
        return 0;
      source.Position = offset + this._done;
      var read = source.Read(buffer[..want]);
      if (read <= 0)
        throw new InvalidDataException("DAR member data runs past the end of the archive.");
      this._done += read;
      return read;
    }
  }

  /// <summary>
  /// The escape layer removed (section 6): from <paramref name="offset"/>, a prefix followed by
  /// <c>'X'</c> is the prefix as data and any other mark ends the data. Stops after
  /// <paramref name="logicalLength"/> data bytes (or at a mark when it is negative), and never reads
  /// at or beyond <paramref name="limit"/>.
  /// </summary>
  internal sealed class Unescape(Stream source, long offset, long logicalLength, long limit) : ReadOnlyForward {
    private readonly byte[] _window = new byte[Window + 8];
    private int _start;
    private int _end;
    private long _next = offset;
    private long _produced;
    private bool _atMark;
    private bool _sourceDone;
    private int _pendingPrefix = -1;

    public override int Read(Span<byte> buffer) {
      var written = 0;
      var prefix = DarPrimitives.EscapePrefix;
      while (written < buffer.Length && (logicalLength < 0 || this._produced < logicalLength)) {
        if (this._pendingPrefix >= 0) {
          // An escaped prefix being handed out; it may straddle the caller's buffers.
          var n0 = Math.Min(buffer.Length - written, prefix.Length - this._pendingPrefix);
          if (logicalLength >= 0)
            n0 = (int)Math.Min(n0, logicalLength - this._produced);
          prefix.Slice(this._pendingPrefix, n0).CopyTo(buffer[written..]);
          written += n0;
          this._produced += n0;
          this._pendingPrefix += n0;
          if (this._pendingPrefix == prefix.Length)
            this._pendingPrefix = -1;
          continue;
        }
        if (this._atMark)
          break;
        if (this._end - this._start < 6 && !this._sourceDone)
          this.Fill();
        var available = this._end - this._start;
        if (available == 0)
          break;
        var run = this._window.AsSpan(this._start, available);
        if (run[0] == prefix[0] && available >= 5 && run[..5].SequenceEqual(prefix)) {
          if (available < 6)
            throw new InvalidDataException("DAR data ends inside an escape mark.");
          if (run[5] != DarPrimitives.NotAMark) {
            this._atMark = true;
            break;
          }
          this._start += 6;
          this._pendingPrefix = 0;
          continue;
        }
        // Plain bytes up to the next candidate prefix. Fill keeps six bytes ahead unless the
        // source has ended, so a candidate at the front is always classified above.
        var stop = run[1..].IndexOf(prefix[0]);
        var n = stop < 0 ? available : stop + 1;
        n = Math.Min(n, buffer.Length - written);
        if (logicalLength >= 0)
          n = (int)Math.Min(n, logicalLength - this._produced);
        run[..n].CopyTo(buffer[written..]);
        written += n;
        this._produced += n;
        this._start += n;
      }
      if (written == 0 && buffer.Length > 0 && logicalLength >= 0 && this._produced < logicalLength)
        throw new InvalidDataException($"DAR member data ends after {this._produced} of {logicalLength} bytes.");
      return written;
    }

    private void Fill() {
      if (this._start > 0) {
        Array.Copy(this._window, this._start, this._window, 0, this._end - this._start);
        this._end -= this._start;
        this._start = 0;
      }
      while (this._end < this._window.Length && !this._sourceDone) {
        var want = (int)Math.Min(this._window.Length - this._end, limit - this._next);
        if (want <= 0) {
          this._sourceDone = true;
          break;
        }
        source.Position = this._next;
        var read = source.Read(this._window, this._end, want);
        if (read <= 0) {
          this._sourceDone = true;
          break;
        }
        this._next += read;
        this._end += read;
        if (this._end - this._start >= 6)
          break;
      }
    }
  }

  /// <summary>
  /// dar's block framing (section 7): type <c>01</c>, an infinint size and one independently
  /// compressed block of at most <paramref name="clearBlockSize"/> clear bytes; <c>02 0</c> ends it.
  /// </summary>
  internal sealed class Blocks(Stream framed, char algorithm, int clearBlockSize) : ReadOnlyForward {
    private byte[] _block = [];
    private int _at;
    private bool _done;

    public override int Read(Span<byte> buffer) {
      while (this._at == this._block.Length) {
        if (this._done)
          return 0;
        this.NextBlock();
      }
      var n = Math.Min(buffer.Length, this._block.Length - this._at);
      this._block.AsSpan(this._at, n).CopyTo(buffer);
      this._at += n;
      return n;
    }

    private void NextBlock() {
      var type = framed.ReadByte();
      if (type < 0)
        throw new InvalidDataException("DAR compressed blocks end without an EOF block.");
      var size = ReadInfinint(framed);
      if (type == 2) {
        if (size != 0)
          throw new InvalidDataException("DAR EOF block carries a size.");
        this._done = true;
        this._block = [];
        this._at = 0;
        return;
      }
      if (type != 1 || size == 0 || size > (ulong)Math.Max(clearBlockSize * 2L + 1024, 1 << 20))
        throw new InvalidDataException($"Malformed DAR compressed block header (type {type}, size {size}).");
      var compressed = new byte[size];
      framed.ReadExactly(compressed);
      this._block = algorithm switch {
        'z' => ZlibStream.Decompress(compressed),
        'q' => Lz4Block(compressed, clearBlockSize),
        'l' or 'j' or 'k' => Lzo1xDecompressor.DecompressUpTo(compressed, clearBlockSize),
        _ => DarArchive.DecompressStreamToArray(algorithm, compressed),
      };
      if (this._block.Length > clearBlockSize)
        throw new InvalidDataException("DAR compressed block decodes to more than the block size.");
      this._at = 0;
    }

    private static byte[] Lz4Block(ReadOnlySpan<byte> block, int ceiling) {
      var buffer = new byte[ceiling];
      var n = Lz4BlockDecompressor.Decompress(block, buffer);
      return n == ceiling ? buffer : buffer.AsSpan(0, n).ToArray();
    }
  }

  /// <summary>
  /// Hole records expanded (section 8): a sparse prefix followed by <c>'F'</c> and an infinint is
  /// that many zero bytes, followed by <c>'X'</c> it is the prefix itself as data. The output must
  /// come to exactly <paramref name="expectedSize"/> bytes.
  /// </summary>
  internal sealed class Unsparse(Stream inner, ulong expectedSize) : ReadOnlyForward {
    private readonly byte[] _window = new byte[Window + 64];
    private int _start;
    private int _end;
    private bool _innerDone;
    private ulong _produced;
    private ulong _zeros;
    private int _pendingPrefix = -1; // bytes of an escaped prefix still to hand out

    public override int Read(Span<byte> buffer) {
      var written = 0;
      var prefix = DarPrimitives.SparsePrefix;
      while (written < buffer.Length) {
        if (this._zeros > 0) {
          var n = (int)Math.Min((ulong)(buffer.Length - written), this._zeros);
          buffer.Slice(written, n).Clear();
          this.Count(n);
          written += n;
          this._zeros -= (ulong)n;
          continue;
        }
        if (this._pendingPrefix >= 0) {
          var n = Math.Min(buffer.Length - written, prefix.Length - this._pendingPrefix);
          prefix.Slice(this._pendingPrefix, n).CopyTo(buffer[written..]);
          this.Count(n);
          written += n;
          this._pendingPrefix += n;
          if (this._pendingPrefix == prefix.Length)
            this._pendingPrefix = -1;
          continue;
        }
        if (this._end - this._start < 64 && !this._innerDone)
          this.Fill();
        var available = this._end - this._start;
        if (available == 0)
          break;
        var span = this._window.AsSpan(this._start, available);
        if (span[0] == prefix[0] && available >= 6 && span[..5].SequenceEqual(prefix)) {
          var type = span[5];
          if (type == DarPrimitives.NotAMark) {
            this._start += 6;
            this._pendingPrefix = 0;
            continue;
          }
          if (type != DarPrimitives.HoleMark)
            throw new InvalidDataException($"Unknown DAR sparse record type 0x{type:X2}.");
          var p = 6;
          this._zeros = DarPrimitives.ReadInfinint(span, ref p);
          if (this._zeros > expectedSize - this._produced)
            throw new InvalidDataException("DAR hole runs past the recorded file size.");
          this._start += p;
          continue;
        }
        // Fill keeps 64 bytes ahead unless the input has ended, enough for any record.
        var stop = span[1..].IndexOf(prefix[0]);
        var take = stop < 0 ? available : stop + 1;
        take = Math.Min(take, buffer.Length - written);
        span[..take].CopyTo(buffer[written..]);
        this.Count(take);
        written += take;
        this._start += take;
      }
      if (written == 0 && buffer.Length > 0 && this._produced != expectedSize)
        throw new InvalidDataException($"DAR sparse data expands to {this._produced} bytes, the catalogue says {expectedSize}.");
      return written;
    }

    private void Count(int n) {
      this._produced += (ulong)n;
      if (this._produced > expectedSize)
        throw new InvalidDataException("DAR sparse data runs past the recorded file size.");
    }

    private void Fill() {
      if (this._start > 0) {
        Array.Copy(this._window, this._start, this._window, 0, this._end - this._start);
        this._end -= this._start;
        this._start = 0;
      }
      while (this._end < this._window.Length) {
        var read = inner.Read(this._window, this._end, this._window.Length - this._end);
        if (read <= 0) {
          this._innerDone = true;
          break;
        }
        this._end += read;
        if (this._end >= 64)
          break;
      }
    }
  }

  /// <summary>
  /// A forward-only stream made to look seekable over a short history window, for decoders that
  /// step back a byte or measure what they consumed through <see cref="Stream.Position"/>.
  /// </summary>
  internal sealed class SeekBack(Stream inner) : Stream {
    private const int History = 4096;
    private readonly byte[] _buffer = new byte[History + Window];
    private long _bufferStart; // absolute offset of _buffer[0]
    private int _filled;
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position {
      get => this._position;
      set {
        if (value < this._bufferStart || value > this._bufferStart + this._filled)
          throw new NotSupportedException("DAR data can only step back a little.");
        this._position = value;
      }
    }

    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) {
      if (buffer.IsEmpty)
        return 0;
      var at = (int)(this._position - this._bufferStart);
      if (at == this._filled) {
        // Keep the tail as history, refill behind it.
        var keep = Math.Min(this._filled, History);
        Array.Copy(this._buffer, this._filled - keep, this._buffer, 0, keep);
        this._bufferStart += this._filled - keep;
        this._filled = keep;
        var read = inner.Read(this._buffer, keep, this._buffer.Length - keep);
        if (read <= 0)
          return 0;
        this._filled += read;
        at = keep;
      }
      var n = Math.Min(buffer.Length, this._filled - at);
      this._buffer.AsSpan(at, n).CopyTo(buffer);
      this._position += n;
      return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => this.Position = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._position + offset,
      _ => throw new NotSupportedException(),
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  /// <summary>Reads one infinint (section 2.1) from a stream.</summary>
  internal static ulong ReadInfinint(Stream s) {
    var zeros = 0;
    int b;
    while ((b = s.ReadByte()) == 0)
      if (++zeros > 1)
        throw new InvalidDataException("DAR infinint is wider than 64 bits.");
    if (b < 0 || System.Numerics.BitOperations.PopCount((uint)b) != 1)
      throw new InvalidDataException("Malformed or truncated DAR infinint.");
    var groups = zeros * 8 + System.Numerics.BitOperations.LeadingZeroCount((uint)b) - 24 + 1;
    ulong value = 0;
    for (var i = 0; i < groups * 4; ++i) {
      var v = s.ReadByte();
      if (v < 0)
        throw new InvalidDataException("Truncated DAR infinint.");
      if ((value >> 56) != 0)
        throw new InvalidDataException("DAR infinint is wider than 64 bits.");
      value = (value << 8) | (uint)v;
    }
    return value;
  }
}
