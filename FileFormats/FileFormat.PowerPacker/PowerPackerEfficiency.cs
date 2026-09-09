namespace FileFormat.PowerPacker;

/// <summary>
/// Historical PowerPacker efficiency presets. The four values are the number
/// of bits used for offsets in match classes 0 through 3.
/// </summary>
public enum PowerPackerEfficiency {
  /// <summary>Fast preset: 9/9/9/9.</summary>
  Fast,
  /// <summary>Mediocre preset: 9/10/10/10.</summary>
  Mediocre,
  /// <summary>Good preset: 9/10/11/11 (the traditional default).</summary>
  Good,
  /// <summary>Very-good preset: 9/10/12/12.</summary>
  VeryGood,
  /// <summary>Best preset: 9/10/12/13.</summary>
  Best,
}
