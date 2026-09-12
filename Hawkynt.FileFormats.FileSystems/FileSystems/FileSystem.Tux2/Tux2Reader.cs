#pragma warning disable CS1591
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Tux2;

/// <summary>
/// Reader for Daniel Phillips's historical TUX2 research filesystem.
/// </summary>
/// <remarks>
/// <para>TUX2 deliberately has no invented private container format here. The original
/// announcement described TUX2 as an Ext2 variation and explicitly listed mounting an existing
/// Ext2 partition as a design goal. No stable, independently identifying TUX2 on-disk signature
/// was published, so an image cannot be authenticated as TUX2 from a made-up magic value.</para>
/// <para>The reader therefore keeps an opaque forensic surface for unknown images. It additionally
/// recognises the conservative Ext2 feature profile CompressionWorkbench can safely operate on;
/// that profile is an interoperability path, not evidence that an image was ever written by TUX2.</para>
/// </remarks>
public sealed class Tux2Reader : IDisposable {
  private const long Ext2SuperblockOffset = 1024;
  private const long Ext2SuperblockMagicOffset = Ext2SuperblockOffset + 56;
  private const ushort Ext2SuperblockMagic = 0xEF53;

  // Feature set accepted by the Ext2 compatibility path. These are the classic pointer-based
  // features emitted/understood by FileSystem.Ext; journaling, extents, 64-bit descriptors,
  // metadata checksums and other later incompatible layouts deliberately stay outside the gate.
  private const uint CompatExtAttr = 0x0008;
  private const uint CompatResizeInode = 0x0010;
  private const uint CompatDirIndex = 0x0020;
  private const uint AllowedCompat = CompatExtAttr | CompatResizeInode | CompatDirIndex;
  private const uint IncompatFileType = 0x0002;
  private const uint AllowedIncompat = IncompatFileType;
  private const uint RoCompatSparseSuper = 0x0001;
  private const uint RoCompatLargeFile = 0x0002;
  private const uint RoCompatHugeFile = 0x0008;
  private const uint RoCompatDirNlink = 0x0020;
  private const uint RoCompatExtraIsize = 0x0040;
  private const uint AllowedRoCompat =
    RoCompatSparseSuper | RoCompatLargeFile | RoCompatHugeFile | RoCompatDirNlink | RoCompatExtraIsize;

  private readonly ImageAccessor _image;
  private readonly long _length;
  private readonly List<Tux2Entry> _entries = [];

  /// <summary>Gets the entries exposed by the opaque fallback reader.</summary>
  public IReadOnlyList<Tux2Entry> Entries => this._entries;

  /// <summary>Gets the total size of the selected image.</summary>
  public long Length => this._length;

  /// <summary>
  /// Gets whether the image carries the Ext2 family superblock magic at the canonical offset.
  /// This is only a compatibility hint, not a TUX2 identity test.
  /// </summary>
  public bool LooksLikeExt2 { get; private set; }

  /// <summary>
  /// Gets whether the superblock is a structurally plausible pointer-based Ext2 profile that the
  /// TUX2 descriptor may hand to CompressionWorkbench's Ext2 implementation. This is deliberately
  /// stricter than <see cref="LooksLikeExt2"/> and deliberately says nothing about TUX2 provenance.
  /// </summary>
  public bool IsSupportedExt2CompatibilityProfile { get; private set; }

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
    this.IsSupportedExt2CompatibilityProfile = this.LooksLikeExt2 && this.HasSupportedExt2Superblock();

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

  private bool HasSupportedExt2Superblock() {
    // Through s_feature_ro_compat (+100, four bytes).
    if (this._length < Ext2SuperblockOffset + 104) return false;

    var inodesCount = this._image.ReadUInt32(Ext2SuperblockOffset);
    var blocksCount = this._image.ReadUInt32(Ext2SuperblockOffset + 4);
    var firstDataBlock = this._image.ReadUInt32(Ext2SuperblockOffset + 20);
    var logBlockSize = this._image.ReadUInt32(Ext2SuperblockOffset + 24);
    var blocksPerGroup = this._image.ReadUInt32(Ext2SuperblockOffset + 32);
    var inodesPerGroup = this._image.ReadUInt32(Ext2SuperblockOffset + 40);
    var revision = this._image.ReadUInt32(Ext2SuperblockOffset + 76);
    var inodeSize = revision == 0 ? 128 : this._image.ReadUInt16(Ext2SuperblockOffset + 88);
    var featureCompat = this._image.ReadUInt32(Ext2SuperblockOffset + 92);
    var featureIncompat = this._image.ReadUInt32(Ext2SuperblockOffset + 96);
    var featureRoCompat = this._image.ReadUInt32(Ext2SuperblockOffset + 100);

    if (inodesCount == 0 || blocksCount == 0 || blocksPerGroup == 0 || inodesPerGroup == 0)
      return false;
    if (revision > 1 || logBlockSize > 2)
      return false;

    var blockSize = 1024 << (int)logBlockSize;
    if (inodeSize is not (128 or 256) || inodeSize > blockSize)
      return false;
    if (firstDataBlock != (blockSize == 1024 ? 1u : 0u))
      return false;

    // Reject truncated volumes without multiplying attacker-controlled values.
    if (blocksCount > (ulong)this._length / (uint)blockSize)
      return false;

    if ((featureCompat & ~AllowedCompat) != 0)
      return false;
    if ((featureIncompat & ~AllowedIncompat) != 0)
      return false;
    if ((featureRoCompat & ~AllowedRoCompat) != 0)
      return false;

    return true;
  }

  private byte[] BuildMetadata() {
    var builder = new StringBuilder();
    builder.Append("parse_status=opaque\n");
    builder.Append("format=TUX2 research prototype\n");
    builder.Append("self_identifying=false\n");
    builder.Append(this.LooksLikeExt2
      ? "ext2_superblock_magic=present\n"
      : "ext2_superblock_magic=absent\n");
    builder.Append(this.IsSupportedExt2CompatibilityProfile
      ? "ext2_compatibility_profile=supported\n"
      : "ext2_compatibility_profile=unsupported\n");
    builder.Append("note=TUX2 targeted Ext2 compatibility and no stable standalone TUX2 disk signature/layout was published; unknown images remain opaque.\n");
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
