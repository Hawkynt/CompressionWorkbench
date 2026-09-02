#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.DragonFs;

/// <summary>
/// Moves a file inside a DragonFS volume by moving its directory record and its
/// bytes together, and repointing whoever linked to them.
/// </summary>
/// <remarks>
/// <para>A file here has no address of its own: its bytes begin immediately
/// after the record that names it. So the record travels with the data, and
/// what has to be rewritten is the pointer that reached the record — the
/// <c>next</c> field of the record before it, or the child field of the
/// directory it belongs to.</para>
///
/// <para>The referrer is found by the offset it still names, so a file with a
/// duplicate name cannot send the wrong one somewhere.</para>
/// </remarks>
public sealed class DragonFsBlockMover : IFilesystemBlockMover {

  private const int RecordSize = DragonFsExtentMap.RecordSize;

  /// <summary>Offset of the next-record pointer inside a record.</summary>
  private const int NextOffset = 0;

  /// <summary>Offset of the flags word inside a record.</summary>
  private const int FlagsOffset = 4;

  /// <summary>Offset of the child pointer (directories) inside a record.</summary>
  private const int ChildOffset = 28;

  /// <summary>Nothing to read: the layout is the format's, not the image's.</summary>
  public void Init(Stream image) => ArgumentNullException.ThrowIfNull(image);

  /// <summary>
  /// One byte. A record is reached by an absolute offset, so nothing about the
  /// format asks a file to start on a boundary.
  /// </summary>
  public int BlockSize => 1;

  /// <summary>First byte a file may occupy: past the boot area and the root record.</summary>
  public long FirstDataByte => DragonFsReader.DefaultRootOffset + RecordSize;

  /// <summary>
  /// Each call repoints the record it is given and nothing else, so an owner in
  /// several runs — which this format cannot produce — would be several calls.
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
    if (oldOffset == newOffset) return;

    if (newOffset > uint.MaxValue)
      throw new NotSupportedException(
        $"DragonFs: {newOffset} is past what a record pointer holds.");

    var (referrer, field) = this.FindReferrer(image, oldOffset);
    if (referrer < 0)
      throw new InvalidOperationException(
        $"DragonFs: nothing points at {oldOffset}, so '{fileName}' cannot be repointed.");

    Span<byte> pointer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(pointer, (uint)newOffset);
    image.Position = referrer + field;
    image.Write(pointer);
    image.Flush();
  }

  /// <summary>
  /// The record that points at <paramref name="target" />, and which of its two
  /// pointers does so. Returns -1 when nothing does.
  /// </summary>
  private (long Referrer, int Field) FindReferrer(Stream image, long target) {
    image.Position = 0;
    long rootOffset;
    using (var reader = new DragonFsReader(image)) rootOffset = reader.RootOffset;

    var record = new byte[RecordSize];
    var seen = new HashSet<long>();
    var pending = new Queue<long>();
    pending.Enqueue(rootOffset);

    while (pending.Count > 0) {
      var at = pending.Dequeue();
      while (at > 0 && at + RecordSize <= image.Length && seen.Add(at)) {
        image.Position = at;
        image.ReadExactly(record);
        var next = (long)BinaryPrimitives.ReadUInt32BigEndian(record);
        var flags = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(FlagsOffset));
        if ((flags & 0x0002) != 0) break;                    // end marker
        var child = (long)BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(ChildOffset));

        if (next == target) return (at, NextOffset);
        if ((flags & 0x0001) != 0) {
          if (child == target) return (at, ChildOffset);
          if (child != 0) pending.Enqueue(child);
        }

        if (next == 0) break;
        at = next;
      }
    }

    return (-1, 0);
  }
}
