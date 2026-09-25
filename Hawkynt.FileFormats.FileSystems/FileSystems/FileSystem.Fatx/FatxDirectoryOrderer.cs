using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Fatx;

/// <summary>
/// Reorders FATX directory records in place without changing allocation chains
/// or file payloads. Whole 64-byte records move as opaque units, preserving all
/// attributes, timestamps, first-cluster values and sizes byte-for-byte.
/// </summary>
internal static class FatxDirectoryOrderer {
  private const byte EndMarker = 0xFF;
  private const byte EmptyMarker = 0x00;
  private const byte DeletedMarker = 0xE5;
  private const int MaxNameLength = 42;
  private const byte DirectoryAttribute = 0x10;

  public static void Sort(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("FATX directory sorting requires a readable, writable, seekable stream.", nameof(image));

    image.Position = 0;
    using var reader = new FatxReader(image);
    var visited = new HashSet<uint>();
    SortDirectory(image, reader, reader.RootDirCluster, visited);
    image.Flush();
  }

  private static void SortDirectory(
      Stream image,
      FatxReader reader,
      uint startCluster,
      HashSet<uint> visitedDirectories) {
    if (startCluster < 1 || reader.IsEoc(startCluster) || !visitedDirectories.Add(startCluster))
      return;

    var slotOffsets = new List<long>();
    var live = new List<Record>();
    var deleted = new List<byte[]>();
    var childClusters = new List<uint>();

    var cluster = startCluster;
    var seenChain = new HashSet<uint>();
    var reachedEnd = false;

    while (cluster >= 1 && !reader.IsEoc(cluster) && seenChain.Add(cluster)) {
      var clusterOffset = reader.ClusterOffset(cluster);
      if (clusterOffset < 0 || clusterOffset + reader.ClusterSize > image.Length)
        throw new InvalidDataException($"FATX directory cluster {cluster} lies outside the image.");

      for (var offset = 0; offset < reader.ClusterSize; offset += FatxReader.DirRecordSize) {
        var recordOffset = clusterOffset + offset;
        var bytes = ReadRecord(image, recordOffset);
        var nameLength = bytes[0];

        if (nameLength is EndMarker or EmptyMarker) {
          reachedEnd = true;
          break;
        }

        slotOffsets.Add(recordOffset);

        if (nameLength == DeletedMarker) {
          deleted.Add(bytes);
          continue;
        }

        if (nameLength > MaxNameLength)
          throw new InvalidDataException(
            $"FATX directory contains malformed name length {nameLength} at 0x{recordOffset:X}.");

        var name = Encoding.ASCII.GetString(bytes, 2, nameLength);
        live.Add(new Record(name, bytes));

        if ((bytes[1] & DirectoryAttribute) != 0) {
          var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x2C, 4));
          if (firstCluster >= 1 && !reader.IsEoc(firstCluster))
            childClusters.Add(firstCluster);
        }
      }

      if (reachedEnd)
        break;

      cluster = reader.GetNextCluster(cluster);
    }

    live.Sort(static (left, right) => {
      var result = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
      return result != 0 ? result : StringComparer.Ordinal.Compare(left.Name, right.Name);
    });

    var recordsNeeded = live.Count + deleted.Count;
    if (recordsNeeded > slotOffsets.Count)
      throw new InvalidDataException("FATX directory scan lost record slots while sorting.");

    var index = 0;
    foreach (var record in live)
      WriteRecord(image, slotOffsets[index++], record.Bytes);
    foreach (var record in deleted)
      WriteRecord(image, slotOffsets[index++], record);

    // If live records moved over deleted slots, every old live slot after the
    // compacted prefix must become an end marker. Filling whole records with
    // 0xFF matches the writer's canonical unused-slot representation.
    for (; index < slotOffsets.Count; ++index)
      WriteUnusedRecord(image, slotOffsets[index]);

    // Preserve a terminator even for a directory that originally filled one
    // cluster and continued into another. The first physical slot immediately
    // after the compacted records is the canonical end when one is available.
    if (TryGetNextSlotOffset(image, reader, startCluster, recordsNeeded, out var terminatorOffset))
      WriteUnusedRecord(image, terminatorOffset);

    foreach (var child in childClusters.Distinct())
      SortDirectory(image, reader, child, visitedDirectories);
  }

  private static byte[] ReadRecord(Stream image, long offset) {
    var buffer = new byte[FatxReader.DirRecordSize];
    image.Position = offset;
    image.ReadExactly(buffer);
    return buffer;
  }

  private static void WriteRecord(Stream image, long offset, ReadOnlySpan<byte> record) {
    image.Position = offset;
    image.Write(record);
  }

  private static void WriteUnusedRecord(Stream image, long offset) {
    Span<byte> unused = stackalloc byte[FatxReader.DirRecordSize];
    unused.Fill(EndMarker);
    WriteRecord(image, offset, unused);
  }

  private static bool TryGetNextSlotOffset(
      Stream image,
      FatxReader reader,
      uint startCluster,
      int occupiedSlots,
      out long offset) {
    var remaining = occupiedSlots;
    var cluster = startCluster;
    var seen = new HashSet<uint>();

    while (cluster >= 1 && !reader.IsEoc(cluster) && seen.Add(cluster)) {
      var slots = reader.ClusterSize / FatxReader.DirRecordSize;
      if (remaining < slots) {
        offset = reader.ClusterOffset(cluster) + (long)remaining * FatxReader.DirRecordSize;
        return offset >= 0 && offset + FatxReader.DirRecordSize <= image.Length;
      }

      remaining -= slots;
      cluster = reader.GetNextCluster(cluster);
    }

    offset = -1;
    return false;
  }

  private sealed record Record(string Name, byte[] Bytes);
}
