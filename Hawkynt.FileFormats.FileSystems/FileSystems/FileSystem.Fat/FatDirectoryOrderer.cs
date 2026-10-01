#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Fat;

/// <summary>
/// Sorts the entries of every FAT12/16/32 directory in place. A VFAT long name and the
/// short entry it belongs to move as one record, so names, attributes, timestamps and
/// first clusters travel byte for byte; no cluster is allocated, freed or moved.
/// </summary>
/// <remarks>
/// <para>Layout rules, from Microsoft's FAT specification (FATGEN 1.03, "FAT Directory
/// Structure" and "FAT Long Directory Entries"): a directory is a run of 32-byte slots
/// ending at the first slot whose name byte is 0x00; 0xE5 marks a free slot; a long name is
/// stored in slots with attribute 0x0F immediately before its short entry, highest sequence
/// number first (0x40 on the last), each carrying the checksum of the 11-byte short name.
/// The order of entries is not otherwise constrained, apart from "." and ".." being the
/// first two slots of a subdirectory.</para>
///
/// <para>What stays put: the volume label entry and the dot entries stay first, deleted
/// slots are kept (after the live records, never wiped — that is a different verb), and the
/// end-of-directory marker stays in the same slot, so nothing past it changes either.</para>
///
/// <para>The whole tree is planned before anything is written. A long-name chain whose
/// sequence or checksum is broken is refused rather than guessed at: a driver would show
/// those slots as garbage or drop them, and the sort would have to decide which.</para>
/// </remarks>
internal static class FatDirectoryOrderer {
  private const int SlotSize = 32;
  private const byte AttrLongName = 0x0F;
  private const byte AttrVolumeId = 0x08;
  private const byte AttrDirectory = 0x10;
  private const byte FreeMarker = 0xE5;

