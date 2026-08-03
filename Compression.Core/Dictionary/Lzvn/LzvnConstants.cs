namespace Compression.Core.Dictionary.Lzvn;

/// <summary>
/// Constants for the LZVN block format: a byte-oriented opcode LZ77 stream in the
/// spirit of Apple's LZVN ("Lempel-Ziv Variable-length iNteger") codec shipped
/// alongside LZFSE for fast, low-ratio compression of small buffers and Mach-O
/// pages.
/// </summary>
/// <remarks>
/// Apple never published a format specification for LZVN; the only authoritative
/// description of its opcode table lives in the closed-source encoder/decoder
/// shipped inside the (also Apple-published) lzfse repository
/// (https://github.com/lzfse/lzfse), and reverse-engineering write-ups such as
/// https://blog.yossarian.net/2021/06/01/Playing-with-Apples-weird-compression-formats
/// describe its general shape: single-byte opcodes that combine a literal run with
/// a following match, and a match distance that is encoded in 1, 2 or more bytes
/// depending on magnitude so that nearby matches cost less than far ones.
/// Reproducing Apple's exact opcode table byte-for-byte would require transcribing
/// their source, which the clean-room policy for this repository forbids. This
/// building block instead implements an original opcode stream that follows the
/// same documented shape (single-byte token combining literal-run and match-length
/// nibbles, tiered 1/2/5-byte distance encoding) so it demonstrates the same class
/// of format without claiming bit-for-bit compatibility with Apple's real LZVN
/// bitstream.
/// </remarks>
public static class LzvnConstants {
  /// <summary>Minimum length of a dictionary match worth encoding.</summary>
  public const int MinMatch = 3;

  /// <summary>Literal-length nibble value meaning "read extended continuation bytes".</summary>
  public const int LiteralExtended = 15;

  /// <summary>Match-length nibble value meaning "read extended continuation bytes".</summary>
  public const int MatchExtended = 14;

  /// <summary>Match-length nibble value meaning "no match follows" (trailing literal-only token).</summary>
  public const int MatchNone = 15;

  /// <summary>Maximum literal-length value directly encodable in the token nibble.</summary>
  public const int MaxDirectLiteral = 14;

  /// <summary>Maximum match-length field value (relative to <see cref="MinMatch"/>) directly encodable in the token nibble.</summary>
  public const int MaxDirectMatch = 13;

  /// <summary>Distance tier 1 (1-byte) upper bound: distances 1..128.</summary>
  public const int DistanceTier1Max = 128;

  /// <summary>Distance tier 2 (2-byte) upper bound: distances 129..32640.</summary>
  public const int DistanceTier2Max = 32640;

  /// <summary>First byte value marking the 5-byte (raw 32-bit) distance tier.</summary>
  public const byte DistanceTier3Marker = 0xFF;
}
