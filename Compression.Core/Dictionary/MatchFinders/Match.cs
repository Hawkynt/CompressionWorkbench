namespace Compression.Core.Dictionary.MatchFinders;

/// <summary>
/// Represents a match found in the sliding window.
/// </summary>
/// <param name="Distance">The distance back from the current position (1-based).</param>
/// <param name="Length">The length of the match in bytes.</param>
public readonly record struct Match(int Distance, int Length);
