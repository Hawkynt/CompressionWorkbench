namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Hash-chain longest-match search over the input for the small-window LHA encoders
/// (<c>-lzs-</c>, <c>-lz5-</c>, <c>-lh1-</c>). A match at distance <c>d</c> copies from <c>d</c> bytes
/// back and may overlap the bytes it produces.
/// </summary>
internal sealed class LhaMatchFinder {
  private const int HashBits = 14;
  private const int MaxChain = 128;
  private readonly byte[] _data;
  private readonly int _maxDistance;
  private readonly int _minLength;
  private readonly int _maxLength;
  private readonly int[] _head = new int[1 << HashBits];
  private readonly int[] _prev;
  private int _inserted;

  public LhaMatchFinder(byte[] data, int maxDistance, int minLength, int maxLength) {
    this._data = data;
    this._maxDistance = maxDistance;
    this._minLength = minLength;
    this._maxLength = maxLength;
    this._prev = new int[data.Length];
    Array.Fill(this._head, -1);
  }

  private int Hash(int pos) =>
    ((this._data[pos] << 10) ^ (this._data[pos + 1] << 5) ^ this._data[pos + 2]) & ((1 << HashBits) - 1);

  /// <summary>Indexes every position before <paramref name="pos"/> that is not yet indexed.</summary>
  private void InsertUpTo(int pos) {
    for (; this._inserted < pos; ++this._inserted) {
      if (this._inserted + 2 >= this._data.Length)
        continue;
      var h = this.Hash(this._inserted);
      this._prev[this._inserted] = this._head[h];
      this._head[h] = this._inserted;
    }
  }

  /// <summary>Finds the longest match for the bytes at <paramref name="pos"/>; returns length 0 when none reaches the minimum.</summary>
  public (int Length, int Distance) Find(int pos) {
    this.InsertUpTo(pos);
    var limit = Math.Min(this._maxLength, this._data.Length - pos);
    if (limit < this._minLength || pos + 2 >= this._data.Length)
      return (0, 0);

    int bestLength = 0, bestDistance = 0, chain = 0;
    for (var candidate = this._head[this.Hash(pos)]; candidate >= 0 && chain < MaxChain; candidate = this._prev[candidate], ++chain) {
      var distance = pos - candidate;
      if (distance > this._maxDistance)
        break;
      var length = 0;
      while (length < limit && this._data[candidate + length] == this._data[pos + length])
        ++length;
      if (length > bestLength) {
        bestLength = length;
        bestDistance = distance;
        if (length == limit)
          break;
      }
    }
    return bestLength >= this._minLength ? (bestLength, bestDistance) : (0, 0);
  }
}
