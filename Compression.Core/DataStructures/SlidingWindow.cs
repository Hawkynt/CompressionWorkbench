namespace Compression.Core.DataStructures;

/// <summary>
/// Circular buffer that supports byte-by-byte writing and distance/length copying
/// (including overlapping copies where distance &lt; length).
/// </summary>
public sealed class SlidingWindow {
  private readonly byte[] _buffer;
  private int _position;
  private int _count;

  /// <summary>
  /// Initializes a new <see cref="SlidingWindow"/> with the specified capacity.
  /// </summary>
  /// <param name="windowSize">The size of the sliding window in bytes.</param>
  public SlidingWindow(int windowSize) {
    ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 1);
    this._buffer = new byte[windowSize];
  }

  /// <summary>
  /// Gets the window capacity.
  /// </summary>
  public int WindowSize => this._buffer.Length;

  /// <summary>
  /// Gets the number of bytes currently stored in the window.
  /// </summary>
  public int Count => this._count;

  /// <summary>
  /// Writes a single byte into the window.
  /// </summary>
  /// <param name="value">The byte to write.</param>
  public void WriteByte(byte value) {
    this._buffer[this._position] = value;
    this._position = (this._position + 1) % this._buffer.Length;
    if (this._count < this._buffer.Length)
      ++this._count;
  }

  /// <summary>
  /// Copies <paramref name="length"/> bytes from a position <paramref name="distance"/> bytes back
  /// in the window, writing each byte into the window as it is copied.
  /// Handles overlapping copies correctly (e.g., distance=1, length=10 repeats one byte 10 times).
  /// </summary>
  /// <param name="distance">How far back to look (1 = last byte written).</param>
  /// <param name="length">How many bytes to copy.</param>
  /// <param name="output">Buffer to receive the copied bytes.</param>
  /// <exception cref="ArgumentOutOfRangeException">Distance exceeds available data or is &lt; 1.</exception>
  public void CopyFromWindow(int distance, int length, Span<byte> output) {
    ArgumentOutOfRangeException.ThrowIfLessThan(distance, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(distance, this._count);

    int winSize = this._buffer.Length;
    int srcPos = ((this._position - distance) % winSize + winSize) % winSize;

    if (distance >= length) {
      // Non-overlapping: source region doesn't overlap with what we're writing
      int contiguous = winSize - srcPos;
      if (contiguous >= length) {
        // Single contiguous copy from buffer
        this._buffer.AsSpan(srcPos, length).CopyTo(output);
      } else {
        // Wraps around the circular buffer
        this._buffer.AsSpan(srcPos, contiguous).CopyTo(output);
        this._buffer.AsSpan(0, length - contiguous).CopyTo(output.Slice(contiguous));
      }

      // Write all copied bytes into the window
      WriteSpan(output.Slice(0, length));
    } else {
      // Overlapping: distance < length — build the repeat pattern then bulk-write
      // First, copy 'distance' bytes (the base pattern)
      int copied = 0;
      while (copied < distance) {
        int toCopy = Math.Min(distance - copied, winSize - srcPos);
        this._buffer.AsSpan(srcPos, toCopy).CopyTo(output.Slice(copied));
        copied += toCopy;
        srcPos = (srcPos + toCopy) % winSize;
      }

      // Now double the pattern repeatedly until filled
      while (copied < length) {
        int toCopy = Math.Min(copied, length - copied);
        output.Slice(0, toCopy).CopyTo(output.Slice(copied));
        copied += toCopy;
      }

      // Write into window
      WriteSpan(output.Slice(0, length));
    }
  }

  /// <summary>
  /// Writes a span of bytes into the window.
  /// </summary>
  /// <param name="data">The bytes to write.</param>
  public void WriteBytes(ReadOnlySpan<byte> data) {
    int winSize = this._buffer.Length;
    int remaining = data.Length;
    int offset = 0;

    while (remaining > 0) {
      int space = winSize - this._position;
      int toCopy = Math.Min(remaining, space);
      data.Slice(offset, toCopy).CopyTo(this._buffer.AsSpan(this._position, toCopy));
      this._position = (this._position + toCopy) % winSize;
      this._count = Math.Min(this._count + toCopy, winSize);
      offset += toCopy;
      remaining -= toCopy;
    }
  }

  /// <summary>
  /// Gets the byte at the specified distance back from the current position.
  /// </summary>
  /// <param name="distance">How far back to look (1 = last byte written).</param>
  /// <returns>The byte at that distance.</returns>
  public byte GetByte(int distance) {
    ArgumentOutOfRangeException.ThrowIfLessThan(distance, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(distance, this._count);

    int index = ((this._position - distance) % this._buffer.Length + this._buffer.Length) % this._buffer.Length;
    return this._buffer[index];
  }

  private void WriteSpan(Span<byte> data) {
    int winSize = this._buffer.Length;
    int remaining = data.Length;
    int offset = 0;

    while (remaining > 0) {
      int space = winSize - this._position;
      int toCopy = Math.Min(remaining, space);
      data.Slice(offset, toCopy).CopyTo(this._buffer.AsSpan(this._position, toCopy));
      this._position = (this._position + toCopy) % winSize;
      this._count = Math.Min(this._count + toCopy, winSize);
      offset += toCopy;
      remaining -= toCopy;
    }
  }
}
