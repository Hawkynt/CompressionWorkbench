#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.BcacheFs;

/// <summary>
/// Parsed BcacheFS superblock — the on-disk header that lives at byte offset
/// 4096 of a BcacheFS device. Field layout follows the kernel's
/// <c>struct bch_sb</c> as defined in <c>fs/bcachefs/bcachefs_format.h</c>.
/// </summary>
/// <remarks>
/// <para>Spec-accurate field offsets:</para>
/// <list type="bullet">
///   <item>0..16  csum (struct bch_csum: __le64 lo + __le64 hi)</item>
///   <item>16..18 version (__le16, encoded as <c>(major &lt;&lt; 10) | minor</c>)</item>
///   <item>18..20 version_min (__le16)</item>
///   <item>20..24 pad[2]</item>
///   <item>24..40 magic (__uuid_t = BCHFS_MAGIC c68573f6-66ce-90a9-d96a-60cf-803d-f7ef)</item>
///   <item>40..56 uuid (__uuid_t — internal device UUID)</item>
///   <item>56..72 user_uuid (__uuid_t — user-visible)</item>
///   <item>72..104 label[32]</item>
///   <item>104..112 offset (__le64 — sector this SB was written at)</item>
///   <item>112..120 seq (__le64 — write sequence)</item>
///   <item>120..122 block_size (__le16, sectors)</item>
///   <item>122 dev_idx (u8 — this device's index in the members array)</item>
///   <item>123 nr_devices (u8)</item>
///   <item>124..128 u64s (__le32 — variable area length in u64 cells)</item>
///   <item>128..136 time_base_lo, 136..140 time_base_hi, 140..144 time_precision</item>
///   <item>144..200 flags[7], 200..208 write_time, 208..224 features[2], 224..240 compat[2]</item>
///   <item>240..752 layout (struct bch_sb_layout)</item>
/// </list>
/// </remarks>
internal sealed class BcacheFsSuperblock {
  /// <summary>BCHFS_MAGIC — c68573f6-66ce-90a9-d96a-60cf-803d-f7ef in storage byte order.</summary>
  public static readonly byte[] MagicUuid = [
    0xC6, 0x85, 0x73, 0xF6,
    0x66, 0xCE,
    0x90, 0xA9,
    0xD9, 0x6A,
    0x60, 0xCF, 0x80, 0x3D, 0xF7, 0xEF,
  ];

  // The superblock starts at byte 4096; the magic UUID lives 24 bytes into
  // that struct (after csum + version + version_min + 4 bytes of pad), so the
  // file-relative offset is 4096 + 24 = 4120.
  public const int SuperblockOffset = 4096;
  public const int MagicOffset = SuperblockOffset + 24;

  public bool Valid { get; init; }
  public ushort VersionMin { get; init; }
  public ushort Version { get; init; }
  public Guid Uuid { get; init; }
  public Guid UserUuid { get; init; }
  public string Label { get; init; } = "";
  public ulong Offset { get; init; }
  public ulong Seq { get; init; }
  public ushort BlockSize { get; init; }
  public byte DevIdx { get; init; }
  public byte NrDevices { get; init; }
  public uint U64s { get; init; }
  public byte[] RawBytes { get; init; } = [];

  public static BcacheFsSuperblock TryParse(ReadOnlySpan<byte> image) {
    if (image.Length < MagicOffset + 16) return new BcacheFsSuperblock();
    var magicSlot = image.Slice(MagicOffset, 16);
    if (!magicSlot.SequenceEqual(MagicUuid)) return new BcacheFsSuperblock();

    // We snapshot the first 1024 bytes of the superblock — enough to cover all
    // fixed-offset fields up through the layout struct (ends at +752) plus
    // the start of any variable-length field section.
    var sbStart = SuperblockOffset;
    var rawLen = Math.Min(1024, image.Length - sbStart);
    var raw = image.Slice(sbStart, rawLen).ToArray();

    var sb = image.Slice(SuperblockOffset);
    if (sb.Length < 128) return new BcacheFsSuperblock();

    // csum @ 0..16 — skipped; we don't validate
    var version = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(16, 2));
    var versionMin = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(18, 2));
    // pad[2] @ 20..24 — skipped
    // magic UUID @ 24..40 — already verified
    var uuid = ReadGuid(sb.Slice(40, 16));
    var userUuid = ReadGuid(sb.Slice(56, 16));
    var labelBytes = sb.Slice(72, 32);
    var labelEnd = labelBytes.IndexOf((byte)0);
    if (labelEnd < 0) labelEnd = labelBytes.Length;
    var label = Encoding.UTF8.GetString(labelBytes.Slice(0, labelEnd));
    var offset = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(104, 8));
    var seq = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(112, 8));
    var blockSize = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(120, 2));
    var devIdx = sb[122];
    var nrDevices = sb[123];
    var u64s = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(124, 4));

    return new BcacheFsSuperblock {
      Valid = true,
      Version = version,
      VersionMin = versionMin,
      Uuid = uuid,
      UserUuid = userUuid,
      Label = label,
      Offset = offset,
      Seq = seq,
      BlockSize = blockSize,
      DevIdx = devIdx,
      NrDevices = nrDevices,
      U64s = u64s,
      RawBytes = raw,
    };
  }

  /// <summary>Reads a UUID stored in RFC-4122 mixed-endian form (matching the kernel struct uuid_t).</summary>
  private static Guid ReadGuid(ReadOnlySpan<byte> bytes) {
    if (bytes.Length < 16) return Guid.Empty;
    var arr = new byte[16];
    bytes.Slice(0, 16).CopyTo(arr);
    try { return new Guid(arr); } catch { return Guid.Empty; }
  }

  public string FormatVersion() {
    // BCH_VERSION(major, minor) = (major << 10) | minor.
    var major = (uint)(Version >> 10);
    var minor = (uint)(Version & 0x3FF);
    return string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}");
  }
}
