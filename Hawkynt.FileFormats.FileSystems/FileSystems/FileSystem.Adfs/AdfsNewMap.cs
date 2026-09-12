#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Adfs;

/// <summary>
/// Reads and rewrites the zone bitmap of a single-zone new-map ADFS disc.
/// </summary>
/// <remarks>
/// <para>A new-map file is a fragment identifier, and where that identifier's
/// bits sit in the zone bitmap is where the file's sectors are. Nothing else
/// records the position — the directory entry names the fragment, not the
/// sector — so moving a file means writing the bitmap again, and the directory
/// is left alone.</para>
///
/// <para>The whole disc belongs to some fragment: the driver walks the bitmap
/// fragment by fragment, and a gap would put that walk out of step. Free space
/// is therefore fragments of identifier zero, threaded onto a chain the zone's
/// header points at.</para>
/// </remarks>
internal static class AdfsNewMap {

  /// <summary>Bits the header and disc record occupy before the bitmap starts.</summary>
  private const int MapStartBit = 32 + 60 * 8;

  /// <summary>Fragment identifiers the format reserves for its own structures.</summary>
  internal const uint MapFragment = 3;

  internal const uint RootFragment = 2;

  /// <summary>What a disc says about itself, and where its fragments are.</summary>
  internal sealed class Layout {
    public int SectorSize { get; init; }
    public int IdLength { get; init; }
    public int MapEndBit { get; init; }
    public int TotalSectors { get; init; }
    public uint RootFragmentId { get; init; }
    public int RootSize { get; init; }

    /// <summary>Fragments in bitmap order: identifier, first sector, sector count.</summary>
    public List<(uint Id, int FirstSector, int Sectors)> Fragments { get; } = [];
  }

  /// <summary>Reads the zone, or returns null when this is not a single-zone new map.</summary>
  public static Layout? TryRead(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek || image.Length < 1024) return null;

    var zone = new byte[1024];
    image.Position = 0;
    image.ReadExactly(zone, 0, 1024);

    var record = zone.AsSpan(4, 60);
    var log2SectorSize = record[0];
    if (log2SectorSize is < 8 or > 10) return null;

    var idLength = record[4];
    var log2BytesPerMapBit = record[5];
    var zones = record[9] | (record[42] << 8);
    if (zones != 1) return null;
    if (idLength < log2SectorSize + 3 || idLength > 19) return null;

    var rootIndirect = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
    var discSize = BinaryPrimitives.ReadUInt32LittleEndian(record[16..])
                 | ((long)BinaryPrimitives.ReadUInt32LittleEndian(record[36..]) << 32);
    if (rootIndirect == 0 || discSize <= 0) return null;

    var sectorSize = 1 << log2SectorSize;
    if (sectorSize != 1024) return null;                       // a wider zone is another format
    if (log2BytesPerMapBit != log2SectorSize) return null;     // one bit, one sector

    var mapEndBit = (int)(32 + (discSize >> log2BytesPerMapBit) + 60 * 8);
    if (mapEndBit > 8 * sectorSize) return null;

