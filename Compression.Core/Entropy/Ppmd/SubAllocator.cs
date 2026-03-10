namespace Compression.Core.Entropy.Ppmd;

/// <summary>
/// Fixed-size memory pool allocator for PPMd.
/// Uses 12-byte units with free lists for efficient allocation and deallocation.
/// Memory is managed as a single contiguous byte array with bump-pointer allocation
/// and segregated free lists indexed by unit count.
/// </summary>
internal sealed class SubAllocator {
  /// <summary>Maximum number of free list buckets (unit counts 1..128).</summary>
  private const int MaxFreeListBuckets = 128;

  /// <summary>Sentinel value indicating no valid offset (end of free list or allocation failure).</summary>
  private const int NullOffset = -1;

  private readonly byte[] _memory;
  private readonly int _memorySize;
  private readonly int[] _freeList;
  private int _loUnit;
  private int _hiUnit;

  /// <summary>
  /// Initializes a new <see cref="SubAllocator"/> with the specified memory size.
  /// </summary>
  /// <param name="memorySize">Total memory pool size in bytes. Must be at least <see cref="PpmdConstants.MinMemorySize"/>.</param>
  public SubAllocator(int memorySize) {
    if (memorySize < PpmdConstants.MinMemorySize)
      throw new ArgumentOutOfRangeException(nameof(memorySize),
        $"Memory size must be at least {PpmdConstants.MinMemorySize} bytes.");

    this._memorySize = memorySize;
    this._memory = new byte[memorySize];
    this._freeList = new int[MaxFreeListBuckets + 1];
    Reset();
  }

  /// <summary>
  /// Gets the total memory pool size in bytes.
  /// </summary>
  public int MemorySize => this._memorySize;

  /// <summary>
  /// Allocates space for a context node (one unit). Returns the offset into memory.
  /// </summary>
  /// <returns>Offset of the allocated context, or <c>-1</c> if allocation failed.</returns>
  public int AllocContext() => AllocUnits(1);

  /// <summary>
  /// Allocates the specified number of contiguous units.
  /// Tries the corresponding free list first, then falls back to bump allocation.
  /// </summary>
  /// <param name="numUnits">Number of units to allocate (1..128).</param>
  /// <returns>Offset of the allocated block, or <c>-1</c> if allocation failed.</returns>
  public int AllocUnits(int numUnits) {
    if (numUnits < 1 || numUnits > MaxFreeListBuckets)
      throw new ArgumentOutOfRangeException(nameof(numUnits));

    // Try the free list for this size
    if (this._freeList[numUnits] != NullOffset) {
      int offset = this._freeList[numUnits];
      this._freeList[numUnits] = GetNextFreePointer(offset);
      return offset;
    }

    // Try a larger free list and split
    for (int i = numUnits + 1; i <= MaxFreeListBuckets; ++i) {
      if (this._freeList[i] != NullOffset) {
        int offset = this._freeList[i];
        this._freeList[i] = GetNextFreePointer(offset);

        // Return the excess to a smaller free list
        int excess = i - numUnits;
        if (excess > 0 && excess <= MaxFreeListBuckets) {
          int excessOffset = offset + numUnits * PpmdConstants.UnitSize;
          SetNextFreePointer(excessOffset, this._freeList[excess]);
          this._freeList[excess] = excessOffset;
        }

        return offset;
      }
    }

    // Bump allocation from the low end
    int bytesNeeded = numUnits * PpmdConstants.UnitSize;
    if (this._loUnit + bytesNeeded <= this._hiUnit) {
      int offset = this._loUnit;
      this._loUnit += bytesNeeded;
      return offset;
    }

    return NullOffset;
  }

  /// <summary>
  /// Frees the specified number of units at the given offset, returning them to the free list.
  /// </summary>
  /// <param name="offset">Offset of the block to free.</param>
  /// <param name="numUnits">Number of units to free.</param>
  public void FreeUnits(int offset, int numUnits) {
    if (numUnits < 1 || numUnits > MaxFreeListBuckets)
      throw new ArgumentOutOfRangeException(nameof(numUnits));

    SetNextFreePointer(offset, this._freeList[numUnits]);
    this._freeList[numUnits] = offset;
  }

