namespace Compression.Core.FileSystems.Cvf;

/// <summary>
/// Hash-chain LZ77 match finder over one cluster, shared by the DS/JM and SD-4 encoders. Every match
/// it reports lies wholly inside the cluster: no CVF codec carries a dictionary from one cluster to
/// the next.
/// </summary>
internal sealed class ClusterMatchFinder {
  private const int HashBits = 13;
  private const int MinLength = 3;

  private readonly byte[] _data;
  private readonly int[] _head = new int[1 << HashBits];
  private readonly int[] _previous;
  private readonly int _maxDistance;
  private readonly int _maxLength;
  private readonly int _maxChain;
  private readonly int _niceLength;
  private int _inserted;

  /// <param name="data">The cluster.</param>
  /// <param name="maxDistance">The farthest distance the format can express.</param>
  /// <param name="maxLength">The longest match the format can express.</param>
  /// <param name="level">Search effort, 1 (fastest) and up: how many earlier positions with the same
  /// hash are tried, and how long a match must be to end the search early.</param>
  public ClusterMatchFinder(byte[] data, int maxDistance, int maxLength, int level) {
    this._data = data;
    this._previous = new int[data.Length];
    this._maxDistance = maxDistance;
    this._maxLength = maxLength;
    (this._maxChain, this._niceLength) = level switch {
      <= 1 => (16, 64),
      2 => (64, 258),
      3 => (256, 1024),
      _ => (1024, int.MaxValue),
    };
    Array.Fill(this._head, -1);
  }

  /// <summary>
  /// The longest match for the bytes at <paramref name="position"/>, preferring the nearest on ties;
  /// <c>(0, 0)</c> when there is none of at least three bytes. Positions before
  /// <paramref name="position"/> are indexed first, so calls must come in ascending order.
  /// </summary>
  /// <param name="position">Where the match starts.</param>
  /// <param name="end">Exclusive bound the match may not run past; the cluster end when omitted.</param>
  public (int Length, int Distance) Find(int position, int end = int.MaxValue) {
    this.IndexUpTo(position);
    var data = this._data;
    var limit = Math.Min(this._maxLength, Math.Min(data.Length, end) - position);
    if (limit < MinLength) return (0, 0);

    int bestLength = 0, bestDistance = 0;
    var candidate = this._head[Hash(data, position)];
    for (var chain = this._maxChain; candidate >= 0 && chain > 0; --chain, candidate = this._previous[candidate]) {
      var distance = position - candidate;
      if (distance > this._maxDistance) break;
      if (data[candidate + bestLength] != data[position + bestLength]) continue;

      var length = 0;
      while (length < limit && data[candidate + length] == data[position + length]) ++length;
      if (length <= bestLength) continue;

      (bestLength, bestDistance) = (length, distance);
      if (length == limit || length >= this._niceLength) break;
    }
    return bestLength >= MinLength ? (bestLength, bestDistance) : (0, 0);
  }

  private void IndexUpTo(int position) {
    var last = Math.Min(position, this._data.Length - MinLength + 1);
    for (; this._inserted < last; ++this._inserted) {
      var h = Hash(this._data, this._inserted);
      this._previous[this._inserted] = this._head[h];
      this._head[h] = this._inserted;
    }
  }

  private static int Hash(byte[] data, int position)
    => (int)(((uint)data[position] << 16 | (uint)data[position + 1] << 8 | data[position + 2]) * 2654435761u >> (32 - HashBits));
}
