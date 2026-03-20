namespace Compression.Core.Dictionary.Rzip;

/// <summary>
/// Rolling hash block matcher (rsync-style) for long-distance deduplication.
/// Finds matching blocks across large distances using a Rabin-like rolling hash.
/// </summary>
public sealed class RollingHashMatcher {
  private readonly int _blockSize;
  private readonly Dictionary<uint, List<int>> _hashTable;

  /// <summary>Block size for hash computation.</summary>
  public int BlockSize => _blockSize;

  /// <summary>
  /// Initializes a new <see cref="RollingHashMatcher"/> with the specified block size.
  /// </summary>
  /// <param name="blockSize">The size of blocks used for hash computation.</param>
  public RollingHashMatcher(int blockSize = 4096) {
    ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);
    _blockSize = blockSize;
    _hashTable = new Dictionary<uint, List<int>>();
  }

  /// <summary>
  /// Index the reference data by computing rolling hashes at block boundaries.
  /// </summary>
  /// <param name="data">The reference data to index.</param>
  public void Index(byte[] data) {
    _hashTable.Clear();
    for (int i = 0; i + _blockSize <= data.Length; i += _blockSize) {
      uint hash = ComputeHash(data, i, _blockSize);
      if (!_hashTable.TryGetValue(hash, out var list)) {
        list = new List<int>();
        _hashTable[hash] = list;
      }

      list.Add(i);
    }
  }

  /// <summary>
  /// Find matching blocks in the input against the indexed reference.
  /// Returns list of tokens representing literal ranges and matches against the reference.
  /// </summary>
  /// <param name="input">The input data to scan for matches.</param>
  /// <param name="reference">The reference data that was previously indexed.</param>
  /// <returns>A list of <see cref="RzipToken"/> instances describing literals and matches.</returns>
  public List<RzipToken> FindMatches(byte[] input, byte[] reference) {
    var tokens = new List<RzipToken>();
    int pos = 0;
    int literalStart = 0;

    while (pos + _blockSize <= input.Length) {
      uint hash = ComputeHash(input, pos, _blockSize);
      if (_hashTable.TryGetValue(hash, out var candidates)) {
        // Verify match against each candidate
        foreach (int refPos in candidates) {
          if (VerifyMatch(input, pos, reference, refPos, _blockSize)) {
            // Extend match forward beyond the initial block
            int matchLen = _blockSize;
            while (pos + matchLen < input.Length && refPos + matchLen < reference.Length &&
                   input[pos + matchLen] == reference[refPos + matchLen])
              matchLen++;

            // Emit pending literals
            if (pos > literalStart)
              tokens.Add(new RzipToken(literalStart, pos - literalStart, -1, true));

            tokens.Add(new RzipToken(pos, matchLen, refPos, false));
            pos += matchLen;
            literalStart = pos;
            goto next;
          }
        }
      }

      pos++;
      next: ;
    }

    // Remaining literals
    if (input.Length > literalStart)
      tokens.Add(new RzipToken(literalStart, input.Length - literalStart, -1, true));

    return tokens;
  }

  private static uint ComputeHash(byte[] data, int offset, int length) {
    // FNV-1a hash
    uint h = 2166136261u;
    int end = offset + length;
    for (int i = offset; i < end; i++) {
      h ^= data[i];
      h *= 16777619u;
    }

    return h;
  }

  private static bool VerifyMatch(byte[] a, int aOff, byte[] b, int bOff, int len) {
    for (int i = 0; i < len; i++) {
      if (a[aOff + i] != b[bOff + i])
        return false;
    }

    return true;
  }
}

/// <summary>Token representing a literal range or a match against reference data.</summary>
/// <param name="InputOffset">Offset into the input array where this token starts.</param>
/// <param name="Length">Length in bytes of the literal data or the match.</param>
/// <param name="ReferenceOffset">Absolute offset into the reference/output data for matches, or -1 for literals.</param>
/// <param name="IsLiteral"><c>true</c> if this token represents literal bytes; <c>false</c> for a back-reference match.</param>
public readonly record struct RzipToken(int InputOffset, int Length, int ReferenceOffset, bool IsLiteral);
