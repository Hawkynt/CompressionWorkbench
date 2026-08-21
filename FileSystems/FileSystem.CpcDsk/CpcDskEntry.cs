#pragma warning disable CS1591
namespace FileSystem.CpcDsk;

/// <summary>
/// One file on an Amstrad CPC disk, as its AMSDOS directory entry describes it.
/// </summary>
public sealed class CpcDskEntry {
  /// <summary>The file's name, in CP/M's eight-and-three.</summary>
  public string Name { get; init; } = "";

  /// <summary>Track its first block starts on (0-based).</summary>
  public int Track { get; init; }

  /// <summary>Side its first block starts on (0 or 1).</summary>
  public int Side { get; init; }

  /// <summary>Id of the sector its first block starts at; DATA-format disks run from &amp;C1.</summary>
  public byte SectorId { get; init; }

  /// <summary>
  /// The file's length in bytes, which CP/M records only as a count of 128-byte
  /// records — so it is the written length rounded up to the next record.
  /// </summary>
  public int Size { get; init; }

  /// <summary>Absolute byte offset of the file's first block within the DSK stream.</summary>
  internal long DataOffset { get; init; }

  /// <summary>The allocation blocks the directory gives it, in file order.</summary>
  internal IReadOnlyList<int> Blocks { get; init; } = [];
}
