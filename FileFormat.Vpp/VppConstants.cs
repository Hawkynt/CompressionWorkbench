namespace FileFormat.Vpp;

internal static class VppConstants {
  /// <summary>Magic UInt32 LE: 0xCE0A8951 — Volition Package signature.</summary>
  internal const uint Magic = 0xCE0A8951u;

  /// <summary>Only v1 (Red Faction 1 / Summoner) is supported by this reader/writer.</summary>
  internal const uint SupportedVersion = 1u;

  /// <summary>VPP_PC layout aligns header, index block, and every file payload to 2048 bytes.</summary>
  internal const int Alignment = 2048;

  /// <summary>64-byte index entry: name(60) + size(4).</summary>
  internal const int IndexEntrySize = 64;

  /// <summary>Header occupies the entire first alignment block.</summary>
  internal const int HeaderSize = Alignment;

  /// <summary>Index entry name field — the last byte must remain a zero terminator.</summary>
  internal const int NameFieldSize = 60;

  /// <summary>Maximum usable name length in bytes (NameFieldSize minus the mandatory null terminator).</summary>
  internal const int MaxNameLength = NameFieldSize - 1;
}
