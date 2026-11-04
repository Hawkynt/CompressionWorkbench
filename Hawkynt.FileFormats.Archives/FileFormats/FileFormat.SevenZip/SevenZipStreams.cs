using System.Runtime.ExceptionServices;

namespace FileFormat.SevenZip;

/// <summary>
/// A read-only window onto one packed stream of a 7z archive. Several windows over one archive
/// stream may be read concurrently (a BCJ2 folder reads four packed streams at once); each keeps
/// its own position and seeks the shared stream under <paramref name="gate"/> before every read.
/// </summary>
internal sealed class SevenZipWindowStream(Stream archive, object gate, long start, long length) : Stream {
  private long _position;

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => false;
  public override long Length => length;
  public override long Position { get => this._position; set => throw new NotSupportedException(); }

  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  public override int Read(Span<byte> buffer) {
    var remaining = length - this._position;
    if (remaining <= 0 || buffer.IsEmpty)
      return 0;
    if (buffer.Length > remaining)
      buffer = buffer[..(int)remaining];

    int read;
    lock (gate) {
      archive.Position = start + this._position;
      read = archive.Read(buffer);
    }
    if (read <= 0)
      throw new EndOfStreamException("7z: the archive ends inside a packed stream (truncated file, or a missing volume).");
    this._position += read;
    return read;
  }

  public override void Flush() { }
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// An in-place, length-preserving stream transform a 7z filter coder applies — branch
/// converters, Delta, byte swapping, AES-CBC decryption.
/// </summary>
internal interface ISevenZipFilter {
  /// <summary>
  /// Transforms <paramref name="data"/> in place and returns how many leading bytes are final;
  /// the rest is presented again with more input. With <paramref name="final"/> set nothing more
  /// follows and every byte must be returned as final (an undecidable tail passes unchanged).
  /// </summary>
  int Process(Span<byte> data, bool final);
}

/// <summary>Pulls through an <see cref="ISevenZipFilter"/>: reads its input, hands out the filtered bytes.</summary>
internal sealed class SevenZipFilterReadStream(Stream input, ISevenZipFilter filter, long length) : Stream {
  private readonly byte[] _buffer = new byte[1 << 16];
  private int _ready;      // filtered bytes at the start of the buffer not yet handed out start at _readPos
  private int _readPos;
  private int _filled;     // bytes in the buffer (filtered ones first)
  private bool _inputEnded;
  private long _produced;

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => false;
  public override long Length => length;
  public override long Position { get => this._produced; set => throw new NotSupportedException(); }

  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  public override int Read(Span<byte> destination) {
    if (destination.IsEmpty || this._produced >= length)
      return 0;

    while (this._readPos == this._ready) {
      if (this._inputEnded && this._filled == this._ready)
        throw new EndOfStreamException("7z: a filter's input ended before its declared size.");
      this.Refill();
    }

    var n = Math.Min(Math.Min(destination.Length, this._ready - this._readPos), length - this._produced);
    this._buffer.AsSpan(this._readPos, (int)n).CopyTo(destination);
    this._readPos += (int)n;
    this._produced += n;
    return (int)n;
  }

  private void Refill() {
    // Move the not-yet-final tail to the front and read behind it.
    var tail = this._filled - this._ready;
    if (tail > 0 && this._ready > 0)
      Buffer.BlockCopy(this._buffer, this._ready, this._buffer, 0, tail);
    this._filled = tail;
    this._ready = 0;
    this._readPos = 0;

    while (!this._inputEnded && this._filled < this._buffer.Length) {
      var read = input.Read(this._buffer, this._filled, this._buffer.Length - this._filled);
      if (read <= 0) {
        this._inputEnded = true;
        break;
      }
      this._filled += read;
      if (this._filled >= 4096)
        break;
    }

    this._ready = filter.Process(this._buffer.AsSpan(0, this._filled), this._inputEnded);
  }

  public override void Flush() { }
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Pushes through an <see cref="ISevenZipFilter"/>: accepts the filter's input, writes the
/// filtered bytes to <paramref name="sink"/>. <see cref="Complete"/> flushes the tail.
/// </summary>
internal sealed class SevenZipFilterWriteStream(Stream sink, ISevenZipFilter filter) : Stream {
  private readonly byte[] _buffer = new byte[1 << 16];
  private int _filled;
  private bool _completed;

  public override bool CanRead => false;
  public override bool CanSeek => false;
  public override bool CanWrite => true;
  public override long Length => throw new NotSupportedException();
  public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

  public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

  public override void WriteByte(byte value) => this.Write([value]);

  public override void Write(ReadOnlySpan<byte> data) {
    while (!data.IsEmpty) {
      var n = Math.Min(data.Length, this._buffer.Length - this._filled);
      data[..n].CopyTo(this._buffer.AsSpan(this._filled));
      this._filled += n;
      data = data[n..];
      if (this._filled == this._buffer.Length)
        this.Drain(final: false);
    }
  }

