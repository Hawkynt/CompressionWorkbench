#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Tux3;

/// <summary>
/// Native-superblock reader for the linux-tux3 research filesystem.
/// </summary>
/// <remarks>
/// <para>The on-disk structure is taken from the canonical linux-tux3 <c>struct disksuper</c>:
/// it starts at byte 4096, is packed, and all integer fields are big-endian.</para>
/// <para>This reader intentionally stops at the superblock. It does not interpret the inode,
/// orphan, allocation, atom or directory trees and therefore exposes no invented file table.
/// The old private <c>TUX3SUPR</c>/<c>TUX3WORM</c> dialect is not accepted.</para>
/// </remarks>
public sealed class Tux3Reader : IDisposable {
  /// <summary>Current linux-tux3 disk-format magic (2014-05-06 revision).</summary>
  public static readonly byte[] Magic = [0x74, 0x75, 0x78, 0x33, 0x20, 0x14, 0x05, 0x06];

  /// <summary>Older 2012-12-20 userspace-tree disk-format magic.</summary>
  public static readonly byte[] Legacy2012Magic = [0x74, 0x75, 0x78, 0x33, 0x20, 0x12, 0x12, 0x20];

  /// <summary>Fixed byte offset of <c>struct disksuper</c>.</summary>
  public const int SuperblockOffset = 1 << 12;

  /// <summary>Size in bytes of the packed current <c>struct disksuper</c>.</summary>
  public const int DiskSuperSize = 0x64;

  private readonly ImageAccessor _image;
  private readonly long _length;
  private readonly List<Tux3Entry> _entries = [];

  /// <summary>Gets the entries exposed by this metadata-only reader.</summary>
  public IReadOnlyList<Tux3Entry> Entries => this._entries;

  /// <summary>Gets the total image size.</summary>
  public long Length => this._length;

  /// <summary>Gets whether a supported native superblock was parsed.</summary>
  public bool ValidSuperblock { get; private set; }

  /// <summary>Gets the disk-format revision identified by the eight-byte magic.</summary>
  public string Revision { get; private set; } = "";

  public ulong Birthday { get; private set; }
  public ulong Flags { get; private set; }
  public ushort BlockBits { get; private set; }
  public ulong VolBlocks { get; private set; }
  public ulong IRoot { get; private set; }
  public ulong ORoot { get; private set; }
  public ulong UsedInodes { get; private set; }
  public ulong NextBlock { get; private set; }
  public ulong AtomDictionarySize { get; private set; }
  public uint FreeAtom { get; private set; }
  public uint AtomGeneration { get; private set; }
  public ulong LogChain { get; private set; }
  public uint LogCount { get; private set; }

  /// <summary>Initializes a reader over a TUX3 image.</summary>
  public Tux3Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    this._image = new ImageAccessor(stream);
    this._length = this._image.Length;
    this.Parse();
  }

  private void Parse() {
    if (this._length < SuperblockOffset + DiskSuperSize)
      throw new InvalidDataException("Tux3: image too small for the native disksuper at byte 4096.");

    var super = this._image.Read(SuperblockOffset, DiskSuperSize);
    var span = super.AsSpan();
    var magic = span[..8];

    if (magic.SequenceEqual(Magic))
      this.Revision = "2014-05-06";
    else if (magic.SequenceEqual(Legacy2012Magic))
      this.Revision = "2012-12-20";
    else
      throw new InvalidDataException("Tux3: unsupported or missing native disk-format magic at byte 4096.");

    this.Birthday = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(0x08, 8));
    this.Flags = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(0x10, 8));
    this.BlockBits = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(0x18, 2));
    this.VolBlocks = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(0x20, 8));
    this.IRoot = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(0x28, 8));
    this.ORoot = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(0x30, 8));
    this.UsedInodes = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(0x38, 8));
    this.NextBlock = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(0x40, 8));
    this.AtomDictionarySize = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(0x48, 8));
    this.FreeAtom = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0x50, 4));
    this.AtomGeneration = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0x54, 4));
    this.LogChain = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(0x58, 8));
    this.LogCount = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0x60, 4));
    this.ValidSuperblock = true;

    this._entries.Add(new Tux3Entry { Name = "FULL.tux3", Size = this._length, Offset = 0 });

    var metadata = this.BuildMetadata();
    this._entries.Add(new Tux3Entry { Name = "metadata.ini", Size = metadata.LongLength, Data = metadata });
    this._entries.Add(new Tux3Entry { Name = "superblock.bin", Size = super.LongLength, Data = super });
  }

  private byte[] BuildMetadata() {
    var builder = new StringBuilder();
    builder.Append("parse_status=superblock-only\n");
    builder.Append("format=TUX3 (linux-tux3 prototype)\n");
    builder.Append(CultureInfo.InvariantCulture, $"revision={this.Revision}\n");
    builder.Append(CultureInfo.InvariantCulture, $"superblock_offset={SuperblockOffset}\n");
    builder.Append(CultureInfo.InvariantCulture, $"birthday=0x{this.Birthday:X16}\n");
    builder.Append(CultureInfo.InvariantCulture, $"flags=0x{this.Flags:X16}\n");
    builder.Append(CultureInfo.InvariantCulture, $"blockbits={this.BlockBits}\n");
    if (this.BlockBits < 63)
      builder.Append(CultureInfo.InvariantCulture, $"block_size={1UL << this.BlockBits}\n");
    builder.Append(CultureInfo.InvariantCulture, $"volblocks={this.VolBlocks}\n");
    builder.Append(CultureInfo.InvariantCulture, $"iroot=0x{this.IRoot:X16}\n");
    builder.Append(CultureInfo.InvariantCulture, $"oroot=0x{this.ORoot:X16}\n");
    builder.Append(CultureInfo.InvariantCulture, $"usedinodes={this.UsedInodes}\n");
    builder.Append(CultureInfo.InvariantCulture, $"nextblock={this.NextBlock}\n");
    builder.Append(CultureInfo.InvariantCulture, $"atomdictsize={this.AtomDictionarySize}\n");
    builder.Append(CultureInfo.InvariantCulture, $"freeatom={this.FreeAtom}\n");
    builder.Append(CultureInfo.InvariantCulture, $"atomgen={this.AtomGeneration}\n");
    builder.Append(CultureInfo.InvariantCulture, $"logchain=0x{this.LogChain:X16}\n");
    builder.Append(CultureInfo.InvariantCulture, $"logcount={this.LogCount}\n");
    builder.Append("note=Native itable/otable/allocation/directory traversal is not implemented; no private file table is assumed.\n");
    return Encoding.UTF8.GetBytes(builder.ToString());
  }

  /// <summary>Returns an entry as a byte array when it fits the CLR array limit.</summary>
  public byte[] Extract(Tux3Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Offset < 0) return entry.Data;
    if (entry.Size > Array.MaxLength)
      throw new IOException($"Tux3: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    if (entry.Size == 0) return [];
    return this._image.Read(entry.Offset, checked((int)entry.Size));
  }

  /// <summary>Streams an entry to <paramref name="destination"/>.</summary>
  public long ExtractTo(Tux3Entry entry, Stream destination) {
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
