#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.BcacheFs;

/// <summary>
/// Reads the files a <see cref="BcacheFsWriter" /> placed in the CWB-BCH-WB
/// payload area of a bcachefs image.
/// <para>
/// bcachefs keeps inodes, dirents and extents in b-trees whose keys are
/// varint-packed bkeys; this reader does not walk them. It reads the marker in the
/// reserved sectors ahead of the superblock layout and follows the chained
/// directory the workbench writer left there. An image from real
/// <c>bcachefs format</c> carries no marker and surfaces no entries.
/// </para>
/// </summary>
public sealed class BcacheFsReader : IDisposable {

  private readonly ImageAccessor _image;
  private readonly List<Entry> _entries = [];

  /// <summary>True when the image starts with a bcachefs superblock.</summary>
  public bool Valid { get; }

  /// <summary>Files the payload area holds. Empty for an image without the marker.</summary>
  public IReadOnlyList<Entry> Entries => this._entries;

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._image.Length;

  public BcacheFsReader(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    // Blocks are pulled on demand: the metadata is a handful of blocks however
    // many gigabytes of payload follow it.
    this._image = new ImageAccessor(stream, leaveOpen);

    var minimum = BcacheFsWriter.FirstPayloadBlock * BcacheFsWriter.PayloadBlockSize;
    if (this._image.Length < minimum) return;

    // The superblock magic sits at sector 8, 24 bytes into the struct.
    var primary = this._image.Read(BcacheFsWriter.BchSbSector * 512, 64);
    this.Valid = primary.AsSpan(24, BcacheFsWriter.BcachefsMagic.Length)
      .SequenceEqual(BcacheFsWriter.BcachefsMagic);

    var marker = this._image.Read(BcacheFsWriter.PayloadMarkerOffset, 32);
    if (!marker.AsSpan(0, BcacheFsWriter.PayloadMarker.Length)
        .SequenceEqual(BcacheFsWriter.PayloadMarker))
      return;

    var dirBlock = BinaryPrimitives.ReadInt64LittleEndian(
      marker.AsSpan((int)(BcacheFsWriter.PayloadDirOffset - BcacheFsWriter.PayloadMarkerOffset), 8));
    this.ReadDirectory(dirBlock);
  }

  /// <summary>One file in the payload area: its name, first block and byte length.</summary>
  public sealed record Entry(string Name, long FirstBlock, long Size);

  /// <summary>Reads a file's contents. Only valid below the array limit.</summary>
  public byte[] Extract(Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size > Array.MaxLength)
      throw new IOException(
        $"BcacheFS: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    using var buffer = new MemoryStream();
    this.ExtractTo(entry, buffer);
    return buffer.ToArray();
  }

  /// <summary>
  /// Writes <paramref name="entry" />'s contents into <paramref name="destination" />.
  /// A file's blocks are one contiguous run, so this is a single forward copy.
  /// Returns the number of bytes written.
  /// </summary>
  public long ExtractTo(Entry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.Size <= 0) return 0;

    var offset = entry.FirstBlock * BcacheFsWriter.PayloadBlockSize;
    if (offset < 0 || offset >= this._image.Length) return 0;
    var take = Math.Min(entry.Size, this._image.Length - offset);
    if (take <= 0) return 0;
    this._image.CopyTo(offset, destination, take);
    return take;
  }

  private void ReadDirectory(long firstBlock) {
    var visited = new HashSet<long>();
    var block = firstBlock;
    while (block != 0 && visited.Add(block)) {
      var offset = block * BcacheFsWriter.PayloadBlockSize;
      if (offset < 0 || offset + BcacheFsWriter.PayloadBlockSize > this._image.Length) break;
      var buf = this._image.Read(offset, BcacheFsWriter.PayloadBlockSize);
      if (!buf.AsSpan(0, BcacheFsWriter.DirMagic.Length).SequenceEqual(BcacheFsWriter.DirMagic)) break;

      var next = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8, 8));
      var count = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16, 4));
      for (var i = 0; i < count && i < BcacheFsWriter.DirEntriesPerBlock; ++i) {
        var o = BcacheFsWriter.DirHeadSize + i * BcacheFsWriter.DirEntrySize;
        var name = ReadCString(buf.AsSpan(o, BcacheFsWriter.DirNameLength));
        if (name.Length == 0) continue;
        var first = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(o + BcacheFsWriter.DirNameLength, 8));
        var size = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(o + BcacheFsWriter.DirNameLength + 8, 8));
        if (size < 0) continue;
        this._entries.Add(new Entry(name, first, size));
      }
      block = next;
    }
  }

  private static string ReadCString(ReadOnlySpan<byte> span) {
    var n = span.IndexOf((byte)0);
    if (n < 0) n = span.Length;
    return n == 0 ? "" : Encoding.UTF8.GetString(span[..n]);
  }

  public void Dispose() => this._image.Dispose();
}
