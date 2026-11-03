namespace Compression.Core.Dictionary.Ace;

/// <summary>Constants of ACE 1.0 / 2.0 compression (values per the ACE technical notes and acefile).</summary>
public static class AceConstants {
  /// <summary>Compression type: stored.</summary>
  public const int CompStore = 0;

  /// <summary>Compression type: ACE 1.0 LZ77.</summary>
  public const int CompAce10 = 1;

  /// <summary>Compression type: ACE 2.0 blocked (LZ77, delta, exe, sound and picture modes).</summary>
  public const int CompAce20 = 2;

  /// <summary>Smallest dictionary, in bits.</summary>
  public const int MinDictBits = 10;

  /// <summary>Largest dictionary, in bits.</summary>
  public const int MaxDictBits = 22;

  /// <summary>Dictionary size the writer uses unless told otherwise, in bits.</summary>
  public const int DefaultDictBits = 20;

  internal const int MinDictSize = 1 << MinDictBits;
  internal const int MaxDictSize = 1 << MaxDictBits;

  internal const int Lz77MaxCodeWidth = 11;
  internal const int MaxDistAtLen2 = 255;
  internal const int MaxDistAtLen3 = 8191;
  internal const int Lz77TypeCode = 260 + MaxDictBits + 1;
  internal const int Lz77NumMainCodes = 260 + MaxDictBits + 2;
  internal const int Lz77NumLenCodes = 256 - 1;
  internal const int DistHistSize = 4;

  internal const int ModeLz77 = 0;
  internal const int ModeLz77Delta = 1;
  internal const int ModeLz77Exe = 2;
  internal const int ModeSound8 = 3;
  internal const int ModeSound16 = 4;
  internal const int ModeSound32A = 5;
  internal const int ModeSound32B = 6;
  internal const int ModePic = 7;
}
