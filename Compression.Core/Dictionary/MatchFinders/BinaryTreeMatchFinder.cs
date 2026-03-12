namespace Compression.Core.Dictionary.MatchFinders;

/// <summary>
/// Binary-tree match finder for LZ77-style compression.
/// Maintains a binary search tree of hash-indexed positions,
/// providing better match quality than hash chains at higher computational cost.
/// </summary>
public sealed class BinaryTreeMatchFinder : IMatchFinder {
  private const int HashBits = 15;
  private const int HashSize = 1 << BinaryTreeMatchFinder.HashBits;
  private const int HashMask = BinaryTreeMatchFinder.HashSize - 1;
  private const int MaxIterations = 128;

  private readonly int _windowMask;
  private readonly int[] _head;
  private readonly int[] _left;
  private readonly int[] _right;

  /// <summary>
  /// Initializes a new <see cref="BinaryTreeMatchFinder"/>.
  /// </summary>
  /// <param name="windowSize">The maximum sliding window size. Must be a power of two.</param>
  public BinaryTreeMatchFinder(int windowSize) {
    this._windowMask = windowSize - 1;
    this._head = new int[BinaryTreeMatchFinder.HashSize];
    this._left = new int[windowSize];
    this._right = new int[windowSize];
    this._head.AsSpan().Fill(-1);
  }

  /// <inheritdoc />
  public Match FindMatch(ReadOnlySpan<byte> data, int position, int maxDistance, int maxLength, int minLength = 3) {
    if (position + 2 >= data.Length)
      return default;

    var hash = ComputeHash(data, position);
    var windowStart = Math.Max(0, position - maxDistance);
    var posIdx = position & this._windowMask;

    var bestDistance = 0;
    var bestLength = 0;

    // Insert new node at root; split old tree into left/right subtrees
    var oldRoot = this._head[hash];
    this._head[hash] = position;

    var leftAnchorIdx = posIdx;
    var rightAnchorIdx = posIdx;

    var node = oldRoot;
    var iterations = 0;

    while (node >= windowStart && node >= 0 && iterations < BinaryTreeMatchFinder.MaxIterations) {
      var nodeIdx = node & this._windowMask;
      iterations++;

      // Compare strings
      var limit = Math.Min(maxLength, Math.Min(data.Length - position, data.Length - node));
      var matchLen = 0;
      while (matchLen < limit && data[position + matchLen] == data[node + matchLen])
        matchLen++;

      if (matchLen >= minLength && matchLen > bestLength) {
        bestLength = matchLen;
        bestDistance = position - node;
        if (bestLength >= maxLength)
          break;
      }

      if (matchLen < limit && data[position + matchLen] < data[node + matchLen]) {
        this._right[rightAnchorIdx] = node;
        rightAnchorIdx = nodeIdx;
        node = this._left[nodeIdx];
      } else {
        this._left[leftAnchorIdx] = node;
        leftAnchorIdx = nodeIdx;
        node = this._right[nodeIdx];
      }
    }

    this._right[rightAnchorIdx] = -1;
    this._left[leftAnchorIdx] = -1;

    return bestLength >= minLength ? new Match(bestDistance, bestLength) : default;
  }

  /// <summary>
  /// Inserts a position into the tree without searching for a match.
  /// </summary>
  /// <param name="data">The input data buffer.</param>
  /// <param name="position">The position to insert.</param>
  public void InsertPosition(ReadOnlySpan<byte> data, int position) {
    if (position + 2 >= data.Length)
      return;

    this.FindMatch(data, position, 0, 3, 3);
  }

  private static int ComputeHash(ReadOnlySpan<byte> data, int position) =>
    ((data[position] << 10) ^ (data[position + 1] << 5) ^ data[position + 2]) & BinaryTreeMatchFinder.HashMask;
}
