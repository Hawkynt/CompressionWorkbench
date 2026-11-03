#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Reiser4;

/// <summary>
/// Moves the data blocks of a Reiser4 volume's extent files in place and repoints the extent
/// units that name them, wherever in the tree their twig nodes sit.
/// </summary>
/// <remarks>
/// <para>Every data run a file has is one extent unit: a start block and a width in some twig
/// node. A move is the copy plus that unit's start, so the planner may move each unit's run on
/// its own. Units are tracked by where their run currently is; once the pass is over
/// <see cref="SettleDirectory" /> writes every new start into its twig, re-seals node41 checksums,
/// and moves the allocator bits: the blocks every unit left are cleared and the blocks it landed on
/// set, so no other bit of the bitmap changes.</para>
/// <para>Tails and compressed clusters live inside tree nodes and do not move; nor do the nodes.
/// A volume whose journal still holds transactions, or a file whose extent covers blocks past its
/// end, is refused, and the caller falls back to a staged rebuild.</para>
/// </remarks>
public sealed class Reiser4BlockMover : IFilesystemBlockMover {

  private sealed class Unit {
    internal required ulong Node { get; init; }
    internal required int Field { get; init; }
    internal required long Original { get; init; }
    internal required long Length { get; init; }
    internal required ulong Width { get; init; }
    internal long Current { get; set; }
  }

  private readonly Dictionary<string, List<Unit>> _unitsOf = new(StringComparer.Ordinal);
  private ushort _nodePlugin;
  private long _imageLength;

