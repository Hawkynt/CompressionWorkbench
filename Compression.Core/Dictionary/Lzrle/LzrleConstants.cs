namespace Compression.Core.Dictionary.Lzrle;

/// <summary>
/// Constants for the LZRLE (run-length-augmented LZ) block format.
/// </summary>
/// <remarks>
/// LZRLE is a clean-room design that augments a classic literal/match LZ77 token
/// stream with a dedicated third token type for runs of a single repeated byte value,
/// the same concept popularised by the LZO-RLE variant merged into the Linux kernel
/// (used by zram since 5.1) to cheaply cover the long zero-filled runs common in RAM
/// pages: see the kernel LZO documentation (https://docs.kernel.org/staging/lzo.html)
/// and the LZO-RLE patch discussion (https://lwn.net/Articles/778510/). The byte-level
/// token layout below is an original design, not a reproduction of LZO1X's opcode
/// table (which uses context-dependent "first instruction" state that is intricate to
/// reproduce byte-exactly without transcribing the reference implementation).
/// A run token costs one type/length byte plus one value byte, versus a match token's
/// type/length byte plus a 4-byte distance, so long runs of any repeated byte (not
/// just zero) are always cheaper to encode as a run than as a self-referencing match.
/// </remarks>
public static class LzrleConstants {
  /// <summary>Minimum length of a dictionary match worth encoding.</summary>
  public const int MinMatch = 4;

  /// <summary>Minimum length of a repeated-byte run worth encoding.</summary>
  public const int MinRun = 4;

  /// <summary>Token type: literal run follows.</summary>
  public const int TypeLiteral = 0;

  /// <summary>Token type: dictionary match (4-byte little-endian distance follows).</summary>
  public const int TypeMatch = 1;

  /// <summary>Token type: repeated-byte run (single value byte follows).</summary>
  public const int TypeRun = 2;

  /// <summary>Number of bits reserved for the token length field.</summary>
  public const int LengthFieldBits = 6;

  /// <summary>Sentinel length-field value meaning "read extended continuation bytes".</summary>
  public const int LengthFieldMax = (1 << LzrleConstants.LengthFieldBits) - 1;
}
