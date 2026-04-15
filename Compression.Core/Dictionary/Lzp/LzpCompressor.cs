namespace Compression.Core.Dictionary.Lzp;

/// <summary>
/// LZP (Lempel-Ziv Prediction) compressor. Predicts the next byte from a context hash;
/// if the prediction matches, emits a match bit; if it misses, emits a literal byte.
/// </summary>
/// <remarks>
/// Output format: 4-byte original size (LE) + 1-byte order, followed by groups of 8
/// decisions. Each group starts with a flag byte where bit 0 corresponds to the first
/// decision: 1 = match (predicted byte is correct, no extra data), 0 = literal (the
/// actual byte follows inline). The hash table maps context hashes to predicted byte
/// values, updated after every decision.
/// </remarks>
public static class LzpCompressor {
  private const int HashBits = 20;
  private const int HashSize = 1 << HashBits;
  private const uint HashMask = HashSize - 1;

  /// <summary>
  /// Compresses data using LZP with the specified context order.
  /// </summary>
  /// <param name="input">The data to compress.</param>
  /// <param name="order">The context order (number of preceding bytes used for prediction). Must be at least 1.</param>
  /// <returns>The compressed byte array including a header.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="input"/> is null.</exception>
  /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="order"/> is less than 1 or greater than 255.</exception>
  public static byte[] Compress(byte[] input, int order = 3) {
    ArgumentNullException.ThrowIfNull(input);
    if (order is < 1 or > 255)
      throw new ArgumentOutOfRangeException(nameof(order), "Order must be between 1 and 255.");

    if (input.Length == 0) {
      // Header only: size 0 + order byte.
      var empty = new byte[5];
      empty[4] = (byte)order;
      return empty;
    }

    var hashTable = new byte[HashSize];
    using var ms = new MemoryStream();

    // Write header: original size (4 bytes LE) + order (1 byte).
    ms.Write(BitConverter.GetBytes(input.Length));
    ms.WriteByte((byte)order);

    var pos = 0;
    while (pos < input.Length) {
      // Collect up to 8 decisions in a group.
      byte flags = 0;
      var literals = new MemoryStream();
      var count = Math.Min(8, input.Length - pos);

      for (var bit = 0; bit < count; bit++) {
        if (pos < order) {
          // Not enough context yet — always a literal.
          literals.WriteByte(input[pos]);
          pos++;
          continue;
        }

        var hash = ComputeHash(input, pos, order);
        var predicted = hashTable[hash];

        if (predicted == input[pos]) {
          // Match — set bit.
          flags |= (byte)(1 << bit);
        } else {
          // Literal — emit the byte.
          literals.WriteByte(input[pos]);
        }

        hashTable[hash] = input[pos];
        pos++;
      }

      ms.WriteByte(flags);
      literals.WriteTo(ms);
    }

    return ms.ToArray();
  }

  /// <summary>
  /// Compresses data using LZP with the specified context order.
  /// </summary>
  /// <param name="input">The data to compress.</param>
  /// <param name="order">The context order (number of preceding bytes used for prediction). Must be at least 1.</param>
  /// <returns>The compressed byte array including a header.</returns>
  /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="order"/> is less than 1 or greater than 255.</exception>
  public static byte[] Compress(ReadOnlySpan<byte> input, int order = 3)
    => Compress(input.ToArray(), order);

  /// <summary>
  /// Computes a 20-bit FNV-1a hash of the <paramref name="order"/> bytes preceding position <paramref name="pos"/>.
  /// </summary>
  internal static int ComputeHash(byte[] data, int pos, int order) {
    var h = 2166136261u;
    for (var i = pos - order; i < pos; i++) {
      h ^= data[i];
      h *= 16777619u;
    }

    return (int)(h & HashMask);
  }
}
