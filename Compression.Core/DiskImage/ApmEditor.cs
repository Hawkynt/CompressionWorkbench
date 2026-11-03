using System.Buffers.Binary;
using System.Text;

namespace Compression.Core.DiskImage;

/// <summary>
/// Creates Apple Partition Maps and adds or deletes their entries in place.
/// </summary>
/// <remarks>
/// <para>Layout (Apple, "Inside Macintosh: Devices", chapter 3; big-endian): block 0 is the Driver
/// Descriptor Record (<c>ER</c>, block size, block count); the map starts at block 1, one entry per
/// block (<c>PM</c>, entry count, first block, block count, name, type, data area, status). The
/// map describes itself as an <c>Apple_partition_map</c> partition named <c>Apple</c> and the
/// space no partition uses as <c>Apple_Free</c> entries named <c>Extra</c>, so the entries cover
/// the whole disk. The map partition's size bounds the number of entries (63 by default, the
/// usual 32 KiB map with the record in front of it).</para>
/// <para>Entries are kept in ascending order of their first block, free runs between them,
/// adjacent free runs merged. Status words follow parted's: 0x7F for HFS, 0x33 for other data
/// partitions, 0 for the map and free space.</para>
/// </remarks>
public sealed class ApmEditor {
  private const ushort DdrSignature = 0x4552, EntrySignature = 0x504D;
  /// <summary>Entries the default map has room for (including the map's own).</summary>
  public const int DefaultMapBlocks = 63;
  private const string MapType = "Apple_partition_map", FreeType = "Apple_Free";

  /// <summary>One partition-map entry.</summary>
  /// <param name="Start">First block.</param>
  /// <param name="Blocks">Length in blocks.</param>
  /// <param name="Name">Partition name (at most 32 bytes).</param>
  /// <param name="Type">Partition type (at most 32 bytes), e.g. <c>Apple_HFS</c>.</param>
  /// <param name="Status">Status word.</param>
  public sealed record Entry(long Start, long Blocks, string Name, string Type, uint Status) {
    /// <summary>The entry's block as read, kept so an edit leaves untouched entries (boot code, processor) as they were.</summary>
    internal byte[]? Raw { get; init; }

    /// <summary>Whether the entry is free space.</summary>
    public bool IsFree => this.Type == FreeType;
    /// <summary>Whether the entry is the map's own.</summary>
    public bool IsMap => this.Type == MapType;
  }

  private readonly Stream _disk;
  private readonly List<Entry> _entries;
  private readonly long _diskBlocks;
  private bool _planOnly;

  /// <summary>The block size (512 or 2048).</summary>
  public int BlockSize { get; }

  /// <summary>How many entries the map partition has room for.</summary>
  public int Capacity { get; }

  /// <summary>The map's entries, in order.</summary>
  public IReadOnlyList<Entry> Entries => this._entries;

  private ApmEditor(Stream disk, int blockSize, List<Entry> entries) {
    this._disk = disk;
    this.BlockSize = blockSize;
    this._entries = entries;
    this._diskBlocks = disk.Length / blockSize;
    var map = entries.FirstOrDefault(e => e.IsMap)
      ?? throw new InvalidDataException("The Apple Partition Map has no Apple_partition_map entry.");
    if (map.Start != 1)
      throw new InvalidDataException("The Apple_partition_map entry does not start at block 1.");
    this.Capacity = (int)Math.Min(map.Blocks, 4096);
    if (entries.Count > this.Capacity)
      throw new InvalidDataException("The Apple Partition Map holds more entries than its own partition has blocks.");
  }

  /// <summary>
  /// Where a partition of <paramref name="blocks"/> blocks goes: the free run after the last
  /// partition when it holds it, otherwise the last earlier one that does; from a 1 MiB boundary
  /// when the run still fits it there.
  /// </summary>
  /// <exception cref="InvalidOperationException">No free run is large enough.</exception>
  public long PlaceFree(long blocks) {
    var alignment = 1024 * 1024 / this.BlockSize;
    foreach (var free in this._entries.Where(e => e.IsFree).OrderByDescending(e => e.Start)) {
      var aligned = (free.Start + alignment - 1) / alignment * alignment;
      if (aligned + blocks <= free.Start + free.Blocks)
        return aligned;
      if (blocks <= free.Blocks)
        return free.Start;
    }
    throw new InvalidOperationException($"The partition map has no free run of {blocks} blocks.");
  }

