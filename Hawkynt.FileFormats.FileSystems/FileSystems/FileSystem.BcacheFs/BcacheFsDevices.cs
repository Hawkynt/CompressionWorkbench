#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// The member devices a commit writes to, by member index, where each keeps its fixed
/// structures, and how many copies of metadata and of file data the volume asks for.
/// </summary>
/// <remarks>
/// A bucket is named across the volume by <see cref="BcacheFsBucketId" />: its member index in
/// the bits above <see cref="BcacheFsBucketId.DeviceShift" />, so a single-device volume's buckets
/// keep their own numbers.
/// </remarks>
internal sealed class BcacheFsDevices {
  internal required IReadOnlyDictionary<byte, Stream> Streams { get; init; }
  internal required IReadOnlyDictionary<byte, BcacheFsAllocationBuilder.Geometry> Geometry { get; init; }

  /// <summary>Which data types each member may hold (<c>BCH_MEMBER_DATA_ALLOWED</c>), as a bit per type.</summary>
  internal IReadOnlyDictionary<byte, byte> DataAllowed { get; init; } = new Dictionary<byte, byte>();

  /// <summary>Copies of every b-tree node (<c>metadata_replicas</c>).</summary>
  internal int MetadataReplicas { get; init; } = 1;

  /// <summary>Copies of every data extent (<c>data_replicas</c>).</summary>
  internal int DataReplicas { get; init; } = 1;

  internal IReadOnlyList<byte> Indices => [.. this.Streams.Keys.Order()];

  internal Stream this[byte device] => this.Streams[device];

  internal bool Allows(byte device, byte dataType)
    => !this.DataAllowed.TryGetValue(device, out var mask) || (mask & (1 << dataType)) != 0;

  /// <summary>One device, as this package's writer and the single-stream edits see a volume.</summary>
  internal static BcacheFsDevices Single(Stream image, BcacheFsAllocationBuilder.Geometry? geometry = null) => new() {
    Streams = new Dictionary<byte, Stream> { [0] = image },
    Geometry = new Dictionary<byte, BcacheFsAllocationBuilder.Geometry> { [0] = geometry ?? BcacheFsSuperblockEditor.ReadGeometry(image) },
  };

  /// <summary>Every member <paramref name="core" /> was opened over, each with its own superblock's layout and journal.</summary>
  internal static BcacheFsDevices Of(BcacheFsCoreVolume core) {
    ArgumentNullException.ThrowIfNull(core);
    var geometry = new Dictionary<byte, BcacheFsAllocationBuilder.Geometry>();
    var allowed = new Dictionary<byte, byte>();
    foreach (var (index, stream) in core.Devices) {
      stream.Position = 0;
      geometry[index] = BcacheFsSuperblockEditor.ReadGeometry(stream);
      if (index < core.Members.Count) allowed[index] = core.Members[index].DataAllowed;
    }
    var flags0 = core.Superblock.Flags.Length > 0 ? core.Superblock.Flags[0] : 0UL;
    return new BcacheFsDevices {
      Streams = core.Devices,
      Geometry = geometry,
      DataAllowed = allowed,
      MetadataReplicas = Math.Max(1, (int)((flags0 >> 48) & 0xF)),
      DataReplicas = Math.Max(1, (int)((flags0 >> 52) & 0xF)),
    };
  }

  internal void Flush() {
    foreach (var stream in this.Streams.Values) stream.Flush();
  }
}

/// <summary>A bucket named across the members of a volume: member index above, bucket number below.</summary>
internal static class BcacheFsBucketId {
  internal const int DeviceShift = 48;
  private const long IndexMask = (1L << DeviceShift) - 1;

  internal static long Of(byte device, long bucket) => ((long)device << DeviceShift) | bucket;
  internal static byte Device(long id) => (byte)(id >> DeviceShift);
  internal static long Index(long id) => id & IndexMask;

  /// <summary>The bucket a device sector lies in.</summary>
  internal static long OfSector(byte device, long sector) => Of(device, sector / BucketSectors);

  /// <summary>A pointer's device and sector, as one 64-bit extent or b-tree pointer word holds them.</summary>
  internal static (byte Device, long Sector) Pointer(ulong word) => ((byte)((word >> 48) & 0xFF), PointerSector(word));

  internal static byte PointerDevice(ReadOnlySpan<byte> word) => (byte)((BinaryPrimitives.ReadUInt64LittleEndian(word) >> 48) & 0xFF);
}
