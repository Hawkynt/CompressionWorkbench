namespace Compression.Core.Dictionary.Lzturbo;

/// <summary>
/// Constants for the LZTURBO-inspired block format.
/// </summary>
/// <remarks>
/// LZTURBO, by powturbo, is closed-source: its command-line tool and the
/// TurboBench harness (https://github.com/powturbo/TurboBench) document that it
/// wraps each compressed buffer in a block carrying a method byte and the
/// original/compressed lengths, and that its speed comes from a hash-based LZ77
/// front end whose output is optionally entropy-coded by a selectable back end
/// (Huffman/rANS/etc., chosen by compression level). No byte-level opcode or
/// entropy-table format has ever been published, and the reference binaries are
/// not consulted here, so only that documented outer shape - block magic, method
/// byte, original length, compressed length - is reproduced. The inner token
/// stream implemented below is this project's own clean-room fast-LZ design
/// (literal/match token with extended-length continuation and a 3-byte window
/// offset) representing the front-end architecture; the proprietary entropy
/// back end is not reproduced; <see cref="Method"/> documents that the payload
/// is left entropy-uncoded. This building block therefore models the documented
/// LZTURBO block scheme, not a byte-compatible LZTURBO stream.
/// </remarks>
public static class LzturboConstants {
  /// <summary>4-byte block magic identifying this format.</summary>
  public static readonly byte[] Magic = "LZT1"u8.ToArray();

  /// <summary>Method byte for the only implemented variant: fast-LZ front end, no entropy back end.</summary>
  public const byte Method = 0;

  /// <summary>Size, in bytes, of the block header (magic + method + original length + body length).</summary>
  public const int HeaderSize = 13;

  /// <summary>Minimum length of a dictionary match worth encoding.</summary>
  public const int MinMatch = 4;

  /// <summary>Literal-length nibble value meaning "read extended continuation bytes".</summary>
  public const int LiteralExtended = 15;

  /// <summary>Maximum literal-length value directly encodable in the token nibble.</summary>
  public const int MaxDirectLiteral = 14;

  /// <summary>Match-length nibble value meaning "read extended continuation bytes".</summary>
  public const int MatchExtended = 14;

  /// <summary>Match-length nibble value meaning "no match follows" (trailing literal-only token).</summary>
  public const int MatchNone = 15;

  /// <summary>Maximum match-length field value (relative to <see cref="MinMatch"/>) directly encodable in the token nibble.</summary>
  public const int MaxDirectMatch = 13;

  /// <summary>Width, in bytes, of the little-endian window offset following a match token.</summary>
  public const int DistanceBytes = 3;
}
