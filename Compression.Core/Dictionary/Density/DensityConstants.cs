namespace Compression.Core.Dictionary.Density;

/// <summary>
/// Constants for the Density "Chameleon" block format.
/// </summary>
/// <remarks>
/// Chameleon is the fastest, lowest-ratio algorithm in Guillaume Voirin's Density
/// library (https://github.com/g1mv/density). Unlike a conventional LZ77 matcher it
/// never transmits a match distance at all: it keeps a dictionary of predicted
/// 4-byte "chunks" indexed by a hash of the *previous* chunk, and for each new
/// chunk simply signals whether the dictionary's standing prediction for that hash
/// bucket was correct. Because both encoder and decoder update the same dictionary
/// from the same deterministic hash-of-previous-chunk rule, a correct prediction
/// costs a single flag bit and zero payload bytes. This structure - and the fact
/// that it operates on 4-byte units with a signature word flagging each unit - is
/// described in Charles Bloom's analysis of the algorithm
/// (http://cbloomrants.blogspot.com/2015/03/03-25-15-density-chameleon.html) and in
/// the Density project README. The concrete hash function and table size below are
/// this implementation's own choice (Density's C source is not consulted or
/// transcribed), tuned only to satisfy the "32-bit hash dictionary, 4-byte-unit
/// signature-flagged predictions" shape of the published algorithm.
/// </remarks>
public static class DensityConstants {
  /// <summary>Size, in bytes, of one dictionary unit (chunk).</summary>
  public const int ChunkSize = 4;

  /// <summary>Number of chunks covered by a single 32-bit signature word.</summary>
  public const int ChunksPerBlock = 32;

  /// <summary>Log2 of the prediction dictionary size.</summary>
  public const int HashBits = 16;

  /// <summary>Number of entries in the prediction dictionary.</summary>
  public const int HashSize = 1 << DensityConstants.HashBits;

  /// <summary>Multiplicative hash constant (Knuth's 32-bit golden-ratio constant).</summary>
  public const uint HashMultiplier = 2654435761;
}