    var layout = new Layout {
      SectorSize = sectorSize,
      IdLength = idLength,
      MapEndBit = mapEndBit,
      TotalSectors = (int)(discSize >> log2BytesPerMapBit),
      RootFragmentId = rootIndirect >> 8,
      RootSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[48..]),
    };

    var idMask = (uint)((1 << idLength) - 1);
    var bit = MapStartBit;
    while (bit < mapEndBit) {
      var id = ReadBits(zone, bit, idMask);
      var end = FindNextSetBit(zone, mapEndBit, bit + idLength);
      if (end >= mapEndBit) break;

      layout.Fragments.Add((id, bit - MapStartBit, end + 1 - bit));
      bit = end + 1;
    }

    return layout.Fragments.Count == 0 ? null : layout;
  }

  /// <summary>
  /// Writes the bitmap from a set of fragments, covering the disc from end to
  /// end and chaining whatever is left over as free.
  /// </summary>
  /// <remarks>
  /// A fragment cannot be shorter than its identifier plus its terminator bit,
  /// so a gap too small to describe is given to the fragment before it. That
  /// fragment then reaches past the bytes it holds, which the format allows —
  /// what it does not allow is a stretch of disc belonging to nothing.
  /// </remarks>
  public static void Write(Stream image, Layout layout,
      IEnumerable<(uint Id, int FirstSector, int Sectors)> fragments) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(layout);
    ArgumentNullException.ThrowIfNull(fragments);

    var minimum = layout.IdLength + 1;
    var placed = fragments.Where(f => f.Sectors > 0).OrderBy(f => f.FirstSector).ToList();
    if (placed.Count == 0) return;

    // Stretch each fragment to where the next one starts when the gap between
    // them is too small to be a fragment of its own.
    var emitted = new List<(uint Id, int FirstSector, int Sectors)>();
    for (var i = 0; i < placed.Count; ++i) {
      var (id, first, sectors) = placed[i];
      var until = i + 1 < placed.Count ? placed[i + 1].FirstSector : layout.TotalSectors;
      if (first + sectors > until)
        throw new InvalidOperationException(
          $"ADFS new map: fragment {id} runs from sector {first} for {sectors} sectors, into the " +
          $"one that starts at {until}.");

      var gap = until - (first + sectors);
      if (gap > 0 && gap < minimum) sectors += gap;
      emitted.Add((id, first, Math.Max(sectors, minimum)));
    }

    // Whatever is still unclaimed becomes free fragments, in disc order.
    var free = new List<(int FirstSector, int Sectors)>();
    var cursor = 0;
    foreach (var (_, first, sectors) in emitted) {
      if (first - cursor >= minimum) free.Add((cursor, first - cursor));
      cursor = Math.Max(cursor, first + sectors);
    }

    if (layout.TotalSectors - cursor >= minimum)
      free.Add((cursor, layout.TotalSectors - cursor));

    var zone = new byte[layout.SectorSize];
    image.Position = 0;
    image.ReadExactly(zone, 0, layout.SectorSize);

    // Clear the bitmap, keeping the header and the disc record.
    for (var bit = MapStartBit; bit < 8 * layout.SectorSize; ++bit)
      zone[bit >> 3] &= (byte)~(1 << (bit & 7));

    foreach (var (id, first, sectors) in emitted)
      WriteFragment(zone, layout, MapStartBit + first, sectors, id);

    // Free fragments carry the distance to the next one where their identifier
    // would be, and the zone's header points at the first.
    for (var i = 0; i < free.Count; ++i) {
      var (first, sectors) = free[i];
      var next = i + 1 < free.Count ? (uint)(free[i + 1].FirstSector - first) : 0u;
      WriteFragment(zone, layout, MapStartBit + first, sectors, next);
    }

    var link = free.Count > 0 ? MapStartBit + free[0].FirstSector - 8 : 0;
    BinaryPrimitives.WriteUInt16LittleEndian(zone.AsSpan(1), (ushort)link);

    zone[3] = 0xFF;
    zone[0] = ZoneCheck(zone);

    image.Position = 0;
    image.Write(zone, 0, layout.SectorSize);
    image.Flush();
  }

  /// <summary>Writes one fragment: its identifier, then zeros, then its last bit set.</summary>
  private static void WriteFragment(Span<byte> zone, Layout layout, int startBit, int lengthBits, uint id) {
    if (lengthBits < layout.IdLength + 1) return;
    if (startBit + lengthBits > 8 * layout.SectorSize) return;

    for (var i = 0; i < layout.IdLength; ++i)
      if ((id & (1u << i)) != 0) SetBit(zone, startBit + i);

    SetBit(zone, startBit + lengthBits - 1);
  }

  private static void SetBit(Span<byte> data, int bit) => data[bit >> 3] |= (byte)(1 << (bit & 7));

  private static uint ReadBits(ReadOnlySpan<byte> map, int startBit, uint mask) {
    var at = startBit >> 3;
    if (at + 4 > map.Length) return 0;
    return (BinaryPrimitives.ReadUInt32LittleEndian(map[at..]) >> (startBit & 7)) & mask;
  }

  private static int FindNextSetBit(ReadOnlySpan<byte> map, int endBit, int startBit) {
    for (var bit = startBit; bit < endBit; ++bit)
      if ((map[bit >> 3] & (1 << (bit & 7))) != 0) return bit;
    return endBit;
  }

  /// <summary>
  /// The zone check byte, as <c>adfs_calczonecheck</c> computes it: four
  /// interleaved carrying sums over the sector, folded together.
  /// </summary>
  private static byte ZoneCheck(ReadOnlySpan<byte> map) {
    uint v0 = 0, v1 = 0, v2 = 0, v3 = 0;
    for (var i = map.Length - 4; i != 0; i -= 4) {
      v0 += (uint)map[i] + (v3 >> 8);
      v3 &= 0xff;
      v1 += (uint)map[i + 1] + (v0 >> 8);
      v0 &= 0xff;
      v2 += (uint)map[i + 2] + (v1 >> 8);
      v1 &= 0xff;
      v3 += (uint)map[i + 3] + (v2 >> 8);
      v2 &= 0xff;
    }

    v0 += v3 >> 8;
    v1 += (uint)map[1] + (v0 >> 8);
    v2 += (uint)map[2] + (v1 >> 8);
    v3 += (uint)map[3] + (v2 >> 8);
    return (byte)(v0 ^ v1 ^ v2 ^ v3);
  }
}
