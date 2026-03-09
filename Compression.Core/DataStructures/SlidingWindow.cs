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
  /// <param name="b">The byte to write.</param>
  public void WriteByte(byte b) {
    this._buffer[this._position] = b;
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

    int srcPos = ((this._position - distance) % this._buffer.Length + this._buffer.Length) % this._buffer.Length;

    for (int i = 0; i < length; ++i) {
      byte b = this._buffer[srcPos];
      output[i] = b;
      WriteByte(b);
      srcPos = (srcPos + 1) % this._buffer.Length;
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
}
