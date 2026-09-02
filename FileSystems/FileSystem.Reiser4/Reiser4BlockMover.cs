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

  /// <summary>Where each file's runs are now, in the order its bytes are in.</summary>
  private readonly Dictionary<string, List<long>> _runsOf = new(StringComparer.Ordinal);

  /// <summary>Where the field naming each file's first block sits.</summary>
  private readonly Dictionary<string, long> _firstBlockFieldOf = new(StringComparer.Ordinal);

  /// <summary>How long each file is, which is what says how many blocks it takes.</summary>
  private readonly Dictionary<string, long> _sizeOf = new(StringComparer.Ordinal);

  private long _imageLength;

  /// <summary>Reads the directory once and notes where every file is.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._runsOf.Clear();
    this._firstBlockFieldOf.Clear();
    this._sizeOf.Clear();
    this._imageLength = image.Length;

    image.Position = 0;
    using var reader = new Reiser4Reader(image);
    if (!reader.Valid)
      throw new InvalidDataException("Reiser4: the volume does not carry a payload area this reads.");

    foreach (var entry in reader.Entries) {
      if (entry.Size <= 0) continue;
      this._runsOf[entry.Name] = reader.EnumerateRuns(entry).Select(r => r.Offset).ToList();
      this._sizeOf[entry.Name] = entry.Size;
    }

    foreach (var (name, at) in DirectoryFields(image))
      this._firstBlockFieldOf[name] = at;

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
  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
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
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
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

    runs[at] = newOffset;
  }

  /// <summary>
  /// Writes each file's first block into the directory, once the pass is over.
  /// </summary>
  /// <remarks>
  /// A file's position is one field and a rule: consecutive blocks from there,
  /// stepping over the bitmaps. So the layout is checked against that rule
  /// before anything is written — a file whose runs no longer read as one
  /// sequence cannot be described at all, and saying otherwise would hand back
  /// a volume that reads as noise.
  /// </remarks>
  public void SettleDirectory(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    Span<byte> value = stackalloc byte[8];
    foreach (var (name, runs) in this._runsOf) {
      if (runs.Count == 0) continue;
      if (!this._firstBlockFieldOf.TryGetValue(name, out var field))
        throw new InvalidOperationException(
          $"Reiser4: the directory holds no entry for '{name}' to write back.");

      var expected = ImpliedRuns(runs[0], this._sizeOf[name]);
      if (!expected.SequenceEqual(runs))
        throw new NotSupportedException(
          $"Reiser4: '{name}' would not read back from block {runs[0] / Reiser4Writer.BlockSize} — " +
          "its blocks are where the format implies no file can be.");

      BinaryPrimitives.WriteUInt64LittleEndian(value, (ulong)(runs[0] / Reiser4Writer.BlockSize));
      image.Position = field;
      image.Write(value);
    }

    image.Flush();
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
