namespace FileFormat.Lfd;

internal static class LfdConstants {
  /// <summary>16-byte resource header: type(4) + name(8) + size(4).</summary>
  internal const int HeaderSize = 16;

  /// <summary>Type field width in bytes (NUL-padded ASCII).</summary>
  internal const int TypeFieldSize = 4;

  /// <summary>Name field width in bytes (NUL-padded ASCII).</summary>
  internal const int NameFieldSize = 8;

  /// <summary>Resource map entry type literal. The RMAP entry sits at file offset 0 and indexes every other entry.</summary>
  internal const string RmapType = "RMAP";
}
