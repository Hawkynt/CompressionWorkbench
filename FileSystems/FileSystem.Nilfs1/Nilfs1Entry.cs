#pragma warning disable CS1591
namespace FileSystem.Nilfs1;

public sealed class Nilfs1Entry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public byte[] Data { get; init; } = [];

  /// <summary>
  /// Where the entry's bytes live in the image, for entries the reader leaves in
  /// place rather than copying. -1 when <see cref="Data" /> carries them.
  /// </summary>
  public long Offset { get; init; } = -1;
}
