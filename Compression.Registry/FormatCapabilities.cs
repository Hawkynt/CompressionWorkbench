namespace Compression.Registry;

/// <summary>
/// Flags describing what operations a format supports.
/// </summary>
[Flags]
public enum FormatCapabilities {
  None = 0,
  CanList = 1 << 0,
  CanExtract = 1 << 1,
  CanCreate = 1 << 2,
  CanTest = 1 << 3,
  SupportsPassword = 1 << 4,
  SupportsMultipleEntries = 1 << 5,
  SupportsDirectories = 1 << 6,
  SupportsOptimize = 1 << 8,
  CanCompoundWithTar = 1 << 9,
}
