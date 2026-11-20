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

  // UHARC 0.3 numbers its methods in bands of 14 dictionary sizes: 8..21 LZP, 22..35 PPM,
  // 36..77 ALZ:1..3 (78 and 79 continue the last band), 80 is another code for store.

  /// <summary>First method of 0.3's PPM band.</summary>
  public const int Ppm03First = 22;

  /// <summary>First method of 0.3's ALZ bands.</summary>
  public const int Alz03First = 36;

  /// <summary>Last method 0.3 defines.</summary>
  public const int Alz03Last = 79;

  /// <summary>0.3's second code for the store method.</summary>
  public const int Store03Alias = 80;

  /// <summary>The name of a UHARC 0.3 stream method.</summary>
  public static string Name03(int method) => method switch {
    Store or Store03Alias => "Store",
    Rle => "RLE",
    >= Lz78First and <= Lz78Last => "LZW",
    >= LzpFirst and < Ppm03First => "LZP",
    >= Ppm03First and < Alz03First => "PPM",
    >= Alz03First and <= Alz03Last => $"ALZ:{Math.Min((method - Alz03First) / 14, 2) + 1}",
    _ => $"#{method}",
  };

  /// <summary>Dictionary size in bytes of a UHARC 0.3 method (LZW: codes), 0 where none applies.</summary>
  public static int DictionarySize03(int method) => method switch {
    >= Lz78First and <= Lz78Last => 1 << (method + 8),
    >= LzpFirst and <= Alz03Last => 1 << ((method - LzpFirst) % 14 + 10),
    _ => 0,
  };
}
