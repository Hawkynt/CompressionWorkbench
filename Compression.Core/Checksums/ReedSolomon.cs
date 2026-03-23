namespace Compression.Core.Checksums;

/// <summary>
/// Reed-Solomon encoder/decoder over GF(2^8) using primitive polynomial 0x11D
/// (x^8 + x^4 + x^3 + x^2 + 1). Used for recovery records in archive formats.
/// </summary>
public sealed class ReedSolomon {
  private static readonly byte[] ExpTable = new byte[512];
  private static readonly byte[] LogTable = new byte[256];

  static ReedSolomon() {
    // Build GF(2^8) exp and log tables using polynomial 0x11D
    var x = 1;
    for (var i = 0; i < 255; ++i) {
      ExpTable[i] = (byte)x;
      ExpTable[i + 255] = (byte)x; // wrap for easy mod
      LogTable[x] = (byte)i;
      x <<= 1;
      if ((x & 0x100) != 0)
        x ^= 0x11D;
    }

    LogTable[0] = 0; // undefined but safe default
  }

  private static byte GfMul(byte a, byte b) {
    if (a == 0 || b == 0) return 0;
    return ExpTable[LogTable[a] + LogTable[b]];
  }

  private static byte GfDiv(byte a, byte b) {
    if (b == 0) throw new DivideByZeroException("GF(2^8) division by zero.");
    if (a == 0) return 0;
    return ExpTable[(LogTable[a] - LogTable[b] + 255) % 255];
  }

  private static byte GfPow(byte a, int n) {
    if (n == 0) return 1;
    if (a == 0) return 0;
    return ExpTable[(LogTable[a] * n) % 255];
  }

  private readonly int _dataShards;
  private readonly int _parityShards;
  private readonly byte[,] _matrix; // encoding matrix: parityShards × dataShards

  /// <summary>
  /// Creates a Reed-Solomon codec with the specified number of data and parity shards.
  /// </summary>
  /// <param name="dataShards">Number of data shards.</param>
  /// <param name="parityShards">Number of parity shards (recovery blocks).</param>
  public ReedSolomon(int dataShards, int parityShards) {
    this._dataShards = dataShards;
    this._parityShards = parityShards;

    // Build Vandermonde-based encoding matrix
    // Each parity shard i is computed as: sum(data[j] * matrix[i,j]) for all j
    this._matrix = new byte[parityShards, dataShards];
    for (var i = 0; i < parityShards; ++i) {
      for (var j = 0; j < dataShards; ++j) {
        // Vandermonde: matrix[i,j] = α^(i*j), where α = ExpTable[1] = 2
        this._matrix[i, j] = GfPow(ExpTable[j + 1], i + 1);
      }
    }
  }

  /// <summary>
  /// Encodes data shards to produce parity shards.
  /// </summary>
  /// <param name="data">Array of data shards, each the same length.</param>
  /// <returns>Array of parity shards.</returns>
  public byte[][] Encode(byte[][] data) {
    if (data.Length != this._dataShards)
      throw new ArgumentException($"Expected {this._dataShards} data shards, got {data.Length}.");

    var shardSize = data[0].Length;
    var parity = new byte[this._parityShards][];

    for (var i = 0; i < this._parityShards; ++i) {
      parity[i] = new byte[shardSize];
      for (var j = 0; j < this._dataShards; ++j) {
        var coeff = this._matrix[i, j];
        if (coeff == 0) continue;
        var src = data[j];
        var dst = parity[i];
        for (var k = 0; k < shardSize; ++k)
          dst[k] ^= GfMul(coeff, src[k]);
      }
    }

    return parity;
  }

