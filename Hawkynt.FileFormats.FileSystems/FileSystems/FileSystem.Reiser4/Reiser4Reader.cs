#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Reiser4;

/// <summary>
/// Reads a Reiser4 image: the master superblock's label, UUID and block size,
/// plus regular files represented by the native stat40/cde40/extent40 items
/// emitted by <see cref="Reiser4Writer"/>. The older workbench payload directory
/// remains a compatibility fallback for images written before native tree items
/// became authoritative.
/// </summary>
public sealed class Reiser4Reader : IDisposable {

  /// <summary>Byte offset of the master superblock: block 16 at a 4 KB block size.</summary>
  public const long MasterOffset = 65536;

  private const ulong NativeLeafBlock = 24;
  private const int ItemHeaderBytes = 38;
  private const int DirectoryUnitHeaderBytes = 26;
  private const int TargetKeyBytes = 24;
  private const ushort PluginStat40 = 0;
  private const ushort PluginCde40 = 2;
  private const ushort PluginExtent40 = 5;
  private const byte MinorStatData = 1;
  private const byte MinorFileBody = 4;
  private const ulong HashedNameBit = 0x0100000000000000;

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

  /// <summary>Files found in the native tree, or in the legacy payload directory.</summary>
  public IReadOnlyList<Entry> Entries => this._entries;

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._image.Length;

  /// <summary>
  /// Initializes a new instance of <see cref="Reiser4Reader"/>.
  /// </summary>
  public Reiser4Reader(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    this._image = new ImageAccessor(stream, leaveOpen);
    if (this._image.Length < MasterOffset + Reiser4Writer.BlockSize) return;

    var master = this._image.Read(MasterOffset, Reiser4Writer.BlockSize);
    if (!master.AsSpan(0, MasterMagic.Length).SequenceEqual(MasterMagic)) return;
    this.Valid = true;

    var blockSize = BinaryPrimitives.ReadUInt16LittleEndian(master.AsSpan(18, 2));
    if (blockSize is >= 512 and <= 8192) this.BlockSize = blockSize;
    this.UuidHex = Convert.ToHexString(master.AsSpan(20, 16));
    this.Label = ReadCString(master.AsSpan(36, 16));

    // Native metadata is authoritative. The private payload directory is kept
    // only so old workbench images remain readable while they are migrated.
    if (this.TryReadNativeTree()) return;

    if (!master.AsSpan(Reiser4Writer.MasterPayloadMarkerOff, Reiser4Writer.PayloadMarker.Length)
        .SequenceEqual(Reiser4Writer.PayloadMarker))
      return;

    var dirBlock = BinaryPrimitives.ReadUInt64LittleEndian(
      master.AsSpan(Reiser4Writer.MasterPayloadDirOff, 8));
    this.ReadLegacyDirectory(dirBlock);
  }

  internal readonly record struct NativeRun(ulong Start, ulong Width);

  /// <summary>One regular file: its name, first data block and byte length.</summary>
  public sealed record Entry(string Name, ulong FirstBlock, long Size) {
    internal IReadOnlyList<NativeRun>? NativeRuns { get; init; }
  }

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

  /// <summary>Writes <paramref name="entry"/>'s contents into <paramref name="destination"/>.</summary>
  public long ExtractTo(Entry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.Size <= 0) return 0;

    if (entry.NativeRuns is { } runs)
      return this.ExtractNative(runs, entry.Size, destination);

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

  private long ExtractNative(IReadOnlyList<NativeRun> runs, long size, Stream destination) {
    long written = 0;
    foreach (var run in runs) {
      if (written >= size || run.Width == 0) break;
      var offset = checked((long)run.Start * this.BlockSize);
      if (offset < 0 || offset >= this._image.Length) break;
      var runBytes = checked((long)Math.Min(run.Width, (ulong)(long.MaxValue / this.BlockSize)) * this.BlockSize);
      var take = Math.Min(Math.Min(runBytes, size - written), this._image.Length - offset);
      if (take <= 0) break;
      this._image.CopyTo(offset, destination, take);
      written += take;
    }
    return written;
  }

  /// <summary>Where an entry's bytes live as physical byte runs.</summary>
  public IEnumerable<(long Offset, long Length)> EnumerateRuns(Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size <= 0) yield break;

