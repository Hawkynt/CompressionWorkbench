#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Reiser4;

/// <summary>
/// Moves a file's blocks inside a Reiser4 payload area and rewrites the
/// directory entry that said where it started.
/// </summary>
/// <remarks>
/// <para>A file here is the run of blocks that begins at the block its
/// directory entry names, stepping over the allocator bitmaps that sit at
/// stride boundaries. Nothing else records the position — there is one field
/// per file and the rest is implied — so a move is the copy plus those eight
/// bytes.</para>
///
/// <para>Because the position is implied, a file can only be put somewhere its
/// blocks stay in that order: consecutive, bitmaps stepped over. The directory
/// is written once the pass is over and checked against that rule; a layout
/// that breaks it is refused rather than written down.</para>
/// </remarks>
public sealed class Reiser4BlockMover : IFilesystemBlockMover {

  private const ulong NativeLeafBlock = 24;
  private const int NativeItemHeaderBytes = 38;
  private const ushort NativeExtent40Plugin = 5;
  private const byte NativeFileBodyMinor = 4;
  private const uint NativeNodeMagic = 0x52344653;
  private const ulong RootObjectId = 0x2a;

  /// <summary>Where each file's runs are now, in the order its bytes are in.</summary>
  private readonly Dictionary<string, List<long>> _runsOf = new(StringComparer.Ordinal);

  /// <summary>The byte length represented by each corresponding physical run.</summary>
  private readonly Dictionary<string, List<long>> _runLengthsOf = new(StringComparer.Ordinal);

  /// <summary>Where the field naming each file's first block sits.</summary>
  private readonly Dictionary<string, long> _firstBlockFieldOf = new(StringComparer.Ordinal);

  /// <summary>Native object id assigned to each file by the writer's tree profile.</summary>
  private readonly Dictionary<string, ulong> _objectIdOf = new(StringComparer.Ordinal);

  /// <summary>How long each file is, which is what says how many blocks it takes.</summary>
  private readonly Dictionary<string, long> _sizeOf = new(StringComparer.Ordinal);

  private long _imageLength;

  /// <summary>Reads the directory once and notes where every file is.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._runsOf.Clear();
    this._runLengthsOf.Clear();
    this._firstBlockFieldOf.Clear();
    this._objectIdOf.Clear();
    this._sizeOf.Clear();
    this._imageLength = image.Length;

    image.Position = 0;
    using var reader = new Reiser4Reader(image);
    if (!reader.Valid)
      throw new InvalidDataException("Reiser4: the volume does not carry a payload area this reads.");

    foreach (var entry in reader.Entries) {
      if (entry.Size <= 0) continue;
      var runs = reader.EnumerateRuns(entry).ToArray();
      this._runsOf[entry.Name] = runs.Select(static run => run.Offset).ToList();
      this._runLengthsOf[entry.Name] = runs.Select(static run => run.Length).ToList();
      this._sizeOf[entry.Name] = entry.Size;
    }

    var objectId = RootObjectId + 1;
    foreach (var (name, at) in DirectoryFields(image)) {
      this._firstBlockFieldOf[name] = at;
      this._objectIdOf[name] = objectId++;
    }