  /// <summary>Reads the tree once and notes where every extent unit is.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._unitsOf.Clear();
    this._imageLength = image.Length;
    image.Position = 0;
    using var volume = new Reiser4Volume(image);
    if (!volume.TreeValid)
      throw new InvalidDataException($"Reiser4: the volume cannot be moved in place: {volume.Problem}.");
    if (volume.ReplayedTransactions > 0)
      throw new NotSupportedException("Reiser4: the journal holds transactions to replay; blocks cannot be moved in place.");
    this._nodePlugin = volume.NodePlugin;
    var seen = new HashSet<Reiser4Key>();
    foreach (var (path, _, obj, _) in volume.Paths) {
      if (obj.Stat.Kind != Reiser4Stat.Regular || obj.Body.Count == 0 || obj.Body[0].Plugin != Reiser4ItemPlugin.Extent || !seen.Add(obj.StatKey))
        continue;
      var remaining = (long)obj.Stat.Size;
      var units = new List<Unit>();
      foreach (var item in obj.Body) {
        var body = volume.ReadBody(item);
        for (var at = 0; at < body.Length; at += 16) {
          var start = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(at));
          var width = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(at + 8));
          var bytes = (long)Math.Min((ulong)Math.Max(0, remaining), width * Reiser4Writer.BlockSize);
          remaining -= (long)(width * Reiser4Writer.BlockSize);
          if (start == 0) continue;
          if ((ulong)((bytes + Reiser4Writer.BlockSize - 1) / Reiser4Writer.BlockSize) != width)
            throw new NotSupportedException($"Reiser4: '{path}' holds blocks past its end, which cannot be moved in place.");
          units.Add(new Unit {
            Node = item.Block, Field = item.BodyOffset + at, Original = checked((long)start * Reiser4Writer.BlockSize),
            Length = bytes, Width = width, Current = checked((long)start * Reiser4Writer.BlockSize),
          });
        }
      }
      if (units.Count > 0) this._unitsOf[path] = units;
    }
  }

  /// <summary>A block, which is what an extent unit counts in.</summary>
  public int BlockSize => Reiser4Writer.BlockSize;

  /// <summary>First byte a file may occupy: past the jump area, the superblocks, the journal, the status and backup blocks and the first two nodes.</summary>
  public long FirstDataByte => Math.Min(this._imageLength, 25L * Reiser4Writer.BlockSize);

  /// <summary>Each call notes where one unit's run has got to.</summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>A run may be held outside the volume while the rest of the layout moves.</summary>
  public bool SupportsHeldRuns => true;

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._imageLength == 0) this.Init(image);
    if (oldOffset == newOffset) return;
    if (newOffset % Reiser4Writer.BlockSize != 0)
      throw new NotSupportedException(
        $"Reiser4: {newOffset} is not on a {Reiser4Writer.BlockSize}-byte block boundary, which is all an extent can name.");
    if (!this._unitsOf.TryGetValue(fileName, out var units))
      throw new InvalidOperationException($"Reiser4: the tree names no extent file '{fileName}', so it cannot be repointed.");
    // By where the run is now rather than where it began: the planner may put a run down more
    // than once on its way to where it ends up.
    var unit = units.FirstOrDefault(u => u.Current == oldOffset)
      ?? throw new InvalidOperationException($"Reiser4: no run of '{fileName}' sits at {oldOffset}, so it cannot be repointed.");
    if (length != unit.Length)
      throw new NotSupportedException(
        $"Reiser4: moving only {length} bytes of the {unit.Length}-byte run of '{fileName}' would require splitting its extent unit.");
    unit.Current = newOffset;
  }

  /// <summary>Writes every unit's new start into its twig and moves the allocator bits with it.</summary>
  public void SettleDirectory(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var units = this._unitsOf.Values.SelectMany(static u => u).ToList();
    var nodes = new Dictionary<ulong, byte[]>();
    byte[] Node(ulong block) {
      if (nodes.TryGetValue(block, out var cached)) return cached;
      var buffer = new byte[Reiser4Writer.BlockSize];
      image.Position = checked((long)block * Reiser4Writer.BlockSize);
      image.ReadExactly(buffer);
      return nodes[block] = buffer;
    }

    var bitmaps = new Dictionary<ulong, byte[]>();
    byte[] Bitmap(ulong index) {
      if (bitmaps.TryGetValue(index, out var cached)) return cached;
      var buffer = new byte[Reiser4Writer.BlockSize];
      image.Position = checked((long)(index == 0 ? 18 : index * Reiser4Writer.BlocksPerBitmap) * Reiser4Writer.BlockSize);
      image.ReadExactly(buffer);
      return bitmaps[index] = buffer;
    }
    void Mark(long offset, ulong width, bool used) {
      var first = (ulong)(offset / Reiser4Writer.BlockSize);
      for (var block = first; block < first + width; ++block) {
        var map = Bitmap(block / Reiser4Writer.BlocksPerBitmap);
        var bit = block % Reiser4Writer.BlocksPerBitmap;
        ref var cell = ref map[4 + (int)(bit >> 3)];
        cell = used ? (byte)(cell | 1 << (int)(bit & 7)) : (byte)(cell & ~(1 << (int)(bit & 7)));
      }
    }

    foreach (var unit in units.Where(static u => u.Current != u.Original)) Mark(unit.Original, unit.Width, used: false);
    foreach (var unit in units.Where(static u => u.Current != u.Original)) {
      if (unit.Current < 0 || unit.Current % Reiser4Writer.BlockSize != 0)
        throw new InvalidOperationException("Reiser4: a run did not land on a block boundary inside the volume.");
      Mark(unit.Current, unit.Width, used: true);
      BinaryPrimitives.WriteUInt64LittleEndian(Node(unit.Node).AsSpan(unit.Field), (ulong)(unit.Current / Reiser4Writer.BlockSize));
    }

    foreach (var (block, node) in nodes) {
      if (this._nodePlugin == 1) Reiser4Volume.SealNode41(node);
      image.Position = checked((long)block * Reiser4Writer.BlockSize);
      image.Write(node);
    }
    foreach (var (index, map) in bitmaps) {
      Reiser4Volume.SealBitmap(map);
      image.Position = checked((long)(index == 0 ? 18 : index * Reiser4Writer.BlocksPerBitmap) * Reiser4Writer.BlockSize);
      image.Write(map);
    }
    image.Flush();
  }
}
