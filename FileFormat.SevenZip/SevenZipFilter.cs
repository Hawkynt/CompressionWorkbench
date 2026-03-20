namespace FileFormat.SevenZip;

/// <summary>
/// Specifies an optional pre-filter applied before compression when writing a 7z archive.
/// </summary>
public enum SevenZipFilter {
  /// <summary>No filter.</summary>
  None = 0,

  /// <summary>Copy filter (pass-through, codec 0x00).</summary>
  Copy = 1,

  /// <summary>BCJ x86 filter (codec 0x03030103).</summary>
  BcjX86 = 2,

  /// <summary>BCJ ARM filter (codec 0x03030501).</summary>
  BcjArm = 3,

  /// <summary>BCJ ARM Thumb filter (codec 0x03030701).</summary>
  BcjArmThumb = 4,

  /// <summary>BCJ PowerPC filter (codec 0x03030205).</summary>
  BcjPowerPC = 5,

  /// <summary>BCJ SPARC filter (codec 0x03030805).</summary>
  BcjSparc = 6,

  /// <summary>BCJ IA-64 filter (codec 0x03030401).</summary>
  BcjIA64 = 7,

  /// <summary>Delta filter (codec 0x03).</summary>
  Delta = 8,

  /// <summary>BCJ2 x86 filter with 4 sub-streams (codec 0x0303011B).</summary>
  Bcj2 = 9,
}
