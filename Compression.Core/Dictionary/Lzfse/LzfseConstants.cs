namespace Compression.Core.Dictionary.Lzfse;

/// <summary>
/// Constants for the LZFSE-inspired block format.
/// </summary>
/// <remarks>
/// LZFSE is Apple's LZ77 + FSE (tANS) compressor, published as open source at
/// https://github.com/lzfse/lzfse with a format description in that repository's
/// FORMAT.md. Its defining idea - cited here as the design this building block
/// follows - is to split the LZ77 parse into separate literal, literal-length,
/// match-length and match-distance streams, encode literal bytes and each of the
/// three small "command" alphabets with FSE/tANS instead of Huffman, and let large
/// values escape a small symbol alphabet via extra bits/bytes rather than growing
/// the alphabet itself. This implementation reuses the project's existing tANS
/// engine (<see cref="Compression.Core.Entropy.Fse.FseEncoder"/> /
/// <see cref="Compression.Core.Entropy.Fse.FseDecoder"/>) for the entropy stage, as
/// directed, instead of writing a second FSE implementation. Apple's exact bucket
/// tables (which values map to which of ~20-64 symbols, and how many extra bits
/// each symbol carries) are not published outside their source and are not
/// reproduced; <see cref="ValueBucket"/> uses a simpler original bucketing (direct
/// values 0-30, symbol 31 = escape to a raw 32-bit value) that preserves the same
/// "small FSE-coded symbol plus overflow" shape. The block container (stream
/// lengths, overflow tables) is likewise an original design. This building block
/// is therefore LZFSE-shaped and round-trip correct, not a byte-compatible
/// implementation of Apple's real LZFSE bitstream.
/// </remarks>
public static class LzfseConstants {
  /// <summary>Minimum length of a dictionary match worth encoding.</summary>
  public const int MinMatch = 4;
}