  /// <summary>
  /// Shrinks an existing allocation from <paramref name="oldUnits"/> to <paramref name="newUnits"/>,
  /// returning the excess units to the appropriate free list.
  /// </summary>
  /// <param name="offset">Offset of the block to shrink.</param>
  /// <param name="oldUnits">Current number of units.</param>
  /// <param name="newUnits">Desired number of units (must be less than <paramref name="oldUnits"/>).</param>
  public void ShrinkUnits(int offset, int oldUnits, int newUnits) {
    if (newUnits >= oldUnits)
      return;

    int excess = oldUnits - newUnits;
    if (excess > 0 && excess <= MaxFreeListBuckets) {
      int excessOffset = offset + newUnits * PpmdConstants.UnitSize;
      FreeUnits(excessOffset, excess);
    }
  }

  /// <summary>
  /// Resets the allocator, reclaiming all previously allocated memory.
  /// </summary>
  public void Reset() {
    for (int i = 0; i <= MaxFreeListBuckets; ++i)
      this._freeList[i] = NullOffset;

    this._loUnit = 0;
    this._hiUnit = this._memorySize;
    this._memory.AsSpan().Clear();
  }

  /// <summary>
  /// Gets a span of memory at the specified offset and length.
  /// </summary>
  /// <param name="offset">Byte offset into the memory pool.</param>
  /// <param name="length">Number of bytes to span.</param>
  /// <returns>A span over the requested memory region.</returns>
  public Span<byte> GetSpan(int offset, int length) => this._memory.AsSpan(offset, length);

  /// <summary>
  /// Reads a 4-byte little-endian integer at the specified offset.
  /// </summary>
  /// <param name="offset">Byte offset into the memory pool.</param>
  /// <returns>The 32-bit integer value.</returns>
  public int GetInt(int offset) => BitConverter.ToInt32(this._memory, offset);

  /// <summary>
  /// Writes a 4-byte little-endian integer at the specified offset.
  /// </summary>
  /// <param name="offset">Byte offset into the memory pool.</param>
  /// <param name="value">The 32-bit integer value to write.</param>
  public void SetInt(int offset, int value) => BitConverter.TryWriteBytes(this._memory.AsSpan(offset, 4), value);

  /// <summary>
  /// Reads a 2-byte little-endian unsigned integer at the specified offset.
  /// </summary>
  /// <param name="offset">Byte offset into the memory pool.</param>
  /// <returns>The 16-bit unsigned integer value.</returns>
  public ushort GetUShort(int offset) => BitConverter.ToUInt16(this._memory, offset);

  /// <summary>
  /// Writes a 2-byte little-endian unsigned integer at the specified offset.
  /// </summary>
  /// <param name="offset">Byte offset into the memory pool.</param>
  /// <param name="value">The 16-bit unsigned integer value to write.</param>
  public void SetUShort(int offset, ushort value) => BitConverter.TryWriteBytes(this._memory.AsSpan(offset, 2), value);

  /// <summary>
  /// Reads a single byte at the specified offset.
  /// </summary>
  /// <param name="offset">Byte offset into the memory pool.</param>
  /// <returns>The byte value.</returns>
  public byte GetByte(int offset) => this._memory[offset];

  /// <summary>
  /// Writes a single byte at the specified offset.
  /// </summary>
  /// <param name="offset">Byte offset into the memory pool.</param>
  /// <param name="value">The byte value to write.</param>
  public void SetByte(int offset, byte value) => this._memory[offset] = value;

  /// <summary>
  /// Reads the next-free-block pointer stored at the start of a free block.
  /// </summary>
  private int GetNextFreePointer(int offset) => GetInt(offset);

  /// <summary>
  /// Writes the next-free-block pointer at the start of a free block.
  /// </summary>
  private void SetNextFreePointer(int offset, int nextOffset) => SetInt(offset, nextOffset);

}
