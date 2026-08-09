#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Nwfs;

/// <summary>
/// Reads a NetWare 386 volume: finds the partition, walks the directory chain,
/// and follows a file through the FAT to its bytes.
/// </summary>
/// <remarks>
/// <para>The route is the one a NetWare reader takes. The partition table gives
/// the partition; the hotfix header at sector 32 of it gives the distance to
/// the volume area; the volume area gives the block size and the block the
/// directory starts at; and the data area, which follows the volume area,
/// is what every block number counts from.</para>
///
/// <para>Directory entries are flat. Each names the directory it belongs to
/// rather than being nested inside it, so a path is walked by collecting every
/// entry once and then following parent ids down from the root.</para>
/// </remarks>
public sealed class NwfsReader {

  /// <summary>One thing on the volume: a file or a directory.</summary>
  public sealed record Item(string Path, bool IsDirectory, long Length, uint FirstBlock);

  private readonly byte[] _image;
  private readonly long _dataAreaOffset;
  private readonly int _blockSize;
  private readonly uint _firstSegmentBlock;
  private readonly uint _rootDirectoryBlock;

  /// <summary>What the volume calls itself.</summary>
  public string VolumeName { get; }

  /// <summary>Bytes to a block on this volume.</summary>
  public int BlockSize => this._blockSize;

  private NwfsReader(byte[] image, long dataAreaOffset, int blockSize, uint firstSegmentBlock,
                     uint rootDirectoryBlock, string volumeName) {
    this._image = image;
    this._dataAreaOffset = dataAreaOffset;
    this._blockSize = blockSize;
    this._firstSegmentBlock = firstSegmentBlock;
    this._rootDirectoryBlock = rootDirectoryBlock;
    this.VolumeName = volumeName;
  }

