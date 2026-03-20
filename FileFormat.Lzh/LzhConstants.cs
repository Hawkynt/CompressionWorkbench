namespace FileFormat.Lzh;

/// <summary>
/// Constants for the LHA/LZH archive format.
/// </summary>
public static class LhaConstants {
  /// <summary>Header level 0 marker.</summary>
  public const byte HeaderLevel0 = 0;

  /// <summary>Header level 1 marker.</summary>
  public const byte HeaderLevel1 = 1;

  /// <summary>Header level 2 marker.</summary>
  public const byte HeaderLevel2 = 2;

  /// <summary>Method: no compression (store).</summary>
  public const string MethodLh0 = "-lh0-";

  /// <summary>Method: 4KB window, dynamic Huffman (LHarc 1.x).</summary>
  public const string MethodLh1 = "-lh1-";

  /// <summary>Method: dynamic Huffman with 8KB window (LHA 2.x).</summary>
  public const string MethodLh2 = "-lh2-";

  /// <summary>Method: static Huffman with 8KB window (transitional).</summary>
  public const string MethodLh3 = "-lh3-";

  /// <summary>Method: LZH compression with 4KB window (same tree format as lh5+).</summary>
  public const string MethodLh4 = "-lh4-";

  /// <summary>Method: LZH compression with 8KB window.</summary>
  public const string MethodLh5 = "-lh5-";

  /// <summary>Method: LZH compression with 32KB window.</summary>
  public const string MethodLh6 = "-lh6-";

  /// <summary>Method: LZH compression with 64KB window.</summary>
  public const string MethodLh7 = "-lh7-";

  /// <summary>Method: directory marker.</summary>
  public const string MethodLhd = "-lhd-";

  /// <summary>Method: store (same as lh0, legacy).</summary>
  public const string MethodLz4 = "-lz4-";

  /// <summary>Method: LZSS with 4KB window (no Huffman).</summary>
  public const string MethodLzs = "-lzs-";

  /// <summary>Method: LZSS with 4KB window (LArc variant).</summary>
  public const string MethodLz5 = "-lz5-";

  /// <summary>Method: PMA store (no compression).</summary>
  public const string MethodPm0 = "-pm0-";

  /// <summary>Method: PMA PPMd order-2 compression.</summary>
  public const string MethodPm1 = "-pm1-";

  /// <summary>Method: PMA PPMd order-3 compression.</summary>
  public const string MethodPm2 = "-pm2-";
}
