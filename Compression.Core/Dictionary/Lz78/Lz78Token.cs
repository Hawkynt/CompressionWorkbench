namespace Compression.Core.Dictionary.Lz78;

/// <summary>
/// Represents a single LZ78 output token.
/// </summary>
/// <param name="DictionaryIndex">Index of the matching dictionary entry (0 = empty string).</param>
/// <param name="NextByte">The next byte after the match, or null for the terminal token.</param>
public readonly record struct Lz78Token(int DictionaryIndex, byte? NextByte);
