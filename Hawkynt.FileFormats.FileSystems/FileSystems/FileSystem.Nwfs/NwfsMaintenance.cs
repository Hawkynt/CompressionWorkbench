#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Nwfs;

/// <summary>
/// Transactional rebuild and allocation-map helpers for the deliberately
/// narrow single-segment Traditional NWFS writable profile.
/// </summary>
internal static class NwfsMaintenance {
  internal sealed record Snapshot(
    string VolumeName,
    int BlockSize,
    uint PartitionStartSector,
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
      if (item.IsDirectory)
        directories.Add(item.Path);
      else
        files[item.Path] = reader.Read(item);
    }

    return new Snapshot(
      reader.VolumeName,
      reader.BlockSize,
      checked((uint)(reader.PartitionOffset / NwfsLayout.SectorSize)),
      image.LongLength,
      directories,
      files,
      reader);
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
      PartitionStartSector = snapshot.PartitionStartSector,
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
      if (data.Length == 0)
        continue;
      slack = checked(slack + NwfsLayout.AlignUp(data.LongLength, blockSize) - data.LongLength);
    }
    return slack;
  }

  internal static long EstimateTightImageLength(Snapshot snapshot, int blockSize) {
    if (!NwfsLayout.IsValidBlockSize(blockSize))
      return long.MaxValue;

    var entriesPerCluster = blockSize / NwfsLayout.DirectoryEntryBytes;
    var usedEntries = checked(1L + snapshot.Directories.Count + snapshot.Files.Count);
    var directoryClusters = checked((int)Math.Max(
      1,
      NwfsLayout.DivideRoundUp(usedEntries, entriesPerCluster)));

    var fileClusters = 0;
    foreach (var data in snapshot.Files.Values)
      fileClusters = checked(fileClusters
        + (int)NwfsLayout.DivideRoundUp(data.LongLength, blockSize));

    var plan = NwfsLayout.Plan(blockSize, directoryClusters, fileClusters);
    return NwfsLayout.TightImageLength(snapshot.PartitionStartSector, blockSize, plan.ClusterCount);
  }

  internal static int FindOptimalBlockSize(Snapshot snapshot) {
    var best = snapshot.BlockSize;
    var bestLength = long.MaxValue;
    for (var candidate = NwfsLayout.IoBlockSize; candidate <= 64 * 1024; candidate <<= 1) {
      var length = EstimateTightImageLength(snapshot, candidate);
      if (length >= bestLength)
        continue;
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
    var physicalBlocks = reader.TotalPhysicalBlocks;
    if (physicalBlocks <= 0)
      return [];

    var kinds = new DefragBlockKind[physicalBlocks];
    var owners = new string?[physicalBlocks];

    void MarkPhysical(int physicalBlock, DefragBlockKind kind, string? owner = null) {
      if ((uint)physicalBlock >= (uint)physicalBlocks)
        return;
      kinds[physicalBlock] = kind;
      owners[physicalBlock] = owner;
    }

    void MarkCluster(uint cluster, DefragBlockKind kind, string? owner = null) {
      if (cluster >= reader.TotalClusters)
        return;
      var first = checked((int)cluster * reader.BlocksPerCluster);
      for (var i = 0; i < reader.BlocksPerCluster; ++i)
        MarkPhysical(first + i, kind, owner);
    }

    for (uint cluster = 0; cluster < reader.TotalClusters; ++cluster) {
      if (!reader.TryReadFatEntry(cluster, out var index, out var next)) {
        MarkCluster(cluster, DefragBlockKind.MetadataReserved);
      } else if (index == NwfsLayout.FreeFatIndex && next == NwfsLayout.FreeFatCluster) {
        MarkCluster(cluster, DefragBlockKind.Free);
      } else {
        MarkCluster(cluster, DefragBlockKind.MetadataReserved);
      }
    }

    foreach (var physicalBlock in reader.EnumerateFatPhysicalBlocks())
      MarkPhysical(physicalBlock, DefragBlockKind.MetadataReserved);

    foreach (var cluster in reader.WalkChain(reader.RootDirectoryBlock))
      MarkCluster(cluster, DefragBlockKind.MetadataReserved);
    foreach (var cluster in reader.WalkChain(reader.SecondDirectoryBlock))
      MarkCluster(cluster, DefragBlockKind.MetadataReserved);

    foreach (var item in reader.List()) {
      if (item.IsDirectory || item.FirstBlock == NwfsLayout.EndOfChain)
        continue;
      foreach (var cluster in reader.WalkChain(item.FirstBlock))
        MarkCluster(cluster, DefragBlockKind.Used, item.Path);
    }

    var result = new List<DefragBlockInfo>();
    if (reader.VolumeOffset > 0)
      result.Add(new DefragBlockInfo(
        0,
        reader.VolumeOffset,
        DefragBlockKind.MetadataReserved,
        null,
        DefragBlockClass.Directory));

    var start = 0;
    while (start < physicalBlocks) {
      var kind = kinds[start];
      var owner = owners[start];
      var end = start + 1;
      while (end < physicalBlocks
             && kinds[end] == kind
             && string.Equals(owners[end], owner, StringComparison.Ordinal))
        ++end;

      result.Add(new DefragBlockInfo(
        reader.VolumeOffset + (long)start * NwfsLayout.IoBlockSize,
        (long)(end - start) * NwfsLayout.IoBlockSize,
        kind,
        owner,
        kind == DefragBlockKind.MetadataReserved
          ? DefragBlockClass.Directory
          : DefragBlockClass.Normal));
      start = end;
    }

    var volumeEnd = reader.VolumeOffset + (long)physicalBlocks * NwfsLayout.IoBlockSize;
    if (snapshot.ImageLength > volumeEnd)
      result.Add(new DefragBlockInfo(
        volumeEnd,
        snapshot.ImageLength - volumeEnd,
        DefragBlockKind.MetadataReserved,
        null,
        DefragBlockClass.Directory));

    return result;
  }
}
