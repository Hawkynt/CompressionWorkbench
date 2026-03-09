namespace Compression.Core.Dictionary.MatchFinders;

/// <summary>
/// Interface for LZ77-style match finders.
/// </summary>
public interface IMatchFinder {
  /// <summary>
  /// Finds the best match for data at the specified position.
  /// </summary>
  /// <param name="data">The input data buffer.</param>
  /// <param name="position">The current position in the data.</param>
  /// <param name="maxDistance">The maximum distance to search back.</param>
  /// <param name="maxLength">The maximum match length to return.</param>
  /// <param name="minLength">The minimum match length to consider valid.</param>
  /// <returns>The best match found, or a match with length 0 if none found.</returns>
  Match FindMatch(ReadOnlySpan<byte> data, int position, int maxDistance, int maxLength, int minLength = 3);
}