    if (entry.NativeRuns is { } nativeRuns) {
      long remaining = entry.Size;
      foreach (var run in nativeRuns) {
        if (remaining <= 0 || run.Width == 0) yield break;
        var offset = checked((long)run.Start * this.BlockSize);
        if (offset < 0 || offset >= this._image.Length) yield break;
        var capacity = checked((long)Math.Min(run.Width, (ulong)(long.MaxValue / this.BlockSize)) * this.BlockSize);
        var length = Math.Min(Math.Min(capacity, remaining), this._image.Length - offset);
        if (length <= 0) yield break;
        yield return (offset, length);
        remaining -= length;
      }
      yield break;
    }

    var blocksPerBitmap = Reiser4Writer.BlocksPerBitmap;
    var block = entry.FirstBlock;
    long remainingLegacy = entry.Size;
    while (remainingLegacy > 0) {
      while (IsBitmapBlock(block, blocksPerBitmap)) ++block;
      var start = (long)block * this.BlockSize;
      if (start < 0 || start >= this._image.Length) yield break;

      long run = 0;
      while (remainingLegacy - run > 0 && !IsBitmapBlock(block, blocksPerBitmap)) {
        var take = Math.Min((long)this.BlockSize, remainingLegacy - run);
        take = Math.Min(take, this._image.Length - (start + run));
        if (take <= 0) break;
        run += take;
        ++block;
      }
      if (run <= 0) yield break;
      yield return (start, run);
      remainingLegacy -= run;
    }
  }

  /// <summary>
  /// Reads the single native leaf profile emitted by <see cref="Reiser4Tree"/>.
  /// Item layout is decoded independently from the writer: item headers name the
  /// plugin and key, cde40 units point at object ids, stat40 supplies byte sizes,
  /// and extent40 supplies physical runs. Unsupported/corrupt layouts fail closed
  /// and leave the legacy fallback available.
  /// </summary>
  private bool TryReadNativeTree() {
    if (this.BlockSize != Reiser4Writer.BlockSize) return false;
    var offset = checked((long)NativeLeafBlock * this.BlockSize);
    if (offset + this.BlockSize > this._image.Length) return false;

    var leaf = this._image.Read(offset, this.BlockSize);
    if (leaf.Length != this.BlockSize
        || BinaryPrimitives.ReadUInt32LittleEndian(leaf.AsSpan(8, 4)) != unchecked((uint)Reiser4Tree.NodeMagic))
      return false;

    var itemCount = BinaryPrimitives.ReadUInt16LittleEndian(leaf.AsSpan(2, 2));
    var bodiesEnd = BinaryPrimitives.ReadUInt16LittleEndian(leaf.AsSpan(6, 2));
    if (itemCount == 0 || itemCount > (this.BlockSize - Reiser4Tree.NodeHeaderBytes) / ItemHeaderBytes
        || bodiesEnd < Reiser4Tree.NodeHeaderBytes || bodiesEnd > this.BlockSize - itemCount * ItemHeaderBytes)
      return false;

    var items = new List<NativeItem>(itemCount);
    for (var i = 0; i < itemCount; ++i) {
      var header = this.BlockSize - (i + 1) * ItemHeaderBytes;
      var key0 = BinaryPrimitives.ReadUInt64LittleEndian(leaf.AsSpan(header, 8));
      var bodyOffset = BinaryPrimitives.ReadUInt16LittleEndian(leaf.AsSpan(header + 32, 2));
      var plugin = BinaryPrimitives.ReadUInt16LittleEndian(leaf.AsSpan(header + 36, 2));
      if (bodyOffset < Reiser4Tree.NodeHeaderBytes || bodyOffset > bodiesEnd) return false;
      items.Add(new NativeItem(
        Minor: (byte)(key0 & 0xF),
        Ordering: BinaryPrimitives.ReadUInt64LittleEndian(leaf.AsSpan(header + 8, 8)),
        ObjectId: BinaryPrimitives.ReadUInt64LittleEndian(leaf.AsSpan(header + 16, 8)),
        KeyOffset: BinaryPrimitives.ReadUInt64LittleEndian(leaf.AsSpan(header + 24, 8)),
        Plugin: plugin,
        BodyOffset: bodyOffset));
    }

    var byBody = items.OrderBy(static item => item.BodyOffset).ToArray();
    var statSizes = new Dictionary<ulong, long>();
    var extents = new Dictionary<ulong, IReadOnlyList<NativeRun>>();
    ReadOnlySpan<byte> directory = default;

    for (var i = 0; i < byBody.Length; ++i) {
      var item = byBody[i];
      var end = i + 1 < byBody.Length ? byBody[i + 1].BodyOffset : bodiesEnd;
      if (end < item.BodyOffset) return false;
      var body = leaf.AsSpan(item.BodyOffset, end - item.BodyOffset);

      switch (item.Plugin) {
        case PluginStat40 when item.Minor == MinorStatData && body.Length >= 16:
          var size = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(8, 8));
          if (size <= long.MaxValue) statSizes[item.ObjectId] = (long)size;
          break;
        case PluginExtent40 when item.Minor == MinorFileBody:
          if ((body.Length & 15) != 0) return false;
          var runs = new List<NativeRun>(body.Length / 16);
          for (var p = 0; p < body.Length; p += 16) {
            var start = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(p, 8));
            var width = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(p + 8, 8));
            if (width != 0) runs.Add(new NativeRun(start, width));
          }
          extents[item.ObjectId] = runs;
          break;
        case PluginCde40:
          directory = body;
          break;
      }
    }

    if (directory.IsEmpty) return false;
    var parsed = ParseNativeDirectory(directory, statSizes, extents);
    if (parsed is null) return false;
    this._entries.AddRange(parsed);
    return true;
  }

  private readonly record struct NativeItem(
    byte Minor, ulong Ordering, ulong ObjectId, ulong KeyOffset, ushort Plugin, ushort BodyOffset);

  private static List<Entry>? ParseNativeDirectory(
      ReadOnlySpan<byte> body,
      IReadOnlyDictionary<ulong, long> statSizes,
      IReadOnlyDictionary<ulong, IReadOnlyList<NativeRun>> extents) {
    if (body.Length < 2) return null;
    var count = BinaryPrimitives.ReadUInt16LittleEndian(body);
    var unitsStart = 2 + count * DirectoryUnitHeaderBytes;
    if (unitsStart > body.Length) return null;

    var result = new List<Entry>();
    for (var i = 0; i < count; ++i) {
      var header = 2 + i * DirectoryUnitHeaderBytes;
      var ordering = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(header, 8));
      var objectIdPart = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(header + 8, 8));
      var offsetPart = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(header + 16, 8));
      var unit = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(header + 24, 2));
      var unitEnd = i + 1 < count
        ? BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(header + DirectoryUnitHeaderBytes + 24, 2))
        : body.Length;
      if (unit < unitsStart || unitEnd < unit || unitEnd > body.Length || unit + TargetKeyBytes > unitEnd)
        return null;

      var targetObjectId = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(unit + 16, 8));
      var name = DecodeName(body.Slice(unit, unitEnd - unit), ordering, objectIdPart, offsetPart);
      if (name.Length == 0 || name is "." or "..") continue;
      if (!statSizes.TryGetValue(targetObjectId, out var size)) continue;
      extents.TryGetValue(targetObjectId, out var runs);
      runs ??= Array.Empty<NativeRun>();
      if (size > 0 && runs.Count == 0) return null;
      result.Add(new Entry(name, runs.Count == 0 ? 0 : runs[0].Start, size) { NativeRuns = runs });
    }
    return result;
  }

  private static string DecodeName(
      ReadOnlySpan<byte> unit, ulong ordering, ulong objectId, ulong offset) {
    if (ordering == 0 && objectId == 0 && offset == 0) return ".";
    if ((ordering & HashedNameBit) != 0) {
      var stored = unit[TargetKeyBytes..];
      var nul = stored.IndexOf((byte)0);
      if (nul >= 0) stored = stored[..nul];
      return Encoding.ASCII.GetString(stored);
    }

    Span<byte> name = stackalloc byte[23];
    var length = 0;
    length += Unpack(name[length..], ordering & 0x00FF_FFFF_FFFF_FFFFUL, 7);
    length += Unpack(name[length..], objectId, 8);
    length += Unpack(name[length..], offset, 8);
    return Encoding.ASCII.GetString(name[..length]);
  }

  private static int Unpack(Span<byte> destination, ulong value, int bytes) {
    var written = 0;
    for (var shift = (bytes - 1) * 8; shift >= 0; shift -= 8) {
      var b = (byte)(value >> shift);
      if (b == 0) break;
      destination[written++] = b;
    }
    return written;
  }

  private void ReadLegacyDirectory(ulong firstBlock) {
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

  /// <summary>Releases resources held by this instance.</summary>
  public void Dispose() => this._image.Dispose();
}
