#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.SevenZip;

/// <summary>
/// Walks a 7z archive and emits the byte-level layout: signature header,
/// each solid block (packed data), and the compressed metadata at the end.
/// </summary>
public static class SevenZipLayoutMap {

  /// <summary>
  /// Enumerates the value.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;

    if (archive.Length < SevenZipConstants.SignatureHeaderSize)
      yield break;

    // 32-byte signature header → MetadataReserved
    yield return new DefragBlockInfo(
      0,
      SevenZipConstants.SignatureHeaderSize,
      DefragBlockKind.MetadataReserved,
      FileName: "7z Signature Header");

    SevenZipHeader? sigHeader = null;
    try {
      archive.Position = 0;
      sigHeader = SevenZipHeader.Read(archive);
    } catch {
      // Cannot yield inside catch.
    }

    if (sigHeader == null)
      yield break;

    // Parse the header to get pack info and folder structure
    SevenZipPackInfo? packInfo = null;
    List<SevenZipFolder>? folders = null;
    SevenZipSubStreamsInfo? subStreams = null;
    List<SevenZipFileInfo>? fileInfos = null;
    var parseFailed = false;
    try {
      var nextHeaderPos = SevenZipConstants.SignatureHeaderSize + sigHeader.NextHeaderOffset;
      archive.Position = nextHeaderPos;
      var nextHeaderData = new byte[sigHeader.NextHeaderSize];
      ReadExact(archive, nextHeaderData);
      using var headerStream = new MemoryStream(nextHeaderData);
      (packInfo, folders, subStreams, fileInfos) =
        SevenZipHeaderCodec.ReadHeader(headerStream, null, archiveStream: archive);
    } catch {
      parseFailed = true;
    }

    if (parseFailed) {
      // Still emit the next header tile even if parse fails
      if (sigHeader.NextHeaderSize > 0) {
        var nextPos = SevenZipConstants.SignatureHeaderSize + sigHeader.NextHeaderOffset;
        yield return new DefragBlockInfo(
          nextPos,
          sigHeader.NextHeaderSize,
          DefragBlockKind.MetadataReserved,
          FileName: "7z Metadata (parse failed)");
      }
      yield break;
    }

    // Emit solid blocks (packed data regions)
    var packBaseOffset = (long)SevenZipConstants.SignatureHeaderSize + packInfo!.PackPos;
    var packStreamIndex = 0;
    for (var fi = 0; fi < folders!.Count; fi++) {
      var folder = folders[fi];
      var totalIn = folder.Coders.Sum(c => c.NumInStreams);
      var numPack = totalIn - folder.BindPairs.Count;

      // Calculate actual offset for this folder's pack streams
      var folderOffset = packBaseOffset;
      for (var p = 0; p < packStreamIndex && p < packInfo.PackSizes.Length; p++)
        folderOffset += packInfo.PackSizes[p];

      long totalPackedSize = 0;
      var fileCount = fi < subStreams!.NumUnpackStreams.Length
        ? subStreams.NumUnpackStreams[fi]
        : 1;
      for (var p = 0; p < numPack && packStreamIndex + p < packInfo.PackSizes.Length; p++)
        totalPackedSize += packInfo.PackSizes[packStreamIndex + p];

      if (totalPackedSize > 0) {
        // Collect file names for this solid block
        var names = new List<string>();
        var ssIdx = 0;
        for (var f = 0; f < fi; f++)
          ssIdx += f < subStreams.NumUnpackStreams.Length ? subStreams.NumUnpackStreams[f] : 1;

        var fileIdx = 0;
        var nonEmptyIdx = 0;
        foreach (var info in fileInfos!) {
          if (info.IsEmptyStream) { fileIdx++; continue; }
          if (nonEmptyIdx >= ssIdx && nonEmptyIdx < ssIdx + fileCount)
            names.Add(info.Name);
          nonEmptyIdx++;
          fileIdx++;
          if (nonEmptyIdx >= ssIdx + fileCount) break;
        }

        var blockName = names.Count switch {
          0 => $"Solid block {fi}",
          1 => names[0],
          _ => $"Solid block {fi} ({names.Count} files)"
        };

        yield return new DefragBlockInfo(
          folderOffset,
          totalPackedSize,
          DefragBlockKind.Used,
          FileName: blockName,
          Classification: ClassifyFolder(folder));
      }

      packStreamIndex += numPack;
    }

    // Compressed metadata at end → MetadataReserved
    if (sigHeader.NextHeaderSize > 0) {
      var nextHeaderPos = SevenZipConstants.SignatureHeaderSize + sigHeader.NextHeaderOffset;
      yield return new DefragBlockInfo(
        nextHeaderPos,
        sigHeader.NextHeaderSize,
        DefragBlockKind.MetadataReserved,
        FileName: "7z Metadata");
    }
  }

  private static DefragBlockClass ClassifyFolder(SevenZipFolder folder) {
    foreach (var coder in folder.Coders) {
      var id = coder.CodecId;
      if (id.AsSpan().SequenceEqual(SevenZipConstants.CodecLzma2)) return DefragBlockClass.Hot;
      if (id.AsSpan().SequenceEqual(SevenZipConstants.CodecLzma)) return DefragBlockClass.Hot;
      if (id.AsSpan().SequenceEqual(SevenZipConstants.CodecPpmd)) return DefragBlockClass.Hot;
      if (id.AsSpan().SequenceEqual(SevenZipConstants.CodecBzip2)) return DefragBlockClass.Cold;
      if (id.AsSpan().SequenceEqual(SevenZipConstants.CodecDeflate)) return DefragBlockClass.Normal;
      if (id.AsSpan().SequenceEqual(SevenZipConstants.CodecCopy)) return DefragBlockClass.Frozen;
    }
    return DefragBlockClass.Normal;
  }

  private static void ReadExact(Stream stream, byte[] buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = stream.Read(buffer, total, buffer.Length - total);
      if (read == 0) throw new EndOfStreamException();
      total += read;
    }
  }
}