    // The reserved blocks and the directory chain come first, and the first
    // file starts where they end.
    this.FirstDataByte = this._runsOf.Count == 0
      ? Math.Min(image.Length, 25L * Reiser4Writer.BlockSize)
      : this._runsOf.Values.Select(r => r[0]).Min();
  }

  /// <summary>A block, which is what the directory counts in.</summary>
  public int BlockSize => Reiser4Writer.BlockSize;

  /// <summary>
  /// First byte a file may occupy: past the reserved blocks and the directory,
  /// which is where the first file already sits.
  /// </summary>
  public long FirstDataByte { get; private set; }

  /// <summary>
  /// Each call notes where one run has got to; a file split by the bitmaps it
  /// steps over is simply several calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
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
        $"Reiser4: {newOffset} is not on a {Reiser4Writer.BlockSize}-byte block boundary, which is " +
        "all the directory can name.");

    if (!this._runsOf.TryGetValue(fileName, out var runs))
      throw new InvalidOperationException(
        $"Reiser4: the directory names no file '{fileName}', so it cannot be repointed.");

    // By where the run is now rather than where it began: the planner may put a
    // run down more than once on its way to where it ends up.
    var at = runs.IndexOf(oldOffset);
    if (at < 0)
      throw new InvalidOperationException(
        $"Reiser4: no run of '{fileName}' sits at {oldOffset}, so it cannot be repointed.");

    if (!this._runLengthsOf.TryGetValue(fileName, out var lengths) || at >= lengths.Count)
      throw new InvalidOperationException($"Reiser4: run accounting for '{fileName}' is incomplete.");
    if (length != lengths[at])
      throw new NotSupportedException(
        $"Reiser4: moving only {length} bytes of the {lengths[at]}-byte run of '{fileName}' " +
        "would require splitting its native extent40 item.");

    runs[at] = newOffset;
  }

  /// <summary>
  /// Writes each file's new physical location into both the native extent40 item
  /// and the legacy workbench directory once the pass is over.
  /// </summary>
  public void SettleDirectory(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var native = HasNativeTree(image);
    Span<byte> value = stackalloc byte[8];
    foreach (var (name, runs) in this._runsOf) {
      if (runs.Count == 0) continue;

      if (!native) {
        var expected = ImpliedRuns(runs[0], this._sizeOf[name]);
        if (!expected.SequenceEqual(runs))
          throw new NotSupportedException(
            $"Reiser4: '{name}' would not read back from block {runs[0] / Reiser4Writer.BlockSize} — " +
            "its blocks are where the legacy layout implies no file can be.");
      }

      // Keep the old private directory coherent for pre-native Workbench readers.
      if (this._firstBlockFieldOf.TryGetValue(name, out var field)) {
        BinaryPrimitives.WriteUInt64LittleEndian(value, (ulong)(runs[0] / Reiser4Writer.BlockSize));
        image.Position = field;
        image.Write(value);
      } else if (!native) {
        throw new InvalidOperationException(
          $"Reiser4: the directory holds no entry for '{name}' to write back.");
      }
    }

    if (native)
      this.RepointNativeExtents(image);

    image.Flush();
  }

  /// <summary>True when block 24 is the native leaf written by the tree profile.</summary>
  private static bool HasNativeTree(Stream image) {
    var offset = checked((long)NativeLeafBlock * Reiser4Writer.BlockSize);
    if (!image.CanSeek || offset + Reiser4Writer.BlockSize > image.Length) return false;

    Span<byte> header = stackalloc byte[12];
    image.Position = offset;
    image.ReadExactly(header);
    return BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) == NativeNodeMagic;
  }

  /// <summary>
  /// Repoints only extent40 start blocks in the native leaf. Widths and every
  /// other tree byte stay untouched, so defrag does not silently rebuild metadata.
  /// </summary>
  private void RepointNativeExtents(Stream image) {
    var leafOffset = checked((long)NativeLeafBlock * Reiser4Writer.BlockSize);
    var leaf = new byte[Reiser4Writer.BlockSize];
    image.Position = leafOffset;
    image.ReadExactly(leaf);

    var itemCount = BinaryPrimitives.ReadUInt16LittleEndian(leaf.AsSpan(2, 2));
    var bodiesEnd = BinaryPrimitives.ReadUInt16LittleEndian(leaf.AsSpan(6, 2));
    if (itemCount == 0
        || itemCount > (Reiser4Writer.BlockSize - Reiser4Tree.NodeHeaderBytes) / NativeItemHeaderBytes
        || bodiesEnd < Reiser4Tree.NodeHeaderBytes || bodiesEnd > Reiser4Writer.BlockSize)
      throw new InvalidDataException("Reiser4: malformed native leaf while settling defragmentation.");

    var bodyOffsets = new ushort[itemCount];
    for (var i = 0; i < itemCount; ++i) {
      var header = Reiser4Writer.BlockSize - (i + 1) * NativeItemHeaderBytes;
      bodyOffsets[i] = BinaryPrimitives.ReadUInt16LittleEndian(leaf.AsSpan(header + 32, 2));
    }

    foreach (var (name, objectId) in this._objectIdOf) {
      if (!this._runsOf.TryGetValue(name, out var runs) || runs.Count == 0) continue;
      if (!this._runLengthsOf.TryGetValue(name, out var lengths) || lengths.Count != runs.Count)
        throw new InvalidOperationException($"Reiser4: run accounting for '{name}' is incomplete.");

      var found = false;
      for (var i = 0; i < itemCount; ++i) {
        var header = Reiser4Writer.BlockSize - (i + 1) * NativeItemHeaderBytes;
        var key0 = BinaryPrimitives.ReadUInt64LittleEndian(leaf.AsSpan(header, 8));
        var itemObjectId = BinaryPrimitives.ReadUInt64LittleEndian(leaf.AsSpan(header + 16, 8));
        var plugin = BinaryPrimitives.ReadUInt16LittleEndian(leaf.AsSpan(header + 36, 2));
        if ((byte)(key0 & 0xF) != NativeFileBodyMinor
            || plugin != NativeExtent40Plugin || itemObjectId != objectId)
          continue;

        var body = bodyOffsets[i];
        var end = bodiesEnd;
        foreach (var candidate in bodyOffsets)
          if (candidate > body && candidate < end) end = candidate;
        var bodyLength = end - body;
        if (bodyLength != runs.Count * 16)
          throw new NotSupportedException(
            $"Reiser4: native extent40 body for '{name}' has {bodyLength / 16} runs, " +
            $"but the mover tracks {runs.Count}.");

        for (var r = 0; r < runs.Count; ++r) {
          if ((runs[r] & (Reiser4Writer.BlockSize - 1)) != 0)
            throw new InvalidOperationException($"Reiser4: run of '{name}' is not block aligned.");
          var startBlock = checked((ulong)(runs[r] / Reiser4Writer.BlockSize));
          var width = checked((ulong)((lengths[r] + Reiser4Writer.BlockSize - 1) / Reiser4Writer.BlockSize));
          BinaryPrimitives.WriteUInt64LittleEndian(leaf.AsSpan(body + r * 16, 8), startBlock);
          var existingWidth = BinaryPrimitives.ReadUInt64LittleEndian(leaf.AsSpan(body + r * 16 + 8, 8));
          if (existingWidth != width)
            throw new NotSupportedException(
              $"Reiser4: native extent40 width for '{name}' is {existingWidth} blocks; " +
              $"the mover accounts for {width}.");
        }

        found = true;
        break;
      }

      if (!found)
        throw new InvalidDataException($"Reiser4: native tree has no extent40 item for '{name}'.");
    }

    image.Position = leafOffset;
    image.Write(leaf);
  }

  /// <summary>
  /// The runs a file of this length occupies when it starts at
  /// <paramref name="firstOffset" />: consecutive blocks, broken wherever a
  /// bitmap sits.
  /// </summary>
  private List<long> ImpliedRuns(long firstOffset, long size) {
    var runs = new List<long>();
    var blockSize = (long)Reiser4Writer.BlockSize;
    var block = (ulong)(firstOffset / blockSize);
    var remaining = size;

    while (remaining > 0) {
      while (IsBitmapBlock(block)) ++block;
      var start = (long)block * blockSize;
      if (start < 0 || start >= this._imageLength) break;

      long run = 0;
      while (remaining - run > 0 && !IsBitmapBlock(block)) {
        var take = Math.Min(blockSize, remaining - run);
        take = Math.Min(take, this._imageLength - (start + run));
        if (take <= 0) break;
        run += take;
        ++block;
      }

      if (run <= 0) break;
      runs.Add(start);
      remaining -= run;
    }

    return runs;
  }

  /// <summary>Bitmap blocks sit at stride boundaries, and one sits at block 18.</summary>
  private static bool IsBitmapBlock(ulong block)
    => block == 18 || (block != 0 && block % Reiser4Writer.BlocksPerBitmap == 0);

  /// <summary>Walks the directory chain and yields where each name's first-block field sits.</summary>
  private static IEnumerable<(string Name, long At)> DirectoryFields(Stream image) {
    var master = new byte[Reiser4Writer.BlockSize];
    image.Position = Reiser4Reader.MasterOffset;
    image.ReadExactly(master);

    var block = BinaryPrimitives.ReadUInt64LittleEndian(
      master.AsSpan(Reiser4Writer.MasterPayloadDirOff, 8));

    var visited = new HashSet<ulong>();
    var buffer = new byte[Reiser4Writer.BlockSize];
    while (block != 0 && visited.Add(block)) {
      var at = (long)block * Reiser4Writer.BlockSize;
      if (at < 0 || at + Reiser4Writer.BlockSize > image.Length) yield break;

      image.Position = at;
      image.ReadExactly(buffer);
      if (!buffer.AsSpan(0, Reiser4Writer.DirMagic.Length).SequenceEqual(Reiser4Writer.DirMagic))
        yield break;

      var next = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8, 8));
      var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16, 4));
      for (var i = 0; i < count && i < Reiser4Writer.DirEntriesPerBlock; ++i) {
        var entry = Reiser4Writer.DirHeadSize + i * Reiser4Writer.DirEntrySize;
        var name = ReadName(buffer.AsSpan(entry, Reiser4Writer.DirNameLength));
        if (name.Length == 0) continue;
        yield return (name, at + entry + Reiser4Writer.DirNameLength);
      }

      block = next;
    }
  }

  private static string ReadName(ReadOnlySpan<byte> span) {
    var end = span.IndexOf((byte)0);
    if (end < 0) end = span.Length;
    return end == 0 ? "" : Encoding.UTF8.GetString(span[..end]);
  }
}
