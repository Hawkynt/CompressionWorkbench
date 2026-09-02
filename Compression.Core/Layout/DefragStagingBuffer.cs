#pragma warning disable CS1591
namespace Compression.Core.Layout;

/// <summary>
/// Holds runs of a volume's bytes while the space they came from is reused, so
/// a layout change that has nowhere on disk to park them can still be made.
/// </summary>
/// <remarks>
/// <para>Rearranging a volume in place needs somewhere to put a run whose
/// destination is still occupied. Usually that is a free region of the volume
/// itself, and the more of those there are the less this is needed. A volume
/// with none — a full one — used to leave no way round the cycle at all, and
/// the whole layout was written out again instead.</para>
///
/// <para>Memory is that somewhere. Up to <see cref="MemoryBudgetBytes" /> a run
/// is held in an array; past it the runs go to a scratch file, which is slower
/// but has no ceiling. So the pass gets faster the more free memory there is,
/// and never becomes impossible for want of it.</para>
/// </remarks>
public sealed class DefragStagingBuffer : IDisposable {

  /// <summary>Bytes held in memory before the rest goes to scratch.</summary>
  public const long DefaultMemoryBudgetBytes = 256L * 1024 * 1024;

  private readonly Dictionary<int, byte[]> _inMemory = [];
  private readonly Dictionary<int, (long Offset, long Length)> _spilled = [];
  private readonly long _budget;
  private FileStream? _scratch;
  private string? _scratchPath;
  private long _held;
  private long _scratchEnd;

  /// <summary>
  /// Initializes a new instance of <see cref="DefragStagingBuffer"/>.
  /// </summary>
public DefragStagingBuffer(long memoryBudgetBytes = DefaultMemoryBudgetBytes)
    => this._budget = Math.Max(0, memoryBudgetBytes);

  /// <summary>Bytes this buffer may hold in memory before spilling.</summary>
  public long MemoryBudgetBytes => this._budget;

  /// <summary>Bytes currently held in memory.</summary>
  public long HeldInMemoryBytes => this._held;

  /// <summary>Whether anything had to go to scratch rather than memory.</summary>
  public bool Spilled => this._spilled.Count > 0;

  /// <summary>Reads a run out of the image and holds it under <paramref name="slot" />.</summary>
  public void Park(Stream image, int slot, long offset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    if (length <= 0) return;

    if (length <= this._budget - this._held) {
      var buffer = new byte[length];
      image.Position = offset;
      image.ReadExactly(buffer);
      this._inMemory[slot] = buffer;
      this._held += length;
      return;
    }

    this._scratch ??= OpenScratch(out this._scratchPath);
    var at = this._scratchEnd;
    this._scratch.Position = at;
    Copy(image, offset, this._scratch, length);
    this._scratchEnd += length;
    this._spilled[slot] = (at, length);
  }

  /// <summary>Writes the run held under <paramref name="slot" /> back into the image.</summary>
  public void Unpark(Stream image, int slot, long offset) {
    ArgumentNullException.ThrowIfNull(image);

    if (this._inMemory.Remove(slot, out var buffer)) {
      image.Position = offset;
      image.Write(buffer);
      image.Flush();
      this._held -= buffer.Length;
      return;
    }

    if (!this._spilled.Remove(slot, out var where))
      throw new InvalidOperationException($"Nothing is held under staging slot {slot}.");

    image.Position = offset;
    Copy(this._scratch!, where.Offset, image, where.Length);
    image.Flush();
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() {
    this._inMemory.Clear();
    this._spilled.Clear();
    this._scratch?.Dispose();
    this._scratch = null;
    if (this._scratchPath == null) return;
    try { File.Delete(this._scratchPath); } catch { /* scratch file already gone */ }
    this._scratchPath = null;
  }

  private static FileStream OpenScratch(out string path) {
    path = Path.Combine(Path.GetTempPath(), "cwb_defrag_" + Guid.NewGuid().ToString("N")[..12] + ".stage");
    return new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
      bufferSize: 1 << 16, FileOptions.DeleteOnClose);
  }

  private static void Copy(Stream source, long from, Stream destination, long length) {
    var buffer = new byte[(int)Math.Min(length, 1 << 20)];
    source.Position = from;
    var remaining = length;
    while (remaining > 0) {
      var take = (int)Math.Min(remaining, buffer.Length);
      source.ReadExactly(buffer, 0, take);
      destination.Write(buffer, 0, take);
      remaining -= take;
    }
  }
}
