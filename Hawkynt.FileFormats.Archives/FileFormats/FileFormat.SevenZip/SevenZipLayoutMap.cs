#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.SevenZip;

/// <summary>
/// Walks a 7z archive and emits the byte-level layout: signature header,
/// each solid block (packed data), the packed header when the header is stored
/// compressed or encrypted, and the header at the end.
/// </summary>
/// <remarks>
/// Whatever this map does not cover is dead space a wipe may zero, so it errs on the side of
/// keeping: a header it cannot decode (encrypted, no password) marks all packed data as used
/// rather than leaving it unclaimed.
/// </remarks>
public static class SevenZipLayoutMap {

  /// <summary>
  /// Enumerates the live regions of the archive in <paramref name="archive"/>.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    return Collect(archive);
  }

  private static List<DefragBlockInfo> Collect(Stream archive) {
    var blocks = new List<DefragBlockInfo>();
    archive.Position = 0;
    if (archive.Length < SevenZipConstants.SignatureHeaderSize)
      return blocks;

    blocks.Add(new(0, SevenZipConstants.SignatureHeaderSize, DefragBlockKind.MetadataReserved, FileName: "7z Signature Header"));

    SevenZipHeader sigHeader;
    try {
      archive.Position = 0;
      sigHeader = SevenZipHeader.Read(archive);
    } catch (Exception e) when (e is InvalidDataException or EndOfStreamException) {
      return blocks;
    }
    if (sigHeader.NextHeaderSize <= 0)
      return blocks;

    var nextHeaderPos = SevenZipConstants.SignatureHeaderSize + sigHeader.NextHeaderOffset;
    var header = new DefragBlockInfo(nextHeaderPos, sigHeader.NextHeaderSize, DefragBlockKind.MetadataReserved, FileName: "7z Metadata");
    byte[] raw;
    try {
      archive.Position = nextHeaderPos;
      raw = new byte[sigHeader.NextHeaderSize];
      archive.ReadExactly(raw);
    } catch (Exception e) when (e is EndOfStreamException or ArgumentException or OverflowException or OutOfMemoryException) {
      blocks.Add(header);
      return blocks;
    }

    // A packed header lives among the packed streams; it is metadata a wipe must keep.
    try {
      if (SevenZipHeaderCodec.ReadEncodedHeaderPackInfo(raw) is { } encoded) {
        var offsets = SevenZipHeaderCodec.PackStreamOffsets(encoded);
        for (var i = 0; i < offsets.Length; ++i)
          blocks.Add(new(SevenZipConstants.SignatureHeaderSize + offsets[i], encoded.PackSizes[i], DefragBlockKind.MetadataReserved, FileName: "7z Packed Header"));
      }
    } catch (Exception e) when (e is InvalidDataException or EndOfStreamException or NotSupportedException) {
      // Unreadable even as a descriptor: keep everything below the header.
      blocks.Add(new(SevenZipConstants.SignatureHeaderSize, sigHeader.NextHeaderOffset, DefragBlockKind.Used, FileName: "7z packed data (layout unknown)"));
      blocks.Add(header);
      return blocks;
    }

    SevenZipDatabase database;
    try {
      database = SevenZipHeaderCodec.ReadDatabase(new MemoryStream(raw, writable: false), archive, new object(), new SevenZipKeyCache(null));
    } catch (Exception e) when (e is InvalidDataException or EndOfStreamException or NotSupportedException or UnauthorizedAccessException or ArgumentException) {
      // An encrypted or damaged header hides the folders: keep all packed data.
      blocks.Add(new(SevenZipConstants.SignatureHeaderSize, sigHeader.NextHeaderOffset, DefragBlockKind.Used, FileName: "7z packed data (layout unknown)"));
      blocks.Add(header);
      return blocks;
    }

    var (packInfo, folders, subStreams, fileInfos) = database;
    var packOffsets = SevenZipHeaderCodec.PackStreamOffsets(packInfo);
    var packStreamIndex = 0;
    var subStreamIndex = 0;
    var withData = fileInfos.Where(static f => !f.IsEmptyStream).ToList();
    for (var fi = 0; fi < folders.Count; fi++) {
      var folder = folders[fi];
      var numPack = folder.NumPackStreams;
      var fileCount = subStreams.NumUnpackStreams[fi];
      var names = withData.Skip(subStreamIndex).Take(fileCount).Select(static f => f.Name).ToList();
      var blockName = names.Count switch {
        0 => $"Solid block {fi}",
        1 => names[0],
        _ => $"Solid block {fi} ({names.Count} files)",
      };

      for (var p = 0; p < numPack && packStreamIndex + p < packInfo.PackSizes.Length; p++) {
        var size = packInfo.PackSizes[packStreamIndex + p];
        if (size > 0)
          blocks.Add(new(SevenZipConstants.SignatureHeaderSize + packOffsets[packStreamIndex + p], size, DefragBlockKind.Used,
            FileName: numPack == 1 ? blockName : $"{blockName} [stream {p}]", Classification: ClassifyFolder(folder)));
      }

      packStreamIndex += numPack;
      subStreamIndex += fileCount;
    }

    blocks.Add(header);
    return blocks;
  }

  private static DefragBlockClass ClassifyFolder(SevenZipFolder folder) {
    foreach (var coder in folder.Coders) {
      if (coder.Is(SevenZipConstants.CodecLzma2) || coder.Is(SevenZipConstants.CodecLzma) || coder.Is(SevenZipConstants.CodecPpmd)) return DefragBlockClass.Hot;
      if (coder.Is(SevenZipConstants.CodecBzip2)) return DefragBlockClass.Cold;
      if (coder.Is(SevenZipConstants.CodecDeflate)) return DefragBlockClass.Normal;
      if (coder.Is(SevenZipConstants.CodecCopy)) return DefragBlockClass.Frozen;
    }
    return DefragBlockClass.Normal;
  }
}
