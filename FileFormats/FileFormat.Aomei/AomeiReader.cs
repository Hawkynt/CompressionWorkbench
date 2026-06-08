#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Aomei;

/// <summary>
/// Header-only reader for AOMEI Backupper image files (<c>.adi</c> disk /
/// partition / system image and <c>.afi</c> file backup). Both image kinds
/// share the same 5-byte ASCII signature <c>BIFH\</c> ("Backup Image File
/// Header") at offset 0. AOMEI is produced by Chengdu Aomei Technology Co.
/// (傲梅科技, Chengdu, China) and the format is fully proprietary; the kernel-
/// mode write/mount/backup drivers (<c>ambakdrv.sys</c>, <c>amwrtdrv.sys</c>,
/// <c>ammntdrv.sys</c>) implement the on-disk layout but are closed source.
///
/// <para>
/// Research scope (June 2026): deep search across English- and Chinese-
/// language reverse-engineering communities (Google, GitHub, 010 Editor
/// template repos, libyal, Joachim Metz forensic specs, 52pojie.cn,
/// kanxue.com, freebuf.com, bilibili) produced <b>no</b> public chunk-layout
/// documentation past the 5-byte ASCII header. AOMEI publishes no SDK and
/// no on-disk format spec; the only documented access path is via AOMEI's
/// own "Explore Image" or restore-to-VMDK workflow.
/// </para>
///
/// <para>
/// What is publicly known from product behaviour (not enough to parse the
/// payload):
/// <list type="bullet">
///   <item><description>5-byte ASCII signature <c>BIFH\</c> at offset 0 — same
///         for <c>.adi</c> (disk/partition/system backup) and <c>.afi</c>
///         (file/folder backup); the trailing backslash is part of the
///         signature, not a path delimiter.</description></item>
///   <item><description>Optional compression (user-selectable None / Normal
///         / High) — algorithm is undocumented; not LZMA/Zstd/DEFLATE
///         framing (no recognizable sub-stream magic).</description></item>
///   <item><description>Optional password-based encryption — algorithm and
///         key derivation are undocumented.</description></item>
///   <item><description>Optional splitting at a user-chosen size
///         (minimum 50 MB, FAT32-aware default ~4 GB) — split parts use
///         numbered companion files; cross-part framing is undocumented.</description></item>
///   <item><description>Incremental and differential chain support — chain
///         linkage fields are undocumented.</description></item>
/// </list>
/// </para>
///
/// <para>
/// Consequently this reader exposes only:
/// <list type="bullet">
///   <item><description><c>parse_status</c> — <c>ok</c> when the 5-byte
///         magic matches, <c>partial</c> otherwise.</description></item>
///   <item><description><c>HeaderRaw</c> — a 64-byte capture of the file
///         start, surfaced as <c>header.bin</c> for forensic inspection.</description></item>
/// </list>
/// No chunk index, no payload extraction, no defragmentation — those
/// require a real format spec or a clean-room RE effort that does not
/// currently exist in the public domain.
/// </para>
/// </summary>
public sealed class AomeiReader {

  /// <summary>5-byte ASCII signature shared by <c>.adi</c> and <c>.afi</c>.
  /// Bytes <c>0x42 0x49 0x46 0x48 0x5C</c> = <c>'B','I','F','H','\\'</c>.</summary>
  public static readonly byte[] Magic = [0x42, 0x49, 0x46, 0x48, 0x5C];

  /// <summary>Capture size of the leading bytes surfaced as <c>header.bin</c>.
  /// 64 bytes covers the magic and a short stretch of post-magic bytes
  /// (typically a version field plus a flag word) that may aid future
  /// reverse engineering without leaking user data.</summary>
  public const int HeaderCaptureSize = 64;

  /// <summary>True once the 5-byte magic has been verified.</summary>
  public bool Valid { get; private set; }

  /// <summary><c>ok</c> on magic match, <c>partial</c> on short read or
  /// magic mismatch.</summary>
  public string ParseStatus { get; private set; } = "unparsed";

  /// <summary>Captured leading bytes (up to <see cref="HeaderCaptureSize"/>).</summary>
  public byte[] HeaderRaw { get; private set; } = [];

  /// <summary>
  /// Speculative 32-bit little-endian word at offset 5 (immediately after
  /// the magic). Surfaced as a diagnostic only — its meaning is <b>not</b>
  /// documented anywhere public. AOMEI may use it as a version, a flags
  /// field, or something else entirely.
  /// </summary>
  public uint PostMagicWord { get; private set; }

  public AomeiReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var image = ReadAllBounded(stream);
    Parse(image);
  }

  private void Parse(ReadOnlySpan<byte> image) {
    if (image.Length < Magic.Length) {
      this.ParseStatus = "partial";
      return;
    }

    if (!image[..Magic.Length].SequenceEqual(Magic)) {
      this.ParseStatus = "partial";
      return;
    }

    var captureLen = Math.Min(HeaderCaptureSize, image.Length);
    var raw = new byte[HeaderCaptureSize];
    image[..captureLen].CopyTo(raw);
    this.HeaderRaw = raw;

    if (image.Length >= Magic.Length + 4)
      this.PostMagicWord = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(Magic.Length, 4));

    this.Valid = true;
    this.ParseStatus = "ok";
  }

  // 64 KB cap — header is at offset 0 and bounded, so a speculative carver
  // scan cannot pull a multi-GB image into memory.
  private const int HeaderReadCap = 64 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}
