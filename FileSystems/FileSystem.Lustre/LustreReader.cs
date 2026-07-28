#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FileSystem.Ext;

namespace FileSystem.Lustre;

/// <summary>
/// R/O reader for Lustre MDT/OST images via ldiskfs (ext4-compatible) delegation.
///
/// Lustre is a high-performance distributed parallel filesystem (originally from
/// CMU, now under OpenSFS). Files are striped across many OST (Object Storage
/// Target) servers and the MDS (MetaData Server) holds the namespace.
///
/// Lustre's on-disk format for MDT/OST backing stores is <c>ldiskfs</c>, a fork
/// of ext4 with Lustre-specific extended attributes (LMA, LOV EA striping, FID
/// pointers) and a few feature flags. The block-level format — superblock, group
/// descriptors, inode table, extent trees, directory blocks — is byte-compatible
/// with ext4 for read purposes. We delegate to <see cref="ExtReader"/> for the
/// file walk and surface the ldiskfs view (the raw inode/directory tree of one
/// MDT or OST), not the Lustre logical view (which requires combining MDT inode
/// metadata with object data striped across multiple OSTs — out-of-scope without
/// live cluster metadata).
///
/// Detection paths:
///   1. Legacy "LUSTRE" / "LUst" tag at offset 0 — speculative OST object-header
///      dumps; surfaces metadata.ini + the raw object bytes (Stage-0 behaviour
///      preserved for back-compat).
///   2. ext4 superblock magic 0xEF53 at offset 1080 — real ldiskfs MDT/OST
///      backing-store image. Surfaces metadata.ini + the ldiskfs file walk via
///      <see cref="ExtReader"/>.
/// </summary>
public sealed class LustreReader : IDisposable {

  /// <summary>Lustre long tag: ASCII "LUSTRE" (6 bytes).</summary>
  public static readonly byte[] LongTag = "LUSTRE"u8.ToArray();
  /// <summary>Lustre short tag: bytes 0x4C 0x55 0x73 0x74 (= 0x4C557374 BE).</summary>
  public static readonly byte[] ShortTag = [0x4C, 0x55, 0x73, 0x74];

  // ext4 / ldiskfs constants.
  private const int Ext4SuperblockOffset = 1024;
  private const int Ext4MagicFieldOffset = 56; // within superblock
  private const ushort Ext4Magic = 0xEF53;

  private readonly byte[] _data;
  private readonly List<LustreEntry> _entries = [];
  private ExtReader? _extReader;

  /// <summary>
  /// Backing stream for <see cref="_extReader" />. ExtReader reads the image on
  /// demand rather than copying it, so the stream has to outlive it.
  /// </summary>
  private MemoryStream? _extStream;

  public IReadOnlyList<LustreEntry> Entries => _entries;
  public string Tag { get; private set; } = "";
  public uint TrailingWord { get; private set; }
  public bool ValidHeader { get; private set; }

  /// <summary>True if the input was identified as an ldiskfs (ext4) image and delegated to <see cref="ExtReader"/>.</summary>
  public bool IsLdiskfs { get; private set; }

  /// <summary>Volume label parsed from the ldiskfs superblock (offset 1024+120, 16 bytes), or empty if not ldiskfs / unset.</summary>
  public string LdiskfsVolumeLabel { get; private set; } = "";

