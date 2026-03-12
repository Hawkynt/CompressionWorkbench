namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// Constants and state transition tables for the LZMA compression algorithm.
/// </summary>
internal static class LzmaConstants {
  /// <summary>Number of LZMA states (0..11).</summary>
  public const int NumStates = 12;

  /// <summary>Number of position slots for distance encoding (6-bit).</summary>
  public const int NumPosSlots = 64;

  /// <summary>Number of alignment bits for large distances.</summary>
  public const int NumAlignBits = 4;

  /// <summary>Alignment table size (1 &lt;&lt; NumAlignBits).</summary>
  public const int AlignTableSize = 1 << LzmaConstants.NumAlignBits;

  /// <summary>First position slot that uses extra bits via bit tree.</summary>
  public const int StartPosModelIndex = 4;

  /// <summary>First position slot that uses direct bits + alignment.</summary>
  public const int EndPosModelIndex = 14;

  /// <summary>Number of full distances (128).</summary>
  public const int NumFullDistances = 1 << (LzmaConstants.EndPosModelIndex / 2);

  /// <summary>Number of length-to-position-state mappings.</summary>
  public const int NumLenToPosStates = 4;

  /// <summary>Minimum match length.</summary>
  public const int MatchMinLen = 2;

  /// <summary>Number of repeat distances maintained.</summary>
  public const int NumRepDistances = 4;

  /// <summary>Maximum match length (2 + 8 + 8 + 256 - 1 = 273).</summary>
  public const int MatchMaxLen = LzmaConstants.MatchMinLen + 8 + 8 + 256 - 1;

  /// <summary>Returns the length-to-position state for a given length.</summary>
  public static int GetLenToPosState(int len) {
    len -= LzmaConstants.MatchMinLen;
    if (len < LzmaConstants.NumLenToPosStates)
      return len;
    return LzmaConstants.NumLenToPosStates - 1;
  }

  /// <summary>State transition after encoding a literal.</summary>
  public static int StateUpdateLiteral(int state) => state switch {
    0 or 1 or 2 or 3 or 4 or 5 or 6 => 0,
    7 => 4,
    8 => 5,
    9 => 6,
    10 => 4,
    11 => 5,
    _ => state,
  };

  /// <summary>State transition after encoding a match.</summary>
  public static int StateUpdateMatch(int state) => state switch {
    < 7 => 7,
    _ => 10,
  };

  /// <summary>State transition after encoding a rep match.</summary>
  public static int StateUpdateRep(int state) => state switch {
    < 7 => 8,
    _ => 11,
  };

  /// <summary>State transition after encoding a short rep (1 byte from rep0).</summary>
  public static int StateUpdateShortRep(int state) => state switch {
    < 7 => 9,
    _ => 11,
  };

  /// <summary>Returns true if the state represents a literal context.</summary>
  public static bool StateIsLiteral(int state) => state < 7;
}