  /// <summary>Writes an empty map to <paramref name="disk"/>: the descriptor record, the map entry and one free run.</summary>
  /// <exception cref="ArgumentException">The block size is not 512 or 2048, or the disk is too small for the map.</exception>
  public static ApmEditor CreateEmpty(Stream disk, int blockSize = 512, int mapBlocks = DefaultMapBlocks) {
    ArgumentNullException.ThrowIfNull(disk);
    if (blockSize is not (512 or 2048))
      throw new ArgumentException("Apple Partition Map blocks are 512 or 2048 bytes.", nameof(blockSize));
    var blocks = disk.Length / blockSize;
    if (mapBlocks < 2 || 1 + mapBlocks >= blocks)
      throw new ArgumentException("The disk is too small for the partition map.", nameof(disk));
    if (blocks > uint.MaxValue)
      throw new ArgumentException("An Apple Partition Map addresses at most 2^32 blocks.", nameof(disk));
    disk.Position = 0;
    disk.Write(new byte[blockSize * (1 + mapBlocks)]);
    var editor = new ApmEditor(disk, blockSize, [
      new(1, mapBlocks, "Apple", MapType, 0),
      new(1 + mapBlocks, blocks - 1 - mapBlocks, "Extra", FreeType, 0),
    ]);
    editor.WriteDescriptorRecord();
    editor.Write();
    return editor;
  }

