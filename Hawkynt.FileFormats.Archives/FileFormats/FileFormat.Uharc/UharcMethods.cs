namespace FileFormat.Uharc;

/// <summary>
/// UHARC 0.6 stream method numbers (header byte +10). Each compressing mode owns a band of 16
/// methods; the offset inside the band is <c>log2(dictionary) - 10</c>.
/// </summary>
internal static class UharcMethods {

  public const int Store = 0;
  public const int Rle = 1;
  public const int Lz78First = 2;
  public const int Lz78Last = 7;
  public const int LzpFirst = 8;
  public const int LzpLast = 23;
  public const int PpmFirst = 24;
  public const int PpmLast = 39;
  public const int AlzFirst = 40;
  public const int AlzLast = 87;

  /// <summary>Method of every compressed directory table: LZP with a 512 KiB window.</summary>
  public const int Table = 17;

  /// <summary>The <c>uharc l</c> style name of <paramref name="method"/>.</summary>
  public static string Name(int method) => method switch {
    Store => "Store",
    Rle => "RLE",
    >= Lz78First and <= Lz78Last => "LZ78",
    >= LzpFirst and <= LzpLast => "LZP",
    >= PpmFirst and <= PpmLast => "PPM",
    >= AlzFirst and <= AlzLast => $"ALZ:{(method - AlzFirst) / 16 + 1}",
    _ => $"#{method}",
  };

  /// <summary>Dictionary size in bytes of a dictionary method, 0 for the others.</summary>
  public static int DictionarySize(int method) => method switch {
    >= LzpFirst and <= AlzLast => 1 << ((method - LzpFirst) % 16 + 10),
    _ => 0,
  };
}
