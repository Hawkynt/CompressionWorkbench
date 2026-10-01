#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.ExFat;

/// <summary>
/// Sorts the File entry sets of every exFAT directory in place. A File entry and the
/// secondaries its SecondaryCount names move as one unit, byte for byte, so the stream
/// extension, the name, the timestamps, the attributes and the entry-set checksum all
/// stay valid; no cluster is allocated, freed or moved.
/// </summary>
/// <remarks>
/// <para>Layout rules, from Microsoft's exFAT File System Specification (§6 "Directory
/// Structure", §7.4–7.7): a directory is a run of 32-byte entries ending at the first
/// entry of type 0x00; bit 7 of the type marks an entry in use; a File entry (0x85) is
/// followed by SecondaryCount secondaries, the first a Stream Extension (0xC0) and then
/// the File Name entries (0xC1), and its SetChecksum covers the whole set. A directory
/// with the NoFatChain flag is contiguous, its length the Stream Extension's DataLength.
/// Nothing in the specification ties the order of entry sets to anything.</para>
///
/// <para>What stays put: the root directory's critical and benign primaries (allocation
/// bitmap, up-case table, volume label, GUID, …) stay first in their own order, entries
/// no longer in use are kept after the live sets (wiping them is a different verb), and the
/// end-of-directory marker stays in the same slot.</para>
///
/// <para>The whole tree is planned before anything is written. An entry set whose checksum
/// does not match, or a secondary found outside any set, is refused rather than carried.</para>
/// </remarks>
internal static class ExFatDirectoryOrderer {
  private const int EntrySize = 32;
  private const byte FileEntry = 0x85;
  private const byte StreamExtension = 0xC0;
  private const byte FileName = 0xC1;
  private const uint EndOfChain = 0xFFFFFFFFu;

  /// <summary>Sorts every directory of the volume, writing only through <paramref name="journal"/>.</summary>
  public static void Sort(Stream image, InPlacePatchJournal journal) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(journal);
    var geometry = Geometry.Read(image);
    var plan = new List<(long[] Offsets, byte[] Bytes)>();
    var pending = new Queue<(uint Cluster, bool Contiguous, long Length, string Path)>();
    pending.Enqueue((geometry.RootCluster, false, -1, ""));
    var visited = new HashSet<uint>();

    while (pending.Count > 0) {
      var (cluster, contiguous, length, path) = pending.Dequeue();
      if (!visited.Add(cluster))
        throw new InvalidDataException($"exFAT directory '{path}' shares cluster {cluster} with another directory.");
      var offsets = geometry.Clusters(image, cluster, contiguous, length, path).Select(geometry.ClusterOffset).ToArray();
      var data = new byte[checked(offsets.Length * geometry.ClusterSize)];
      for (var i = 0; i < offsets.Length; ++i) {
        image.Position = offsets[i];
        image.ReadExactly(data, i * geometry.ClusterSize, geometry.ClusterSize);
      }
      var children = Reorder(data, path, out var sorted);
      if (!sorted.AsSpan().SequenceEqual(data)) plan.Add((offsets, sorted));
      foreach (var child in children) pending.Enqueue(child);
    }

