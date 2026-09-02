#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Reiser4;

/// <summary>
/// Reads a Reiser4 image: the master superblock's label, UUID and block size, and
/// the files a <see cref="Reiser4Writer" /> placed in the workbench-layout payload area.
/// <para>
/// The reserved blocks of a workbench-written image are byte-exact
/// <c>mkfs.reiser4</c> captures describing an empty storage tree, so there is no
/// reiser4 tree here to walk. Files live past those blocks, announced by a marker
/// in the master superblock's spare region and described by a chained directory —
/// the layout <see cref="Reiser4Writer" /> documents. An image from a real
/// <c>mkfs.reiser4</c> carries no marker and surfaces no entries; its storage tree
/// (extent40 bodies keyed by file offset, cde40 directory units) is out of scope.
/// </para>
/// </summary>
public sealed class Reiser4Reader : IDisposable {

  /// <summary>Byte offset of the master superblock: block 16 at a 4 KB block size.</summary>
  public const long MasterOffset = 65536;

  private static readonly byte[] MasterMagic = "ReIsEr4"u8.ToArray();

  private readonly ImageAccessor _image;
  private readonly List<Entry> _entries = [];

  /// <summary>True when the image carries a valid Reiser4 master superblock.</summary>
  public bool Valid { get; }

  /// <summary>Filesystem block size from the master superblock.</summary>
  public int BlockSize { get; } = Reiser4Writer.BlockSize;

  /// <summary>Volume label from the master superblock.</summary>
  public string Label { get; } = "";

  /// <summary>Volume UUID from the master superblock, as hex.</summary>
  public string UuidHex { get; } = "";

  /// <summary>Files the payload area holds. Empty for an image without the marker.</summary>
  public IReadOnlyList<Entry> Entries => this._entries;

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._image.Length;

  /// <summary>
  /// Initializes a new instance of <see cref="Reiser4Reader"/>.
  /// </summary>
public Reiser4Reader(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    // Blocks are pulled on demand: the metadata is a handful of blocks however
    // many gigabytes of payload follow it.
    this._image = new ImageAccessor(stream, leaveOpen);
    if (this._image.Length < MasterOffset + Reiser4Writer.BlockSize) return;

    var master = this._image.Read(MasterOffset, Reiser4Writer.BlockSize);
    if (!master.AsSpan(0, MasterMagic.Length).SequenceEqual(MasterMagic)) return;
    this.Valid = true;

    var blockSize = BinaryPrimitives.ReadUInt16LittleEndian(master.AsSpan(18, 2));
    if (blockSize is >= 512 and <= 8192) this.BlockSize = blockSize;
    this.UuidHex = Convert.ToHexString(master.AsSpan(20, 16));
    this.Label = ReadCString(master.AsSpan(36, 16));

    if (!master.AsSpan(Reiser4Writer.MasterPayloadMarkerOff, Reiser4Writer.PayloadMarker.Length)
        .SequenceEqual(Reiser4Writer.PayloadMarker))
      return;

    var dirBlock = BinaryPrimitives.ReadUInt64LittleEndian(
      master.AsSpan(Reiser4Writer.MasterPayloadDirOff, 8));
    this.ReadDirectory(dirBlock);
  }

  /// <summary>One file in the payload area: its name, first block and byte length.</summary>
  public sealed record Entry(string Name, ulong FirstBlock, long Size);

  /// <summary>Reads a file's contents. Only valid below the array limit.</summary>
  public byte[] Extract(Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size > Array.MaxLength)
      throw new IOException(
        $"Reiser4: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    using var buffer = new MemoryStream();
    this.ExtractTo(entry, buffer);
    return buffer.ToArray();
  }

  /// <summary>
  /// Writes <paramref name="entry" />'s contents into <paramref name="destination" />.
  /// A file's blocks are consecutive apart from any block-allocator bitmap they
  /// straddle, which the walk steps over exactly as the writer did. Returns the
  /// number of bytes written.
  /// </summary>
  public long ExtractTo(Entry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.Size <= 0) return 0;

    var blocksPerBitmap = Reiser4Writer.BlocksPerBitmap;
    var block = entry.FirstBlock;
    long written = 0;
    while (written < entry.Size) {
      while (IsBitmapBlock(block, blocksPerBitmap)) ++block;
      var offset = (long)block * this.BlockSize;
      if (offset < 0 || offset >= this._image.Length) break;
      var take = (int)Math.Min(Math.Min(this.BlockSize, entry.Size - written),
        this._image.Length - offset);
      if (take <= 0) break;
      this._image.CopyTo(offset, destination, take);
      written += take;
      ++block;
    }
    return written;
  }

  /// <summary>
  /// Where an entry's bytes are: one run per stretch of consecutive blocks, the
  /// block-allocator bitmaps stepped over exactly as <see cref="ExtractTo" />
  /// steps over them. A file is not one contiguous run whenever a bitmap falls
  /// inside it.
  /// </summary>
  public IEnumerable<(long Offset, long Length)> EnumerateRuns(Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size <= 0) yield break;

    var blocksPerBitmap = Reiser4Writer.BlocksPerBitmap;
    var block = entry.FirstBlock;
    long remaining = entry.Size;
    while (remaining > 0) {
      while (IsBitmapBlock(block, blocksPerBitmap)) ++block;
      var start = (long)block * this.BlockSize;
      if (start < 0 || start >= this._image.Length) yield break;

      // Take every block up to the next bitmap in one run.
      long run = 0;
      while (remaining - run > 0 && !IsBitmapBlock(block, blocksPerBitmap)) {
        var take = Math.Min((long)this.BlockSize, remaining - run);
        take = Math.Min(take, this._image.Length - (start + run));
        if (take <= 0) break;
        run += take;
        ++block;
      }
      if (run <= 0) yield break;
      yield return (start, run);
      remaining -= run;
    }
  }

  private void ReadDirectory(ulong firstBlock) {
    var visited = new HashSet<ulong>();
    var block = firstBlock;
    while (block != 0 && visited.Add(block)) {
      var offset = (long)block * this.BlockSize;
      if (offset < 0 || offset + this.BlockSize > this._image.Length) break;
      var buf = this._image.Read(offset, this.BlockSize);
      if (!buf.AsSpan(0, Reiser4Writer.DirMagic.Length).SequenceEqual(Reiser4Writer.DirMagic)) break;

      var next = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(8, 8));
      var count = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16, 4));
      var capacity = (this.BlockSize - Reiser4Writer.DirHeadSize) / Reiser4Writer.DirEntrySize;
      for (var i = 0; i < count && i < capacity; ++i) {
        var o = Reiser4Writer.DirHeadSize + i * Reiser4Writer.DirEntrySize;
        var name = ReadCString(buf.AsSpan(o, Reiser4Writer.DirNameLength));
        if (name.Length == 0) continue;
        var first = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(o + Reiser4Writer.DirNameLength, 8));
        var size = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(o + Reiser4Writer.DirNameLength + 8, 8));
        if (size < 0) continue;
        this._entries.Add(new Entry(name, first, size));
      }
      block = next;
    }
  }

  private static bool IsBitmapBlock(ulong block, ulong blocksPerBitmap)
    => block == 18 || (block != 0 && block % blocksPerBitmap == 0);

  private static string ReadCString(ReadOnlySpan<byte> span) {
    var n = span.IndexOf((byte)0);
    if (n < 0) n = span.Length;
    return n == 0 ? "" : Encoding.UTF8.GetString(span[..n]);
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() => this._image.Dispose();
}
