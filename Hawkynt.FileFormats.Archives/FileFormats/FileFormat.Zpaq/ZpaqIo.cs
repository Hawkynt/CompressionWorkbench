using Compression.Core.Checksums;

namespace FileFormat.Zpaq;

/// <summary>Byte source consumed by the ZPAQ engine.</summary>
internal interface IZpaqReader {
  /// <summary>Returns the next byte (0..255) or -1 at end of input.</summary>
  int Get();

  /// <summary>Reads up to <paramref name="buffer"/>.Length bytes and returns the count; 0 means end of input.</summary>
  int Read(Span<byte> buffer) {
    var n = 0;
    for (; n < buffer.Length; ++n) {
      var c = this.Get();
      if (c < 0)
        break;
      buffer[n] = (byte)c;
    }
    return n;
  }
}

/// <summary>Byte sink fed by the ZPAQ engine.</summary>
internal interface IZpaqWriter {
  /// <summary>Writes the low eight bits of <paramref name="c"/>.</summary>
  void Put(int c);

  /// <summary>Writes a run of bytes.</summary>
  void Write(ReadOnlySpan<byte> data) {
    foreach (var b in data)
      this.Put(b);
  }
}

/// <summary>Growable in-memory byte buffer that can be appended to and read back, like libzpaq's <c>StringBuffer</c>.</summary>
internal sealed class ZpaqBuffer : IZpaqReader, IZpaqWriter {
  private byte[] _data;
  private int _readPosition;

  /// <summary>Creates an empty buffer with the given initial capacity.</summary>
  public ZpaqBuffer(int capacity = 0) => this._data = capacity > 0 ? new byte[capacity] : [];

  /// <summary>Creates a buffer that holds a copy of <paramref name="content"/>.</summary>
  public ZpaqBuffer(ReadOnlySpan<byte> content) {
    this._data = content.ToArray();
    this.Size = content.Length;
  }

  /// <summary>Number of bytes written.</summary>
  public int Size { get; private set; }

  /// <summary>Maximum number of bytes that may be written before the buffer refuses more.</summary>
  public long Limit { get; set; } = long.MaxValue;

  /// <summary>Bytes written but not read yet.</summary>
  public int Remaining => this.Size - this._readPosition;

  /// <summary>The written content; the backing array may be longer than <see cref="Size"/>.</summary>
  public byte[] Data => this._data;

  /// <summary>The written content as a span.</summary>
  public Span<byte> Span => this._data.AsSpan(0, this.Size);

  /// <inheritdoc/>
  public int Get() => this._readPosition < this.Size ? this._data[this._readPosition++] : -1;

  /// <inheritdoc/>
  public int Read(Span<byte> buffer) {
    var n = Math.Min(buffer.Length, this.Size - this._readPosition);
    if (n <= 0)
      return 0;
    this._data.AsSpan(this._readPosition, n).CopyTo(buffer);
    this._readPosition += n;
    return n;
  }

  /// <inheritdoc/>
  public void Put(int c) {
    this.EnsureRoom(1);
    this._data[this.Size++] = (byte)c;
  }

  /// <inheritdoc/>
  public void Write(ReadOnlySpan<byte> data) {
    if (data.IsEmpty)
      return;
    this.EnsureRoom(data.Length);
    data.CopyTo(this._data.AsSpan(this.Size));
    this.Size += data.Length;
  }

  /// <summary>Truncates (or extends without clearing) the written size and clamps the read position.</summary>
  public void Resize(int size) {
    if (size > this._data.Length)
      Array.Resize(ref this._data, size);
    this.Size = size;
    if (this._readPosition > size)
      this._readPosition = size;
  }

  /// <summary>Discards all content and rewinds the read position.</summary>
  public void Reset() {
    this.Size = 0;
    this._readPosition = 0;
  }

  /// <summary>Returns a copy of the written bytes.</summary>
  public byte[] ToArray() => this._data.AsSpan(0, this.Size).ToArray();

  private void EnsureRoom(int n) {
    if (this.Size + (long)n > this.Limit)
      ZpaqError.Throw("buffer overflow");
    if (this.Size + n <= this._data.Length)
      return;
    var capacity = Math.Max(128, this._data.Length);
    while (capacity < this.Size + n)
      capacity = capacity > Array.MaxLength / 2 ? Array.MaxLength : capacity * 2;
    Array.Resize(ref this._data, capacity);
  }
}

/// <summary>Reads from a <see cref="Stream"/>.</summary>
internal sealed class ZpaqStreamReader(Stream stream) : IZpaqReader {
  /// <inheritdoc/>
  public int Get() => stream.ReadByte();

  /// <inheritdoc/>
  public int Read(Span<byte> buffer) => stream.Read(buffer);
}

/// <summary>Writes to a <see cref="Stream"/>.</summary>
internal sealed class ZpaqStreamWriter(Stream stream) : IZpaqWriter {
  /// <inheritdoc/>
  public void Put(int c) => stream.WriteByte((byte)c);

  /// <inheritdoc/>
  public void Write(ReadOnlySpan<byte> data) => stream.Write(data);
}

/// <summary>Discards everything written to it.</summary>
internal sealed class ZpaqNullWriter : IZpaqWriter {
  /// <summary>The shared instance.</summary>
  public static readonly ZpaqNullWriter Instance = new();

  /// <inheritdoc/>
  public void Put(int c) { }

  /// <inheritdoc/>
  public void Write(ReadOnlySpan<byte> data) { }
}

/// <summary>Incremental SHA-1 that also counts the bytes hashed, matching libzpaq's <c>SHA1</c> usage.</summary>
internal sealed class ZpaqSha1 : IZpaqWriter {
  private readonly Sha1 _sha1 = new();

  /// <summary>Number of bytes hashed since the last <see cref="Result"/>.</summary>
  public long Size { get; private set; }

  /// <inheritdoc/>
  public void Put(int c) {
    Span<byte> one = [(byte)c];
    this.Write(one);
  }

  /// <inheritdoc/>
  public void Write(ReadOnlySpan<byte> data) {
    this._sha1.Update(data);
    this.Size += data.Length;
  }

  /// <summary>Returns the 20-byte digest and resets for the next input.</summary>
  public byte[] Result() {
    this._sha1.Finish();
    var hash = this._sha1.Hash;
    this._sha1.Reset();
    this.Size = 0;
    return hash;
  }
}

/// <summary>Raises the engine's error for malformed input or an invalid model.</summary>
internal static class ZpaqError {
  /// <summary>Throws an <see cref="InvalidDataException"/> carrying <paramref name="message"/>.</summary>
  [System.Diagnostics.CodeAnalysis.DoesNotReturn]
  public static void Throw(string message) => throw new InvalidDataException("ZPAQ: " + message);
}
