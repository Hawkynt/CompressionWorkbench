#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.ApplePascal;

/// <summary>
/// Walks an Apple Pascal volume and emits the on-disk byte layout:
/// boot blocks (0..1) as metadata-reserved, volume directory (blocks 2..5)
/// as metadata-reserved, each file's contiguous extent as a Used block. Any
/// remaining bytes are implicitly Free.
/// </summary>
public static class ApplePascalExtentMap {

  /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    // Boot + directory region = first 6 blocks.
    var dirEnd = 6L * ApplePascalReader.BlockSize;
    if (image.Length < dirEnd) yield break;
    yield return new DefragBlockInfo(0, dirEnd, DefragBlockKind.MetadataReserved);

    // Snapshot the image so we can parse without disturbing the caller.
    image.Position = 0;
    using var r = new ApplePascalReader(image);
    if (!r.ValidVolume) yield break;
    foreach (var e in r.Entries) {
      var startOff = (long)e.StartBlock * ApplePascalReader.BlockSize;
      var endOff = (long)e.EndBlock * ApplePascalReader.BlockSize;
      var length = endOff - startOff;
      if (length > 0)
        yield return new DefragBlockInfo(startOff, length, DefragBlockKind.Used, e.Name);
    }
  }
}
