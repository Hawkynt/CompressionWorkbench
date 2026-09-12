namespace FileFormat.Rar;

/// <summary>Writer generation targeted by RAR archive creation.</summary>
public enum RarCompatibility {
  /// <summary>RAR 5.x container and codec generation.</summary>
  Rar5,

  /// <summary>RAR 2.9-4.x container/codec generation written by the managed RAR4 writer.</summary>
  Rar4,

  /// <summary>
  /// RAR 1.50 compatibility: classic <c>Rar! 1A 07 00</c> container with <c>UNP_VER=15</c>.
  /// The current writer deliberately supports stored members only for this target.
  /// </summary>
  Rar1_5,
}