  /// <summary>
  /// Reconstructs missing data shards using surviving data and parity shards.
  /// </summary>
  /// <param name="shards">All shards (data + parity). Missing shards should be null.</param>
  /// <returns><see langword="true"/> if reconstruction succeeded.</returns>
  public bool Reconstruct(byte[]?[] shards) {
    if (shards.Length != this._dataShards + this._parityShards)
      throw new ArgumentException("Shard count does not match configuration.");

    // Find which shards are present and which are missing
    var missingIndices = new List<int>();
    var presentIndices = new List<int>();

    for (var i = 0; i < shards.Length; ++i) {
      if (shards[i] == null)
        missingIndices.Add(i);
      else
        presentIndices.Add(i);
    }

    if (missingIndices.Count == 0) return true;
    if (presentIndices.Count < this._dataShards) return false;

    // We need exactly _dataShards present shards to solve the system
    var usedPresent = presentIndices.GetRange(0, this._dataShards);
    var shardSize = 0;
    foreach (var idx in usedPresent) {
      if (shards[idx] != null) { shardSize = shards[idx]!.Length; break; }
    }

    // Build sub-matrix from full encoding matrix rows corresponding to usedPresent
    // Full matrix: identity (dataShards×dataShards) stacked with _matrix (parityShards×dataShards)
    var subMatrix = new byte[this._dataShards, this._dataShards];
    for (var row = 0; row < this._dataShards; ++row) {
      var srcIdx = usedPresent[row];
      for (var col = 0; col < this._dataShards; ++col) {
        if (srcIdx < this._dataShards) {
          // Identity row
          subMatrix[row, col] = (byte)(srcIdx == col ? 1 : 0);
        }
        else {
          // Parity row
          subMatrix[row, col] = this._matrix[srcIdx - this._dataShards, col];
        }
      }
    }

    // Invert the sub-matrix using Gaussian elimination in GF(2^8)
    var inverse = InvertMatrix(subMatrix, this._dataShards);
    if (inverse == null) return false;

    // Reconstruct missing data shards
    foreach (var missingIdx in missingIndices) {
      if (missingIdx >= this._dataShards) continue; // we only need to reconstruct data shards

      shards[missingIdx] = new byte[shardSize];
      for (var j = 0; j < this._dataShards; ++j) {
        var coeff = inverse[missingIdx, j];
        if (coeff == 0) continue;
        var src = shards[usedPresent[j]]!;
        var dst = shards[missingIdx]!;
        for (var k = 0; k < shardSize; ++k)
          dst[k] ^= GfMul(coeff, src[k]);
      }
    }

    return true;
  }

  private static byte[,]? InvertMatrix(byte[,] matrix, int n) {
    // Augment with identity
    var aug = new byte[n, 2 * n];
    for (var i = 0; i < n; ++i) {
      for (var j = 0; j < n; ++j)
        aug[i, j] = matrix[i, j];
      aug[i, n + i] = 1;
    }

    // Forward elimination
    for (var col = 0; col < n; ++col) {
      // Find pivot
      var pivotRow = -1;
      for (var row = col; row < n; ++row) {
        if (aug[row, col] != 0) { pivotRow = row; break; }
      }

      if (pivotRow < 0) return null; // singular

      // Swap rows
      if (pivotRow != col) {
        for (var j = 0; j < 2 * n; ++j)
          (aug[col, j], aug[pivotRow, j]) = (aug[pivotRow, j], aug[col, j]);
      }

      // Scale pivot row
      var pivotVal = aug[col, col];
      var pivotInv = GfDiv(1, pivotVal);
      for (var j = 0; j < 2 * n; ++j)
        aug[col, j] = GfMul(aug[col, j], pivotInv);

      // Eliminate column
      for (var row = 0; row < n; ++row) {
        if (row == col) continue;
        var factor = aug[row, col];
        if (factor == 0) continue;
        for (var j = 0; j < 2 * n; ++j)
          aug[row, j] ^= GfMul(factor, aug[col, j]);
      }
    }

    // Extract inverse
    var inv = new byte[n, n];
    for (var i = 0; i < n; ++i)
      for (var j = 0; j < n; ++j)
        inv[i, j] = aug[i, n + j];

    return inv;
  }
}
