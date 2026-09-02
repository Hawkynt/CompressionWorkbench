#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.OneFs;

/// <summary>
/// Stage 0 detection-only reader for Dell EMC Isilon OneFS LIN-tree root
/// images. OneFS is a clustered scale-out NAS — its single-image surface
/// is the LIN-tree root block, whose first bytes are the ASCII tag
/// <c>"OneFS"</c> (5 bytes, 0x4F 0x6E 0x65 0x46 0x53) or the short
/// <c>"ONEF"</c> tag (0x4F 0x4E 0x45 0x46 = 0x4F4E4546 BE int) used in
/// some node-local boot images.
///
/// <para>
/// Only the tag is verified; the real LIN tree (logical inode number tree)
/// is a cluster-wide construct and cannot be walked from a single image.
/// File data is FEC-striped across nodes (N+M:B protection groups) — even
/// a complete single-drive image carries only one stripe and cannot
/// reconstruct file content without peer nodes.
/// </para>
/// <para>
/// OneFS shares OS ancestry with FreeBSD, but the on-disk filesystem layer
/// is proprietary and NOT UFS-compatible: there is no UFS1 superblock magic
/// (<c>0x00011954</c>) at the UFS1 superblock offset (8192). The OneFS
/// on-disk format has never been publicly specified by Dell EMC.
/// </para>
/// </summary>
public sealed class OneFsReader : IDisposable {

  /// <summary>OneFS LIN-tree-root long tag: ASCII "OneFS" (5 bytes).</summary>
  public static readonly byte[] LongTag = "OneFS"u8.ToArray();
  /// <summary>OneFS short tag: ASCII "ONEF" (4 bytes, 0x4F4E4546 BE).</summary>
  public static readonly byte[] ShortTag = "ONEF"u8.ToArray();

  private readonly byte[] _data;
  private readonly List<OneFsEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<OneFsEntry> Entries => _entries;
  /// <summary>
  /// Gets or sets the tag.
  /// </summary>
  public string Tag { get; private set; } = "";
  /// <summary>
  /// Gets or sets the trailing word.
  /// </summary>
  public uint TrailingWord { get; private set; }
  /// <summary>
  /// Gets a value indicating whether valid header.
  /// </summary>
  public bool ValidHeader { get; private set; }

  /// <summary>
  /// Initializes a new instance of <see cref="OneFsReader"/>.
  /// </summary>
  public OneFsReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 8)
      throw new InvalidDataException("OneFS: file too small for LIN-tree-root header.");

    if (_data.AsSpan(0, 5).SequenceEqual(LongTag)) {
      this.Tag = "OneFS";
      this.TrailingWord = _data.Length >= 12
        ? BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(8, 4)) : 0;
    } else if (_data.AsSpan(0, 4).SequenceEqual(ShortTag)) {
      this.Tag = "ONEF";
      this.TrailingWord = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(4, 4));
    } else {
      throw new InvalidDataException("OneFS: missing 'OneFS' / 'ONEF' tag at offset 0.");
    }

    this.ValidHeader = true;

    var meta = BuildMetadata();
    _entries.Add(new OneFsEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Offset = 0, Data = meta });
    _entries.Add(new OneFsEntry { Name = "onefs-volume.bin", Size = _data.Length, IsDirectory = false, Offset = 0, Data = _data });
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=detection-only\n");
    bldr.Append("stage=0\n");
    bldr.Append("format=Dell EMC Isilon OneFS LIN-tree root\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic_tag={this.Tag}\n");
    bldr.Append("magic_offset=0\n");
    bldr.Append(CultureInfo.InvariantCulture, $"trailing_word=0x{this.TrailingWord:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");
    bldr.Append("ro_promotion=blocked\n");
    bldr.Append("ro_promotion_reason_1=FEC-striped across nodes (N+M:B protection groups); single-image carries one stripe only\n");
    bldr.Append("ro_promotion_reason_2=LIN tree is cluster-wide, no per-image inode-to-block mapping\n");
    bldr.Append("ro_promotion_reason_3=on-disk format is proprietary; Dell EMC has not published a spec\n");
    bldr.Append("ro_promotion_reason_4=FreeBSD-kernel ancestry does NOT imply UFS on-disk compatibility (no UFS1 superblock at offset 8192)\n");
    bldr.Append("note=Stage 0 — detection only. OneFS is a clustered scale-out NAS; ");
    bldr.Append("LIN tree is cluster-wide, no single-image content surface.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(OneFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
