#pragma warning disable CS1591
namespace FileSystem.Adfs;

/// <summary>Directory entry from an Acorn ADFS volume.</summary>
public sealed class AdfsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public uint StartSector { get; init; }
  public bool IsDirectory { get; init; }
  public uint LoadAddress { get; init; }
  public uint ExecAddress { get; init; }
  public byte Attributes { get; init; }

  /// <summary>
  /// Indirect disc address on a new-map volume: the fragment id in the high
  /// bits, a share offset in the low byte. Zero on an old-map volume, where
  /// <see cref="StartSector" /> locates the object instead.
  /// </summary>
  public uint IndirectAddress { get; init; }
}
