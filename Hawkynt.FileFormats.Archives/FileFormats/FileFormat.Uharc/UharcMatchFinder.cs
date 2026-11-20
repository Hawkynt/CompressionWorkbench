namespace FileFormat.Uharc;

/// <summary>
/// The match search of the ALZ encoders: 3-byte hash chains over a window, and the "whole copy"
/// rule both ALZ coders need — a copy is the full match, or 255 bytes for a run that goes on,
/// because the byte after a shorter copy is excluded from the next literal.
/// </summary>
internal sealed class UharcMatchFinder {

  public const int MaxLength = 255;
  private const int HashBits = 16;
  private const int ChainDepth = 48;

  private readonly byte[] _data;
  private readonly int[] _head = new int[1 << HashBits];
  private readonly int[] _previous;
  private readonly uint _mask;
  private int _inserted;

  /// <param name="data">The input.</param>
  /// <param name="windowSize">The dictionary size, a power of two.</param>
  public UharcMatchFinder(byte[] data, uint windowSize) {
    this._data = data;
    this._previous = new int[windowSize];
    this._mask = windowSize - 1;
    Array.Fill(this._head, -1);
  }

  private int Hash(int position) => ((this._data[position] << 8 ^ this._data[position + 1] << 4 ^ this._data[position + 2]) * 0x9E37) >> 4 & ((1 << HashBits) - 1);

  /// <summary>Enters every position below <paramref name="end"/> into the hash chains.</summary>
  public void Insert(int end) {
    var last = Math.Min(end, this._data.Length - 2);
    for (; this._inserted < last; ++this._inserted) {
      var h = this.Hash(this._inserted);
      this._previous[this._inserted & this._mask] = this._head[h];
      this._head[h] = this._inserted;
    }
  }

  /// <summary>Distances, nearest first, at which the three bytes at <paramref name="position"/> occurred, up to <paramref name="reach"/>.</summary>
  public IEnumerable<uint> Distances(int position, uint reach) {
    if (position + 2 >= this._data.Length)
      yield break;

    var candidate = this._head[this.Hash(position)];
    for (var depth = 0; depth < ChainDepth && candidate >= 0; ++depth) {
      var distance = (uint)(position - candidate);
      if (distance > reach)
        yield break;
      yield return distance;
      var next = this._previous[candidate & this._mask];
      if (next >= candidate)
        yield break;
      candidate = next;
    }
  }

  /// <summary>
  /// The copy at <paramref name="distance"/> taken whole, at most <paramref name="limit"/> bytes:
  /// the full match, 255 for a run, or 0 when <paramref name="limit"/> cuts a longer match.
  /// </summary>
  public int UsableLength(int position, uint distance, int limit) {
    var data = this._data;
    var source = position - (int)distance;
    var cap = Math.Min(limit, MaxLength);
    var length = 0;
    while (length < cap && data[source + length] == data[position + length])
      ++length;
    if (length == MaxLength)
      return length;
    if (length == limit && limit < data.Length - position && data[source + length] == data[position + length])
      return 0;
    return length;
  }
}