  /// <summary>Filters and writes whatever is still buffered; nothing may follow.</summary>
  public void Complete() {
    if (this._completed)
      return;
    this._completed = true;
    this.Drain(final: true);
  }

  private void Drain(bool final) {
    var done = filter.Process(this._buffer.AsSpan(0, this._filled), final);
    sink.Write(this._buffer, 0, done);
    var tail = this._filled - done;
    if (tail > 0)
      Buffer.BlockCopy(this._buffer, done, this._buffer, 0, tail);
    this._filled = tail;
  }

  public override void Flush() { }
  public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// Accepts at most <paramref name="limit"/> bytes for <paramref name="sink"/> and counts them, so a
/// decoder that overruns its declared unpack size is caught instead of corrupting what follows.
/// </summary>
internal sealed class SevenZipLimitedWriteStream(Stream sink, long limit) : Stream {
  public long Written { get; private set; }

  public override bool CanRead => false;
  public override bool CanSeek => false;
  public override bool CanWrite => true;
  public override long Length => throw new NotSupportedException();
  public override long Position { get => this.Written; set => throw new NotSupportedException(); }

  public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

  public override void WriteByte(byte value) => this.Write([value]);

  public override void Write(ReadOnlySpan<byte> data) {
    if (this.Written + data.Length > limit)
      throw new InvalidDataException("7z: a coder produced more data than its declared unpack size.");
    sink.Write(data);
    this.Written += data.Length;
  }

  public override void Flush() => sink.Flush();
  public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// A bounded single-producer, single-consumer byte pipe: a decoder that can only push its output
/// (LZMA, LZMA2) runs on its own thread and writes into the pipe, and the coder that consumes it
/// pulls. Memory stays bounded by the ring size whatever the stream length; the producer's
/// failure surfaces as the consumer's exception, and disposing the consumer stops the producer.
/// </summary>
internal sealed class SevenZipPipe {
  private readonly byte[] _ring;
  private readonly object _lock = new();
  private int _head;
  private int _count;
  private bool _completed;
  private bool _cancelled;
  private ExceptionDispatchInfo? _error;

  private SevenZipPipe(int capacity) => this._ring = new byte[capacity];

  /// <summary>Starts <paramref name="produce"/> on a background thread and returns the stream it fills.</summary>
  public static Stream Start(Action<Stream> produce, long length, int capacity = 1 << 20) {
    var pipe = new SevenZipPipe(capacity);
    var thread = new Thread(() => {
      try {
        produce(new Writer(pipe));
        pipe.Complete(null);
      } catch (OperationCanceledException) when (pipe.IsCancelled) {
        // The consumer went away; nobody is left to tell.
      } catch (Exception e) {
        pipe.Complete(ExceptionDispatchInfo.Capture(e));
      }
    }) { IsBackground = true, Name = "7z decoder" };
    thread.Start();
    return new Reader(pipe, length);
  }

  private bool IsCancelled { get { lock (this._lock) return this._cancelled; } }

  private void Complete(ExceptionDispatchInfo? error) {
    lock (this._lock) {
      this._completed = true;
      this._error = error;
      Monitor.PulseAll(this._lock);
    }
  }

  private void Cancel() {
    lock (this._lock) {
      this._cancelled = true;
      Monitor.PulseAll(this._lock);
    }
  }

  private void Put(ReadOnlySpan<byte> data) {
    while (!data.IsEmpty)
      lock (this._lock) {
        while (this._count == this._ring.Length && !this._cancelled)
          Monitor.Wait(this._lock);
        if (this._cancelled)
          throw new OperationCanceledException();

        var tail = (this._head + this._count) % this._ring.Length;
        var n = Math.Min(data.Length, Math.Min(this._ring.Length - this._count, this._ring.Length - tail));
        data[..n].CopyTo(this._ring.AsSpan(tail));
        this._count += n;
        data = data[n..];
        Monitor.PulseAll(this._lock);
      }
  }

  private int Take(Span<byte> destination) {
    lock (this._lock) {
      while (this._count == 0 && !this._completed)
        Monitor.Wait(this._lock);
      if (this._count == 0) {
        this._error?.Throw();
        return 0;
      }

      var n = Math.Min(destination.Length, Math.Min(this._count, this._ring.Length - this._head));
      this._ring.AsSpan(this._head, n).CopyTo(destination);
      this._head = (this._head + n) % this._ring.Length;
      this._count -= n;
      Monitor.PulseAll(this._lock);
      return n;
    }
  }

  private sealed class Writer(SevenZipPipe pipe) : Stream {
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) => pipe.Put(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) => pipe.Put(buffer);
    public override void WriteByte(byte value) => pipe.Put([value]);
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
  }

  private sealed class Reader(SevenZipPipe pipe, long length) : Stream {
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => this._position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) {
      if (buffer.IsEmpty)
        return 0;
      var n = pipe.Take(buffer);
      this._position += n;
      return n;
    }

    protected override void Dispose(bool disposing) {
      if (disposing)
        pipe.Cancel();
      base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
