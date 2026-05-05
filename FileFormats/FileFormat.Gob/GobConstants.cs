namespace FileFormat.Gob;

internal static class GobConstants {
  // Trailing 0x20 (space) is part of the spec — distinguishes Jedi Knight GOB from
  // Dark Forces GOB v1 which uses a different magic. Tools must NOT write 0x00 here.
  internal static readonly byte[] Magic = "GOB "u8.ToArray();

  /// <summary>12-byte file header: magic(4) + version(4) + dirOffset(4).</summary>
  internal const int HeaderSize = 12;

  /// <summary>136-byte directory entry: offset(4) + size(4) + name(128).</summary>
  internal const int DirectoryEntrySize = 136;

  /// <summary>Size of the fixed-width name field within a directory entry (null-padded).</summary>
  internal const int NameFieldSize = 128;

  /// <summary>Maximum name length in bytes — 127 + 1 for the mandatory null terminator within a 128-byte field.</summary>
  internal const int MaxNameLength = NameFieldSize - 1;

  /// <summary>Default version written by GobWriter (Jedi Knight era).</summary>
  internal const uint DefaultVersion = 0x14;

  /// <summary>Alternate version observed in some Outlaws builds — accepted on read.</summary>
  internal const uint AlternateVersion = 0x20;
}
