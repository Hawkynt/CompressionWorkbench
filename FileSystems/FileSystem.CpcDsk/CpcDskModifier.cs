#pragma warning disable CS1591
using static FileSystem.CpcDsk.CpcDskAmsdos;

namespace FileSystem.CpcDsk;

/// <summary>
/// Adds and removes files on an Amstrad CPC disk image.
/// </summary>
/// <remarks>
/// <para>Both verbs read the disk's files, change the set, and lay the disk down
/// again. On a filesystem whose whole capacity is 180 kilobytes that costs
/// nothing worth saving, and it is the only way the directory and the data can
/// be guaranteed to still agree afterwards: CP/M records a file as a chain of
/// directory entries carrying block numbers, so editing one file in place means
/// editing an allocation that other files' entries are numbered against.</para>
///
/// <para>Editing in place is what the previous implementation did, against a
/// block numbering of its own invention, and the disks it produced named their
/// files correctly while pointing them at the wrong bytes.</para>
/// </remarks>
public static class CpcDskModifier {

  /// <summary>Every file the disk holds, with its bytes.</summary>
  public static IEnumerable<(string Name, byte[] Data)> EnumerateLogicalFiles(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var reader = new CpcDskReader(image);
    foreach (var entry in reader.Entries)
      yield return (entry.Name, reader.Extract(entry));
  }

  /// <summary>Puts a file on the disk, replacing one of the same name.</summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var (files, tracks, sides, sectorsPerTrack, sectorSize) = Read(image);
    var (basePart, extPart) = SplitName(name);
    var canonical = JoinName(basePart, extPart);

    files.RemoveAll(f => string.Equals(f.Name, canonical, StringComparison.OrdinalIgnoreCase));
    files.Add((canonical, data));
    Write(image, files, tracks, sides, sectorsPerTrack, sectorSize);
  }

  /// <summary>Takes a file off the disk. False when it was not there.</summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    _ = wipeData;   // laying the disk down again leaves nothing of it behind either way

    var (files, tracks, sides, sectorsPerTrack, sectorSize) = Read(image);
    var (basePart, extPart) = SplitName(name);
    var canonical = JoinName(basePart, extPart);

    var removed = files.RemoveAll(f =>
      string.Equals(f.Name, canonical, StringComparison.OrdinalIgnoreCase)
      || string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    if (removed == 0) return false;

    Write(image, files, tracks, sides, sectorsPerTrack, sectorSize);
    return true;
  }

  private static (List<(string Name, byte[] Data)> Files, int Tracks, int Sides,
      int SectorsPerTrack, int SectorSize) Read(Stream image) {
    image.Position = 0;
    var reader = new CpcDskReader(image);
    var geometry = reader.Layout;

    var files = reader.Entries
      .Select(e => (e.Name, Data: reader.Extract(e)))
      .ToList();

    return (files,
      reader.Tracks > 0 ? reader.Tracks : 40,
      reader.Sides > 0 ? reader.Sides : 1,
      geometry?.SectorsPerTrackCount ?? SectorsPerTrack,
      geometry?.SectorBytes ?? SectorSize);
  }

  private static void Write(Stream image, List<(string Name, byte[] Data)> files,
      int tracks, int sides, int sectorsPerTrack, int sectorSize) {
    using var staged = new MemoryStream();
    using (var writer = new CpcDskWriter(staged, leaveOpen: true, tracks: tracks, sides: sides,
             sectorsPerTrack: sectorsPerTrack, sectorSize: sectorSize)) {
      foreach (var (name, data) in files) writer.AddFile(name, data);
      writer.Finish();
    }

    image.Position = 0;
    image.SetLength(staged.Length);
    staged.Position = 0;
    staged.CopyTo(image);
    image.Flush();
  }
}
