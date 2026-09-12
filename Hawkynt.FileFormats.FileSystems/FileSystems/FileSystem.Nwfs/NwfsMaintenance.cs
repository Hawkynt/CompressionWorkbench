#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Nwfs;

/// <summary>
/// Transactional rebuild and allocation-map helpers for the plain, single-volume
/// NWFS profile supported by <see cref="NwfsReader"/> and <see cref="NwfsWriter"/>.
/// </summary>
internal static class NwfsMaintenance {
  internal sealed record Snapshot(
    string VolumeName,
    int BlockSize,
    long ImageLength,
    List<string> Directories,
    Dictionary<string, byte[]> Files,
    NwfsReader Reader);

  internal static Snapshot Read(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("NWFS maintenance requires a readable, seekable stream.", nameof(stream));
    if (stream.Length > int.MaxValue)
      throw new InvalidDataException("The managed NWFS reader currently supports images up to 2 GiB.");

    stream.Position = 0;
    var image = new byte[(int)stream.Length];
    stream.ReadExactly(image);
    var reader = NwfsReader.TryOpen(image)
      ?? throw new InvalidDataException("The image is not a supported plain NWFS volume.");
    var directories = new List<string>();
    var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in reader.List()) {
      if (item.IsDirectory) directories.Add(item.Path);
      else files[item.Path] = reader.Read(item);
    }
    return new Snapshot(reader.VolumeName, reader.BlockSize, image.LongLength, directories, files, reader);
  }

  internal static byte[] Build(
      Snapshot snapshot,
      int? blockSize = null,
      long minimumImageSize = 0,
      string? volumeName = null) {
    ArgumentNullException.ThrowIfNull(snapshot);
    var writer = new NwfsWriter {
      BlockSize = blockSize ?? snapshot.BlockSize,
      VolumeName = string.IsNullOrWhiteSpace(volumeName) ? snapshot.VolumeName : volumeName,
      MinimumImageSize = minimumImageSize,
    };
    foreach (var directory in snapshot.Directories)
      writer.AddDirectory(directory);
    foreach (var (name, data) in snapshot.Files.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
      writer.AddFile(name, data);
    return writer.Build();
  }

  internal static void Replace(Stream archive, byte[] image) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(image);
    if (!archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("NWFS modification requires a writable, seekable stream.", nameof(archive));
    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(image);
    archive.Flush();
    archive.Position = 0;
  }

  internal static long FileSlack(Snapshot snapshot, int blockSize) {
    long slack = 0;
    foreach (var data in snapshot.Files.Values) {
      if (data.Length == 0) continue;
      slack += AlignUp(data.LongLength, blockSize) - data.LongLength;
    }
    return slack;
  }

  internal static long EstimateTightImageLength(Snapshot snapshot, int blockSize) {
    if (!NwfsLayout.IsValidBlockSize(blockSize)) return long.MaxValue;
    var entriesPerBlock = blockSize / NwfsLayout.DirectoryEntryBytes;
    var usedEntries = 1L + snapshot.Directories.Count + snapshot.Files.Count;
    var directoryBlocks = Math.Max(1L, (usedEntries + entriesPerBlock - 1) / entriesPerBlock);
    long fileBlocks = 0;
    foreach (var data in snapshot.Files.Values)
      fileBlocks = checked(fileBlocks + (data.LongLength + blockSize - 1) / blockSize);

    long fatBlocks = 1;
    long totalBlocks;
    while (true) {
      totalBlocks = checked(fatBlocks + directoryBlocks * 2 + fileBlocks);
      var needed = Math.Max(1L, (totalBlocks * NwfsLayout.FatEntryBytes + blockSize - 1) / blockSize);
      if (needed == fatBlocks) break;
      fatBlocks = needed;
    }

    var dataAreaOffset = (long)32 * NwfsLayout.SectorSize
                         + NwfsLayout.HotfixOffsetInPartition
                         + (long)128 * NwfsLayout.SectorSize
                         + NwfsLayout.VolumeAreaBytes;
    return checked(dataAreaOffset + totalBlocks * blockSize);
  }

  internal static int FindOptimalBlockSize(Snapshot snapshot) {
    var best = snapshot.BlockSize;
    var bestLength = long.MaxValue;
    for (var candidate = 1024; candidate <= 256 * 1024; candidate <<= 1) {
      var length = EstimateTightImageLength(snapshot, candidate);
      if (length >= bestLength) continue;
      bestLength = length;
      best = candidate;
    }
    return best;
  }

  internal static IReadOnlyList<DefragBlockInfo> EnumerateExtents(Stream stream) {
    Snapshot snapshot;
    try {
      snapshot = Read(stream);
    } catch {
      return [];
    }

    var reader = snapshot.Reader;
    if (reader.TotalBlocks > int.MaxValue) return [];
    var totalBlocks = (int)reader.TotalBlocks;
    var kinds = new DefragBlockKind[totalBlocks];
    var known = new bool[totalBlocks];
    var owners = new string?[totalBlocks];

    void MarkBlock(uint block, DefragBlockKind kind, string? owner = null) {
      if (block < reader.FirstSegmentBlock) return;
      var relative = (long)block - reader.FirstSegmentBlock;
      if ((ulong)relative >= (ulong)totalBlocks) return;
      var index = (int)relative;
      known[index] = true;
      kinds[index] = kind;
      owners[index] = owner;
    }

    var fatBlocks = Math.Max(1, (totalBlocks * NwfsLayout.FatEntryBytes + reader.BlockSize - 1) / reader.BlockSize);
    for (var i = 0; i < fatBlocks && i < totalBlocks; ++i)
      MarkBlock(reader.FirstSegmentBlock + (uint)i, DefragBlockKind.MetadataReserved);

    foreach (var block in reader.WalkChain(reader.RootDirectoryBlock))
      MarkBlock(block, DefragBlockKind.MetadataReserved);
    foreach (var block in reader.WalkChain(reader.SecondDirectoryBlock))
      MarkBlock(block, DefragBlockKind.MetadataReserved);

    foreach (var item in reader.List()) {
      if (item.IsDirectory || item.FirstBlock == NwfsLayout.NoBlock) continue;
      foreach (var block in reader.WalkChain(item.FirstBlock))
        MarkBlock(block, DefragBlockKind.Used, item.Path);
    }

    // Any FAT entry that is not the all-ones free marker is allocated to a
    // structure the current namespace reader does not understand. Preserve it
    // as metadata rather than guessing (suballocation/salvage are examples).
    for (var i = 0; i < totalBlocks; ++i) {
      if (known[i]) continue;
      var block = reader.FirstSegmentBlock + (uint)i;
      if (!reader.TryReadFatEntry(block, out var index, out var next)) {
        known[i] = true;
        kinds[i] = DefragBlockKind.MetadataReserved;
      } else if (index == NwfsLayout.NoBlock && next == NwfsLayout.NoBlock) {
        known[i] = true;
        kinds[i] = DefragBlockKind.Free;
      } else {
        known[i] = true;
        kinds[i] = DefragBlockKind.MetadataReserved;
      }
    }

    var result = new List<DefragBlockInfo> {
      new(0, reader.DataAreaOffset, DefragBlockKind.MetadataReserved),
    };
    var start = 0;
    while (start < totalBlocks) {
      var kind = kinds[start];
      var owner = owners[start];
      var end = start + 1;
      while (end < totalBlocks && kinds[end] == kind
             && string.Equals(owners[end], owner, StringComparison.Ordinal))
        ++end;
      result.Add(new DefragBlockInfo(
        reader.DataAreaOffset + (long)start * reader.BlockSize,
        (long)(end - start) * reader.BlockSize,
        kind,
        owner,
        kind == DefragBlockKind.MetadataReserved ? DefragBlockClass.Directory : DefragBlockClass.Normal));
      start = end;
    }

    var volumeEnd = reader.DataAreaOffset + (long)totalBlocks * reader.BlockSize;
    if (snapshot.ImageLength > volumeEnd)
      result.Add(new DefragBlockInfo(volumeEnd, snapshot.ImageLength - volumeEnd, DefragBlockKind.MetadataReserved));
    return result;
  }

  private static long AlignUp(long value, int alignment)
    => checked((value + alignment - 1) / alignment * alignment);
}
