#pragma warning disable CS1591
namespace FileSystem.Adfs;

/// <summary>Directory entry from an Acorn ADFS volume.</summary>
public sealed class AdfsEntry {
    /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
    /// <summary>
  /// Gets or sets the start sector.
  /// </summary>
public uint StartSector { get; init; }
    /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
public bool IsDirectory { get; init; }
    /// <summary>
  /// Gets or sets the load address.
  /// </summary>
public uint LoadAddress { get; init; }
    /// <summary>
  /// Gets or sets the exec address.
  /// </summary>
public uint ExecAddress { get; init; }
    /// <summary>
  /// Gets or sets the attributes.
  /// </summary>
public byte Attributes { get; init; }

  /// <summary>
  /// Indirect disc address on a new-map volume: the fragment id in the high
  /// bits, a share offset in the low byte. Zero on an old-map volume, where
  /// <see cref="StartSector" /> locates the object instead.
  /// </summary>
  public uint IndirectAddress { get; init; }
}