  /// <summary>Opens the first NetWare volume in <paramref name="image" />, or null if there is none.</summary>
  public static NwfsReader? TryOpen(byte[] image) {
    ArgumentNullException.ThrowIfNull(image);

    var hotfix = FindHotfix(image);
    if (hotfix < 0) return null;

    if (hotfix + 28 > image.Length) return null;
    var redirectionSectors = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)hotfix + 24));

    var volumeArea = hotfix + (long)redirectionSectors * NwfsLayout.SectorSize;
    if (volumeArea + 32 + NwfsLayout.VolumeEntryBytes > image.Length) return null;
    if (!image.AsSpan((int)volumeArea, 16).SequenceEqual("NetWare Volumes\0"u8)) return null;

    var count = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)volumeArea + 16));
    if (count == 0) return null;

    var entry = image.AsSpan((int)volumeArea + 32);
    var nameLength = Math.Min(entry[0], (byte)NwfsLayout.MaxVolumeNameLength);
    var name = Encoding.ASCII.GetString(entry.Slice(1, nameLength));
    var firstSegmentBlock = BinaryPrimitives.ReadUInt32LittleEndian(entry[36..]);
    var blockValue = BinaryPrimitives.ReadUInt32LittleEndian(entry[44..]);
    if (blockValue == 0) return null;
    var blockSize = (int)(256 / blockValue * 1024);
    if (!NwfsLayout.IsValidBlockSize(blockSize)) return null;

    var rootDirectory = BinaryPrimitives.ReadUInt32LittleEndian(entry[48..]);
    var dataArea = volumeArea + NwfsLayout.VolumeAreaBytes;
    if (dataArea >= image.Length) return null;

    return new NwfsReader(image, dataArea, blockSize, firstSegmentBlock, rootDirectory, name);
  }

  /// <summary>
  /// Where the hotfix header is. A whole disk names its partition in the table
  /// at sector zero; an image of the partition alone has none, and then the
  /// header sits at its own sector 32.
  /// </summary>
  private static long FindHotfix(ReadOnlySpan<byte> image) {
    const int tableOffset = 446;
    const byte netWare386 = 0x65;

    if (image.Length >= 512 && image[510] == 0x55 && image[511] == 0xAA) {
      for (var i = 0; i < 4; ++i) {
        var e = image.Slice(tableOffset + i * 16, 16);
        if (e[4] != netWare386) continue;

        var start = (long)BinaryPrimitives.ReadUInt32LittleEndian(e[8..]) * NwfsLayout.SectorSize;
        var at = start + NwfsLayout.HotfixOffsetInPartition;
        if (at + 8 <= image.Length && image.Slice((int)at, 8).SequenceEqual("HOTFIX00"u8)) return at;
      }
    }

    return NwfsLayout.HotfixOffsetInPartition + 8 <= image.Length
           && image.Slice((int)NwfsLayout.HotfixOffsetInPartition, 8).SequenceEqual("HOTFIX00"u8)
      ? NwfsLayout.HotfixOffsetInPartition
      : -1;
  }

  private long BlockOffset(uint block) =>
    this._dataAreaOffset + (long)(block - this._firstSegmentBlock) * this._blockSize;

  private uint NextBlock(uint block) {
    var at = this._dataAreaOffset + (long)(block - this._firstSegmentBlock) * NwfsLayout.FatEntryBytes + 4;
    return at + 4 > this._image.Length
      ? NwfsLayout.NoBlock
      : BinaryPrimitives.ReadUInt32LittleEndian(this._image.AsSpan((int)at));
  }

  private sealed record Raw(uint ParentId, bool IsDirectory, string Name, uint Length, uint FirstBlock, uint DirectoryId);

  private List<Raw> ReadDirectory() {
    var items = new List<Raw>();
    var perBlock = this._blockSize / NwfsLayout.DirectoryEntryBytes;
    var block = this._rootDirectoryBlock;
    var guard = 0;

    while (block != NwfsLayout.NoBlock && guard++ < 1 << 20) {
      var at = this.BlockOffset(block);
      if (at < 0 || at + this._blockSize > this._image.Length) break;

      for (var i = 0; i < perBlock; ++i) {
        var e = this._image.AsSpan((int)(at + i * NwfsLayout.DirectoryEntryBytes),
                                   NwfsLayout.DirectoryEntryBytes);
        var parent = BinaryPrimitives.ReadUInt32LittleEndian(e);
        if (parent is NwfsLayout.DirIdAvailable or NwfsLayout.DirIdGrantList or NwfsLayout.DirIdVolumeInfo)
          continue;

        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
        var isDirectory = (attributes & NwfsLayout.AttributeDirectory) != 0;
        var nameLength = Math.Min(e[11], (byte)NwfsLayout.MaxNameLength);
        var name = Encoding.ASCII.GetString(e.Slice(12, nameLength));
        if (name.Length == 0) continue;

        // A file that carries a deletion time is in the salvage area, not the volume.
        if (!isDirectory && BinaryPrimitives.ReadUInt32LittleEndian(e[104..]) != 0) continue;

        items.Add(isDirectory
          ? new Raw(parent, true, name, 0, 0, BinaryPrimitives.ReadUInt32LittleEndian(e[120..]))
          : new Raw(parent, false, name, BinaryPrimitives.ReadUInt32LittleEndian(e[48..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(e[52..]), 0));
      }

      block = this.NextBlock(block);
    }

    return items;
  }

  /// <summary>Everything on the volume, each with the path it is reached by.</summary>
  public List<Item> List() {
    var raw = this.ReadDirectory();
    var found = new List<Item>();
    var pending = new Queue<(uint Id, string Prefix)>();
    pending.Enqueue((NwfsLayout.RootDirectoryId, ""));

    var seen = new HashSet<uint> { NwfsLayout.RootDirectoryId };
    while (pending.Count > 0) {
      var (id, prefix) = pending.Dequeue();
      foreach (var item in raw) {
        if (item.ParentId != id) continue;

        var path = prefix.Length == 0 ? item.Name : prefix + "/" + item.Name;
        if (item.IsDirectory) {
          found.Add(new Item(path, true, 0, 0));
          if (seen.Add(item.DirectoryId)) pending.Enqueue((item.DirectoryId, path));
        } else
          found.Add(new Item(path, false, item.Length, item.FirstBlock));
      }
    }

    return found;
  }

  /// <summary>The bytes of the file at <paramref name="path" />, or null if there is none.</summary>
  public byte[]? ReadFile(string path) {
    ArgumentNullException.ThrowIfNull(path);
    var wanted = path.Replace('\\', '/').Trim('/');
    var item = this.List().Find(i => !i.IsDirectory
                                     && string.Equals(i.Path, wanted, StringComparison.OrdinalIgnoreCase));
    return item == null ? null : this.Read(item);
  }

  /// <summary>The bytes of <paramref name="item" />, followed through the FAT.</summary>
  public byte[] Read(Item item) {
    ArgumentNullException.ThrowIfNull(item);
    var data = new byte[item.Length];
    var block = item.FirstBlock;
    var written = 0;
    var guard = 0;

    while (block != NwfsLayout.NoBlock && written < data.Length && guard++ < 1 << 24) {
      var at = this.BlockOffset(block);
      if (at < 0 || at >= this._image.Length) break;

      var take = Math.Min(this._blockSize, data.Length - written);
      if (at + take > this._image.Length) take = (int)(this._image.Length - at);
      if (take <= 0) break;

      this._image.AsSpan((int)at, take).CopyTo(data.AsSpan(written));
      written += take;
      block = this.NextBlock(block);
    }

    return data;
  }
}
