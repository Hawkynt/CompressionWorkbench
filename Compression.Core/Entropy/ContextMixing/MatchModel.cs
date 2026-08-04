namespace Compression.Core.Entropy.ContextMixing;

/// <summary>
/// Tracks the longest recent repeat of the byte stream and predicts the next
/// byte by following that repeat forward — the "match model" technique used
/// by lpaq/PAQ-family context-mixing compressors alongside their fixed-order
/// context models.
/// </summary>
/// <remarks>
/// <para>
/// A rolling hash of the last <c>minOrder</c> bytes maps to the position that
/// previously followed that same context. While a match is active, the model
/// predicts the byte at the remembered position; each further byte that keeps
/// matching extends the run and raises confidence, while any mismatch drops
/// the match and the hash table is consulted again on the next byte. This is
/// a clean-room implementation of the general technique described in
/// Mahoney's PAQ notes (<see href="http://mattmahoney.net/dc/dce.html"/>) and
/// Byron Knoll's cmix write-ups
/// (<see href="http://byronknoll.blogspot.com/2014/01/cmix.html"/>), not a
/// port of any specific implementation.
/// </para>
/// </remarks>
public sealed class MatchModel {
  private readonly byte[] _buffer;
  private readonly int[] _hashHead;
  private readonly int _hashMask;
  private readonly int _minOrder;
  private int _length;

  /// <summary>
  /// Initializes a new <see cref="MatchModel"/>.
  /// </summary>
  /// <param name="capacity">The maximum number of bytes the model will see.</param>
  /// <param name="minOrder">The context length (bytes) used to seed a new match. Defaults to 4.</param>
  /// <param name="hashBits">Log2 of the match hash table size. Defaults to 18.</param>
  public MatchModel(int capacity, int minOrder = 4, int hashBits = 18) {
    ArgumentOutOfRangeException.ThrowIfNegative(capacity);
    this._buffer = new byte[Math.Max(capacity, 1)];
    this._minOrder = Math.Max(minOrder, 1);
    this._hashHead = new int[1 << hashBits];
    this._hashHead.AsSpan().Fill(-1);
    this._hashMask = (1 << hashBits) - 1;
  }

  /// <summary>
  /// Gets the position in the byte history currently being predicted from, or
  /// -1 when no match is active.
  /// </summary>
  public int MatchPointer { get; private set; } = -1;

  /// <summary>
  /// Gets the number of consecutive bytes that have matched so far (0 when no
  /// match is active). Larger values indicate higher prediction confidence.
  /// </summary>
  public int MatchLength { get; private set; }

  /// <summary>
  /// Gets the predicted next byte, or -1 when no match is active.
  /// </summary>
  public int PredictedByte =>
    this.MatchLength > 0 && this.MatchPointer >= 0 && this.MatchPointer < this._length
      ? this._buffer[this.MatchPointer]
      : -1;

  /// <summary>
  /// Records the actual next byte, extending or breaking the active match and
  /// updating the context hash table for future lookups.
  /// </summary>
  /// <param name="value">The byte that was just coded.</param>
  public void Append(byte value) {
    if (this.MatchLength > 0 && this.MatchPointer < this._length && this._buffer[this.MatchPointer] == value) {
      ++this.MatchPointer;
      ++this.MatchLength;
    }
    else {
      this.MatchLength = 0;
      this.MatchPointer = -1;
    }

    if (this._length < this._buffer.Length)
      this._buffer[this._length] = value;
    ++this._length;

    if (this._length < this._minOrder)
      return;

    var hash = this.ComputeContextHash();
    if (this.MatchLength == 0) {
      var candidate = this._hashHead[hash];
      if (candidate >= 0 && candidate < this._length) {
        this.MatchPointer = candidate;
        this.MatchLength = 1;
      }
    }

    this._hashHead[hash] = this._length;
  }

  private int ComputeContextHash() {
    var h = 0xC2B2AE35u;
    for (var i = this._length - this._minOrder; i < this._length; ++i)
      h = MatchModel.Mix(h, this._buffer[i]);

    return (int)(h & (uint)this._hashMask);
  }

  private static uint Mix(uint h, uint x) {
    h ^= x + 0x9E3779B1u + (h << 6) + (h >> 2);
    return h;
  }
}