  /// <summary>Reads the map of <paramref name="disk"/> for editing.</summary>
  /// <param name="disk">The disk.</param>
  /// <param name="planOnly">Whether edits are only tried out in memory and never written, to plan a change before making it.</param>
  /// <exception cref="InvalidDataException">The disk carries no well-formed Apple Partition Map.</exception>
  public static ApmEditor Open(Stream disk, bool planOnly = false) {
    ArgumentNullException.ThrowIfNull(disk);
    var head = new byte[4096];
    disk.Position = 0;
    var got = disk.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
    var blockSize = BinaryPrimitives.ReadUInt16BigEndian(head) == DdrSignature
                    && BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2)) is 512 or 2048
      ? BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2))
      : got >= 514 && BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(512)) == EntrySignature ? 512 : 2048;
    var entries = new List<Entry>();
    var block = new byte[blockSize];
    long count = 1;
    for (var i = 0; i < count; ++i) {
      disk.Position = (1L + i) * blockSize;
      if (disk.ReadAtLeast(block, blockSize, throwOnEndOfStream: false) < blockSize
          || BinaryPrimitives.ReadUInt16BigEndian(block) != EntrySignature)
        throw new InvalidDataException($"Apple Partition Map entry {i + 1} is missing.");
      if (i == 0) {
        count = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(4));
        if (count is < 1 or > 4096)
          throw new InvalidDataException("The Apple Partition Map states an implausible entry count.");
      }
      entries.Add(new(
        BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(8)),
        BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(12)),
        Text(block.AsSpan(16, 32)),
        Text(block.AsSpan(48, 32)),
        BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(88))) { Raw = (byte[])block.Clone() });
    }
    return new ApmEditor(disk, blockSize, entries) { _planOnly = planOnly };
  }

  private static string Text(ReadOnlySpan<byte> field) {
    var end = field.IndexOf((byte)0);
    return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
  }

  /// <summary>The partition type an APM entry carries for a filesystem.</summary>
  public static string TypeFor(PartitionType type) => type switch {
    PartitionType.AppleHfsPlus or PartitionType.Fat32Lba => "Apple_HFS",
    PartitionType.AppleUfs => "Apple_UFS",
    _ => "Apple_UNIX_SVR2",
  };

  /// <summary>
  /// Adds a partition over <paramref name="blocks"/> blocks from <paramref name="start"/>, which
  /// must lie inside one free run.
  /// </summary>
  /// <exception cref="InvalidOperationException">The range is not free, or the map is full.</exception>
  public void AddPartition(long start, long blocks, string type, string name) {
    ArgumentException.ThrowIfNullOrEmpty(type);
    if (blocks <= 0)
      throw new ArgumentOutOfRangeException(nameof(blocks), "A partition needs at least one block.");
    if (Encoding.ASCII.GetByteCount(type) > 32 || Encoding.ASCII.GetByteCount(name ?? "") > 32)
      throw new ArgumentException("Partition names and types hold at most 32 bytes.");
    var free = this._entries.FirstOrDefault(e => e.IsFree && e.Start <= start && start + blocks <= e.Start + e.Blocks)
      ?? throw new InvalidOperationException($"Blocks {start}..{start + blocks - 1} are not free space of the partition map.");
    var replacement = new List<Entry>();
    if (start > free.Start)
      replacement.Add(free with { Blocks = start - free.Start, Raw = null });
    replacement.Add(new(start, blocks, name ?? "", type, type is "Apple_HFS" or "Apple_HFSX" ? 0x7Fu : 0x33u));
    if (start + blocks < free.Start + free.Blocks)
      replacement.Add(free with { Start = start + blocks, Blocks = free.Start + free.Blocks - start - blocks, Raw = null });
    var index = this._entries.IndexOf(free);
    var updated = new List<Entry>(this._entries);
    updated.RemoveAt(index);
    updated.InsertRange(index, replacement);
    if (updated.Count > this.Capacity)
      throw new InvalidOperationException($"The partition map has room for {this.Capacity} entries.");
    this._entries.Clear();
    this._entries.AddRange(updated);
    this.Write();
  }

  /// <summary>Turns the partition starting at <paramref name="start"/> into free space, merging it with free neighbours.</summary>
  /// <exception cref="InvalidOperationException">No partition starts there, or it is the map itself.</exception>
  public void DeletePartition(long start) {
    var index = this._entries.FindIndex(e => e.Start == start && !e.IsFree);
    if (index < 0)
      throw new InvalidOperationException($"No partition starts at block {start}.");
    if (this._entries[index].IsMap)
      throw new InvalidOperationException("The partition map itself cannot be deleted.");
    var entry = this._entries[index];
    this._entries[index] = new(entry.Start, entry.Blocks, "Extra", FreeType, 0);
    this.Normalize();
    this.Write();
  }

  /// <summary>Sorts the entries and merges adjacent free runs.</summary>
  private void Normalize() {
    var sorted = this._entries.OrderBy(e => e.Start).ToList();
    var merged = new List<Entry>();
    foreach (var e in sorted)
      if (merged.Count > 0 && merged[^1].IsFree && e.IsFree && merged[^1].Start + merged[^1].Blocks == e.Start)
        merged[^1] = merged[^1] with { Blocks = merged[^1].Blocks + e.Blocks, Raw = null };
      else
        merged.Add(e);
    this._entries.Clear();
    this._entries.AddRange(merged);
  }

  /// <summary>Block 0 of a new map: <c>ER</c>, the block size and the disk's block count, no drivers.</summary>
  private void WriteDescriptorRecord() {
    var ddr = new byte[this.BlockSize];
    BinaryPrimitives.WriteUInt16BigEndian(ddr, DdrSignature);
    BinaryPrimitives.WriteUInt16BigEndian(ddr.AsSpan(2), (ushort)this.BlockSize);
    BinaryPrimitives.WriteUInt32BigEndian(ddr.AsSpan(4), (uint)this._diskBlocks);
    this._disk.Position = 0;
    this._disk.Write(ddr);
  }

  /// <summary>Writes every entry with the current entry count; an entry read and left alone keeps its other fields, unused map blocks are zeroed.</summary>
  private void Write() {
    if (this._planOnly)
      return;
    var bs = this.BlockSize;
    for (var i = 0; i < this.Capacity; ++i) {
      var block = new byte[bs];
      if (i < this._entries.Count && this._entries[i].Raw is { } raw) {
        raw.CopyTo(block, 0);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(4), (uint)this._entries.Count);
      } else if (i < this._entries.Count) {
        var e = this._entries[i];
        BinaryPrimitives.WriteUInt16BigEndian(block, EntrySignature);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(4), (uint)this._entries.Count);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(8), (uint)e.Start);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(12), (uint)e.Blocks);
        Encoding.ASCII.GetBytes(e.Name, block.AsSpan(16, 32));
        Encoding.ASCII.GetBytes(e.Type, block.AsSpan(48, 32));
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(84), (uint)e.Blocks); // data area: the whole partition
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(88), e.Status);
      }
      this._disk.Position = (1L + i) * bs;
      this._disk.Write(block);
    }
    this._disk.Flush();
  }
}
