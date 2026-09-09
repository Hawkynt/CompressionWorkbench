#pragma warning disable CS1591
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Tux2;

/// <summary>
/// Opaque reader for the historical TUX2 research filesystem.
/// </summary>
/// <remarks>
/// <para>TUX2 deliberately has no invented private container format here. Daniel Phillips's
/// original announcement described TUX2 as an Ext2 variation whose goals included mounting an
/// existing Ext2 partition as TUX2. No stable, independently identifying TUX2 on-disk signature
/// was published, so an image cannot be authenticated as TUX2 from a made-up magic value.</para>
/// <para>The reader therefore surfaces the selected image verbatim plus diagnostic metadata. It
/// reports the Ext2 superblock magic only as a compatibility clue; that magic is not evidence that
/// the image was ever written by TUX2.</para>
/// </remarks>
public sealed class Tux2Reader : IDisposable {
  private const long Ext2SuperblockMagicOffset = 1024 + 56;
  private const ushort Ext2SuperblockMagic = 0xEF53;

  private readonly ImageAccessor _image;
  private readonly long _length;
  private readonly List<Tux2Entry> _entries = [];

  /// <summary>Gets the entries exposed by this opaque reader.</summary>
  public IReadOnlyList<Tux2Entry> Entries => this._entries;

  /// <summary>Gets the total size of the selected image.</summary>
  public long Length => this._length;

  /// <summary>
  /// Gets whether the image carries the Ext2 family superblock magic at the canonical offset.
  /// This is only a compatibility hint, not a TUX2 identity test.
  /// </summary>
  public bool LooksLikeExt2 { get; private set; }

  /// <summary>Initializes a reader over the selected image.</summary>
  public Tux2Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    this._image = new ImageAccessor(stream);
    this._length = this._image.Length;
    this.Parse();
  }

  private void Parse() {
    this.LooksLikeExt2 = this._length >= Ext2SuperblockMagicOffset + sizeof(ushort)
      && this._image.ReadUInt16(Ext2SuperblockMagicOffset) == Ext2SuperblockMagic;

    this._entries.Add(new Tux2Entry {
      Name = "FULL.tux2",
      Size = this._length,
      Offset = 0,
    });

    var metadata = this.BuildMetadata();
    this._entries.Add(new Tux2Entry {
      Name = "metadata.ini",
      Size = metadata.LongLength,
      Data = metadata,
    });
  }

  private byte[] BuildMetadata() {
    var builder = new StringBuilder();
    builder.Append("parse_status=opaque\n");
    builder.Append("format=TUX2 research prototype\n");
    builder.Append("self_identifying=false\n");
    builder.Append(this.LooksLikeExt2
      ? "ext2_superblock_magic=present\n"
      : "ext2_superblock_magic=absent\n");
    builder.Append("note=TUX2 targeted Ext2 compatibility and no stable standalone TUX2 disk signature/layout was published; this reader does not guess one.\n");
    return Encoding.UTF8.GetBytes(builder.ToString());
  }

  /// <summary>Returns an entry as a byte array when it fits the CLR array limit.</summary>
  public byte[] Extract(Tux2Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Offset < 0) return entry.Data;
    if (entry.Size > Array.MaxLength)
      throw new IOException($"Tux2: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    if (entry.Size == 0) return [];
    return this._image.Read(entry.Offset, checked((int)entry.Size));
  }

  /// <summary>Streams an entry to <paramref name="destination"/>.</summary>
  public long ExtractTo(Tux2Entry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);

    if (entry.Offset < 0) {
      destination.Write(entry.Data);
      return entry.Data.LongLength;
    }

    var count = Math.Min(entry.Size, this._length - entry.Offset);
    if (count <= 0) return 0;
    this._image.CopyTo(entry.Offset, destination, count);
    return count;
  }

  /// <inheritdoc />
  public void Dispose() => this._image.Dispose();
}