  public LustreReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 8)
      throw new InvalidDataException("Lustre: file too small for OST/MDS object header.");

    // Path 1: legacy "LUSTRE" / "LUst" tag at offset 0 — Stage-0 dump.
    if (_data.AsSpan(0, 6).SequenceEqual(LongTag)) {
      this.Tag = "LUSTRE";
      this.TrailingWord = _data.Length >= 12
        ? BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(8, 4)) : 0;
      ParseLegacyDump();
      return;
    }

    if (_data.AsSpan(0, 4).SequenceEqual(ShortTag)) {
      this.Tag = "LUst";
      this.TrailingWord = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(4, 4));
      ParseLegacyDump();
      return;
    }

    // Path 2: ldiskfs (ext4) MDT/OST backing-store image.
    if (TryParseLdiskfs())
      return;

    throw new InvalidDataException(
      "Lustre: image is neither a 'LUSTRE'/'LUst' object-header dump nor an ldiskfs (ext4) MDT/OST backing store.");
  }

  private void ParseLegacyDump() {
    this.ValidHeader = true;
    var meta = BuildLegacyMetadata();
    _entries.Add(new LustreEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Offset = 0, Data = meta });
    _entries.Add(new LustreEntry { Name = "lustre-object.bin", Size = _data.Length, IsDirectory = false, Offset = 0, Data = _data });
  }

  private bool TryParseLdiskfs() {
    if (_data.Length < Ext4SuperblockOffset + 264) return false;

    var sb = _data.AsSpan(Ext4SuperblockOffset);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(Ext4MagicFieldOffset));
    if (magic != Ext4Magic) return false;

    // Volume label lives at superblock offset 120, 16 bytes, NUL-padded.
    var labelBytes = sb.Slice(120, 16).ToArray();
    var labelLen = 0;
    while (labelLen < labelBytes.Length && labelBytes[labelLen] != 0) labelLen++;
    this.LdiskfsVolumeLabel = Encoding.ASCII.GetString(labelBytes, 0, labelLen);

    this.IsLdiskfs = true;
    this.ValidHeader = true;
    this.Tag = "ldiskfs";

    // Delegate the file walk to ExtReader. The stream is kept open for the
    // reader's lifetime and disposed alongside it.
    _extStream = new MemoryStream(_data, writable: false);
    _extReader = new ExtReader(_extStream);

    // Synthetic metadata first.
    var meta = BuildLdiskfsMetadata();
    _entries.Add(new LustreEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Offset = 0, Data = meta });

    // Surface the raw ldiskfs image so callers that want the full bytes (forensic /
    // re-mount path) can still get them — same shape as Stage-0 lustre-object.bin.
    _entries.Add(new LustreEntry { Name = "lustre-object.bin", Size = _data.Length, IsDirectory = false, Offset = 0, Data = _data });

    // Then every file the ExtReader surfaces, prefixed so the ldiskfs view is
    // distinguishable from the synthetic metadata files.
    foreach (var e in _extReader.Entries) {
      // Read the file bytes eagerly via ExtReader.Extract — same pattern as the
      // Stage-0 reader. Lustre images are typically small enough that this is fine;
      // production-scale MDT images would need lazy extraction (out-of-scope here).
      var data = e.IsDirectory ? [] : _extReader.Extract(e);
      _entries.Add(new LustreEntry {
        Name = "ldiskfs/" + e.Name,
        Size = e.Size,
        IsDirectory = e.IsDirectory,
        Offset = 0,
        Data = data,
      });
    }

    return true;
  }

  private byte[] BuildLegacyMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=detection-only\n");
    bldr.Append("format=Lustre OST/MDS object\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic_tag={this.Tag}\n");
    bldr.Append("magic_offset=0\n");
    bldr.Append(CultureInfo.InvariantCulture, $"trailing_word=0x{this.TrailingWord:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");
    bldr.Append("note=Stage 0 path - 'LUSTRE'/'LUst' object-header dump. Lustre is a distributed parallel FS; ");
    bldr.Append("striping (LOV EA) + MDT inode resolution require live cluster metadata.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  private byte[] BuildLdiskfsMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=partial-ldiskfs\n");
    bldr.Append("format=Lustre MDT/OST (ldiskfs backing store)\n");
    bldr.Append("backing_fs=ldiskfs (ext4-compatible)\n");
    bldr.Append("magic_offset=1080\n");
    bldr.Append("magic_bytes=0xEF53\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"ldiskfs_volume_label={this.LdiskfsVolumeLabel}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"ldiskfs_entry_count={(_extReader?.Entries.Count ?? 0)}\n");
    bldr.Append("note=R/O via ext4 reader delegation. Surfaces the ldiskfs view of a single MDT or OST ");
    bldr.Append("backing store, NOT the Lustre logical view. Lustre xattrs (LMA, LOV EA striping, FID) ");
    bldr.Append("are preserved in the raw image but not interpreted. Combining MDT inode metadata with ");
    bldr.Append("file data striped across multiple OSTs requires live cluster metadata and is out-of-scope.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  public byte[] Extract(LustreEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  public void Dispose() {
    _extReader?.Dispose();
    _extStream?.Dispose();
    _extStream = null;
  }
}