  /// <summary>Sorts every directory of the volume, writing only through <paramref name="journal"/>.</summary>
  public static void Sort(Stream image, InPlacePatchJournal journal) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(journal);
    var geometry = FatDriverGeometry.Parse(image);
    var plan = Plan(image, geometry);
    foreach (var (offsets, bytes, segment) in plan)
      for (var i = 0; i < offsets.Length; ++i)
        journal.Write(image, offsets[i], bytes.AsSpan(i * segment, segment));
    image.Flush();
  }

  private static List<(long[] Offsets, byte[] Bytes, int Segment)> Plan(Stream image, FatDriverGeometry geometry) {
    var plan = new List<(long[] Offsets, byte[] Bytes, int Segment)>();
    var pending = new Queue<(int Cluster, string Path)>();
    var visited = new HashSet<int>();

    if (geometry.FatType == 32) {
      pending.Enqueue((geometry.RootCluster, ""));
    } else {
      var rootOffset = ((long)geometry.ReservedSectors + (long)geometry.FatCount * geometry.FatSize) * geometry.BytesPerSector;
      var rootLength = checked((int)(geometry.FirstDataSector * geometry.BytesPerSector - rootOffset));
      var root = ReadAt(image, rootOffset, rootLength);
      var children = Reorder(root, "", fat32: false, out var sorted);
      if (!sorted.AsSpan().SequenceEqual(root)) plan.Add(([rootOffset], sorted, rootLength));
      foreach (var child in children) pending.Enqueue(child);
    }

    while (pending.Count > 0) {
      var (cluster, path) = pending.Dequeue();
      if (!visited.Add(cluster))
        throw new InvalidDataException($"FAT directory '{path}' shares cluster {cluster} with another directory.");
      var chain = geometry.ReadChain(image, cluster, path.Length == 0 ? "/" : path);
      var clusterSize = geometry.ClusterSize;
      var offsets = chain.Select(geometry.ClusterOffset).ToArray();
      var data = new byte[checked(offsets.Length * clusterSize)];
      for (var i = 0; i < offsets.Length; ++i) {
        image.Position = offsets[i];
        image.ReadExactly(data, i * clusterSize, clusterSize);
      }
      var children = Reorder(data, path, geometry.FatType == 32, out var sorted);
      if (!sorted.AsSpan().SequenceEqual(data)) plan.Add((offsets, sorted, clusterSize));
      foreach (var child in children) pending.Enqueue(child);
    }
    return plan;
  }

  /// <summary>
  /// Returns the directory's subdirectories and, in <paramref name="sorted"/>, its slots in
  /// the new order: pinned records, live records by name, deleted slots, then everything
  /// from the end marker on exactly as it was.
  /// </summary>
  private static List<(int Cluster, string Path)> Reorder(byte[] directory, string path, bool fat32, out byte[] sorted) {
    var pinned = new List<byte[]>();
    var live = new List<(string Name, int Ordinal, byte[] Raw)>();
    var deleted = new List<byte[]>();
    var children = new List<(int Cluster, string Path)>();
    var longName = new List<byte[]>();
    var end = directory.Length;

    for (var offset = 0; offset + SlotSize <= directory.Length; offset += SlotSize) {
      var slot = directory.AsSpan(offset, SlotSize);
      if (slot[0] == 0x00) { end = offset; break; }

      if (slot[0] == FreeMarker) {
        if (longName.Count > 0) throw Damaged(path, "a long name is followed by a free slot instead of its short entry");
        deleted.Add(slot.ToArray());
        continue;
      }

      var attributes = slot[11];
      if ((attributes & 0x3F) == AttrLongName) {
        longName.Add(slot.ToArray());
        continue;
      }

      var shortEntry = slot.ToArray();
      string name;
      if (longName.Count > 0) {
        name = DecodeLongName(longName, shortEntry, path);
      } else {
        name = DecodeShortName(shortEntry);
      }
      var raw = new byte[(longName.Count + 1) * SlotSize];
      for (var i = 0; i < longName.Count; ++i) longName[i].CopyTo(raw, i * SlotSize);
      shortEntry.CopyTo(raw, longName.Count * SlotSize);
      longName.Clear();

      var isDot = shortEntry.AsSpan(0, 11).SequenceEqual(".          "u8) || shortEntry.AsSpan(0, 11).SequenceEqual("..         "u8);
      if (isDot || (attributes & AttrVolumeId) != 0) {
        pinned.Add(raw);
        continue;
      }

      live.Add((name, live.Count, raw));
      if ((attributes & AttrDirectory) != 0) {
        // The high word (offset 20) is part of the cluster number only on FAT32; FAT12/16
        // leave it to other uses (OS/2 kept an extended-attribute handle there).
        var cluster = BinaryPrimitives.ReadUInt16LittleEndian(shortEntry.AsSpan(26))
                      | (fat32 ? BinaryPrimitives.ReadUInt16LittleEndian(shortEntry.AsSpan(20)) << 16 : 0);
        if (cluster >= 2) children.Add((cluster, path.Length == 0 ? name : path + "/" + name));
      }
    }
    if (longName.Count > 0) throw Damaged(path, "the directory ends inside a long name");

    sorted = (byte[])directory.Clone();
    var cursor = 0;
    foreach (var raw in pinned
               .Concat(live.OrderBy(static r => r.Name, StringComparer.OrdinalIgnoreCase)
                 .ThenBy(static r => r.Name, StringComparer.Ordinal)
                 .ThenBy(static r => r.Ordinal)
                 .Select(static r => r.Raw))
               .Concat(deleted)) {
      raw.CopyTo(sorted, cursor);
      cursor += raw.Length;
    }
    if (cursor != end)
      throw new InvalidOperationException($"FAT directory '{path}': the sorted records fill {cursor} bytes, not {end}.");
    return children;
  }

  /// <summary>Decodes a long name, refusing a chain whose order or checksum is not the one the specification prescribes.</summary>
  private static string DecodeLongName(List<byte[]> slots, byte[] shortEntry, string path) {
    var checksum = ShortNameChecksum(shortEntry);
    if ((slots[0][0] & 0x40) == 0) throw Damaged(path, "a long name does not start with its last-entry flag");
    var count = slots[0][0] & 0x1F;
    if (count != slots.Count) throw Damaged(path, $"a long name claims {count} slot(s) but has {slots.Count}");
    var name = new StringBuilder(slots.Count * 13);
    for (var i = slots.Count - 1; i >= 0; --i) {
      var slot = slots[i];
      if ((slot[0] & 0x1F) != slots.Count - i) throw Damaged(path, "a long name's slots are out of sequence");
      if (slot[13] != checksum) throw Damaged(path, "a long name does not belong to the short entry after it");
      if (!Append(slot, 1, 5, name) || !Append(slot, 14, 6, name) || !Append(slot, 28, 2, name)) break;
    }
    return name.ToString();

    static bool Append(byte[] slot, int offset, int count, StringBuilder into) {
      for (var i = 0; i < count; ++i) {
        var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(slot.AsSpan(offset + 2 * i));
        if (c == '\0') return false;
        into.Append(c);
      }
      return true;
    }
  }

  /// <summary>The rotate-right-and-add checksum over the 11 short-name bytes (FATGEN 1.03).</summary>
  private static byte ShortNameChecksum(byte[] shortEntry) {
    byte sum = 0;
    for (var i = 0; i < 11; ++i)
      sum = (byte)((((sum & 1) << 7) | (sum >> 1)) + shortEntry[i]);
    return sum;
  }

  private static string DecodeShortName(byte[] entry) {
    var name = Encoding.Latin1.GetString(entry, 0, 8).TrimEnd();
    var extension = Encoding.Latin1.GetString(entry, 8, 3).TrimEnd();
    // 0x05 in the first byte stands for a name that really starts with 0xE5.
    if (name.Length > 0 && name[0] == '\u0005') name = 'å' + name[1..];
    // Windows NT's lower-case bits (byte 12): 0x08 base name, 0x10 extension.
    if ((entry[12] & 0x08) != 0) name = name.ToLowerInvariant();
    if ((entry[12] & 0x10) != 0) extension = extension.ToLowerInvariant();
    return extension.Length == 0 ? name : name + "." + extension;
  }

  private static byte[] ReadAt(Stream image, long offset, int length) {
    var data = new byte[length];
    image.Position = offset;
    image.ReadExactly(data);
    return data;
  }

  private static NotSupportedException Damaged(string path, string what)
    => new($"FAT directory '{(path.Length == 0 ? "/" : path)}': {what}; sorting it would have to guess, so it is refused.");
}