    foreach (var (offsets, bytes) in plan)
      for (var i = 0; i < offsets.Length; ++i)
        journal.Write(image, offsets[i], bytes.AsSpan(i * geometry.ClusterSize, geometry.ClusterSize));
    image.Flush();
  }

  private static List<(uint Cluster, bool Contiguous, long Length, string Path)> Reorder(byte[] directory, string path, out byte[] sorted) {
    var pinned = new List<byte[]>();
    var sets = new List<(string Name, int Ordinal, byte[] Raw)>();
    var unused = new List<byte[]>();
    var children = new List<(uint Cluster, bool Contiguous, long Length, string Path)>();
    var end = directory.Length;

    for (var offset = 0; offset + EntrySize <= directory.Length;) {
      var type = directory[offset];
      if (type == 0x00) { end = offset; break; }

      if ((type & 0x80) == 0) {
        unused.Add(directory.AsSpan(offset, EntrySize).ToArray());
        offset += EntrySize;
        continue;
      }
      if ((type & 0x40) != 0)
        throw Damaged(path, $"an in-use secondary entry (0x{type:X2}) at byte {offset} belongs to no entry set");
      if (type != FileEntry) {
        pinned.Add(directory.AsSpan(offset, EntrySize).ToArray());
        offset += EntrySize;
        continue;
      }

      var secondaries = directory[offset + 1];
      var length = (secondaries + 1) * EntrySize;
      if (secondaries < 2 || offset + length > directory.Length)
        throw Damaged(path, $"the File entry at byte {offset} declares {secondaries} secondaries");
      var raw = directory.AsSpan(offset, length).ToArray();
      if (BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(2)) != SetChecksum(raw))
        throw Damaged(path, $"the entry set at byte {offset} fails its checksum");
      for (var i = 1; i <= secondaries; ++i)
        if ((raw[i * EntrySize] & 0xC0) != 0xC0)
          throw Damaged(path, $"the entry set at byte {offset} has a primary or unused entry among its secondaries");
      if (raw[EntrySize] != StreamExtension)
        throw Damaged(path, $"the entry set at byte {offset} does not begin with a Stream Extension");

      var name = DecodeName(raw, path);
      sets.Add((name, sets.Count, raw));

      if ((BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(4)) & 0x10) != 0) {
        var flags = raw[EntrySize + 1];
        var first = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(EntrySize + 20));
        var size = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(EntrySize + 24));
        if (size > long.MaxValue) throw Damaged(path, $"directory '{name}' declares an impossible length");
        if (first >= 2)
          children.Add((first, (flags & 0x02) != 0, (long)size, path.Length == 0 ? name : path + "/" + name));
      }
      offset += length;
    }

    sorted = (byte[])directory.Clone();
    var cursor = 0;
    foreach (var raw in pinned
               .Concat(sets.OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)
                 .ThenBy(static s => s.Name, StringComparer.Ordinal)
                 .ThenBy(static s => s.Ordinal)
                 .Select(static s => s.Raw))
               .Concat(unused)) {
      raw.CopyTo(sorted, cursor);
      cursor += raw.Length;
    }
    if (cursor != end)
      throw new InvalidOperationException($"exFAT directory '{path}': the sorted entries fill {cursor} bytes, not {end}.");
    return children;
  }

  private static string DecodeName(byte[] set, string path) {
    var nameLength = set[EntrySize + 3];
    var name = new StringBuilder(nameLength);
    for (var entry = 2; name.Length < nameLength; ++entry) {
      if (entry * EntrySize >= set.Length || set[entry * EntrySize] != FileName)
        throw Damaged(path, "an entry set holds fewer File Name entries than its name length needs");
      for (var i = 0; i < 15 && name.Length < nameLength; ++i)
        name.Append((char)BinaryPrimitives.ReadUInt16LittleEndian(set.AsSpan(entry * EntrySize + 2 + 2 * i)));
    }
    return name.ToString();
  }

  /// <summary>EntrySetChecksum (§6.3.3): rotate right, add, over every byte but the checksum field.</summary>
  private static ushort SetChecksum(byte[] set) {
    ushort checksum = 0;
    for (var i = 0; i < set.Length; ++i) {
      if (i is 2 or 3) continue;
      checksum = (ushort)(((checksum & 1) != 0 ? 0x8000 : 0) + (checksum >> 1) + set[i]);
    }
    return checksum;
  }

  private static NotSupportedException Damaged(string path, string what)
    => new($"exFAT directory '{(path.Length == 0 ? "/" : path)}': {what}; sorting it would have to guess, so it is refused.");

  /// <summary>The boot-sector fields (§3.1) a directory walk needs.</summary>
  private sealed record Geometry(int ClusterSize, long FatOffset, long FatLength, long HeapOffset, uint ClusterCount, uint RootCluster) {
    public long ClusterOffset(uint cluster) => checked(this.HeapOffset + (long)(cluster - 2) * this.ClusterSize);

    public List<uint> Clusters(Stream image, uint first, bool contiguous, long length, string path) {
      var result = new List<uint>();
      if (contiguous) {
        var count = (length + this.ClusterSize - 1) / this.ClusterSize;
        if (count <= 0 || first + (ulong)count > this.ClusterCount + 2UL)
          throw new InvalidDataException($"exFAT directory '{path}' lies outside the cluster heap.");
        for (var i = 0L; i < count; ++i) result.Add(first + (uint)i);
        return result;
      }
      var seen = new HashSet<uint>();
      Span<byte> entry = stackalloc byte[4];
      for (var cluster = first; ;) {
        if (cluster < 2 || cluster > this.ClusterCount + 1 || !seen.Add(cluster))
          throw new InvalidDataException($"exFAT directory '{path}' has a broken cluster chain at {cluster}.");
        result.Add(cluster);
        var at = checked(this.FatOffset + (long)cluster * 4);
        if (at + 4 > this.FatOffset + this.FatLength)
          throw new InvalidDataException($"exFAT FAT entry {cluster} lies outside the FAT.");
        image.Position = at;
        image.ReadExactly(entry);
        var next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
        if (next == EndOfChain) return result;
        cluster = next;
      }
    }

    public static Geometry Read(Stream image) {
      Span<byte> boot = stackalloc byte[512];
      image.Position = 0;
      image.ReadExactly(boot);
      if (!boot.Slice(3, 8).SequenceEqual("EXFAT   "u8))
        throw new InvalidDataException("Not an exFAT boot sector.");
      int sectorShift = boot[108], clusterShift = boot[109];
      if (sectorShift is < 9 or > 12 || sectorShift + clusterShift > 25)
        throw new InvalidDataException("exFAT sector or cluster size is out of range.");
      var bytesPerSector = 1L << sectorShift;
      return new Geometry(
        1 << (sectorShift + clusterShift),
        BinaryPrimitives.ReadUInt32LittleEndian(boot[80..]) * bytesPerSector,
        BinaryPrimitives.ReadUInt32LittleEndian(boot[84..]) * bytesPerSector,
        BinaryPrimitives.ReadUInt32LittleEndian(boot[88..]) * bytesPerSector,
        BinaryPrimitives.ReadUInt32LittleEndian(boot[92..]),
        BinaryPrimitives.ReadUInt32LittleEndian(boot[96..]));
    }
  }
}
