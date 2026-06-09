#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileSystem.ProDos;

namespace FileSystem.GsOs;

/// <summary>
/// Writer for Apple IIgs GS/OS volumes packaged in the 2IMG container.
///
/// <para><b>What we emit.</b> A canonical 64-byte 2IMG header followed by a
/// ProDOS block-ordered volume produced by <see cref="ProDosWriter"/>. GS/OS
/// is fundamentally ProDOS-derived (ProDOS 16 / GS/OS uses the same on-disk
/// structures as ProDOS 8 — version-byte 5+ in the directory header marks
/// the volume as GS/OS-aware); wrapping a ProDOS volume in a 2IMG container
/// with creator code <c>"CmpW"</c> produces an image that GS/OS, ProDOS 8 and
/// every emulator that consumes 2IMG (Catakig, Bernie ][ The Rescue, KEGS,
/// GSplus) can mount.</para>
///
/// <para><b>2IMG header layout (little-endian, 64 bytes).</b> Defined in the
/// Apple IIgs Hardware Reference and the universal 2IMG spec at
/// <c>apple2.org.za/gswv/a2zine/Docs/DiskImage_2MG_Info.txt</c>.
/// <list type="bullet">
///   <item>0x00 char[4] "2IMG" magic</item>
///   <item>0x04 char[4] creator (we emit "CmpW" — CompressionWorkbench)</item>
///   <item>0x08 u16 header size = 64</item>
///   <item>0x0A u16 version = 1</item>
///   <item>0x0C u32 image format = 1 (ProDOS block order)</item>
///   <item>0x10 u32 flags = 0 (unlocked, volume number unused)</item>
///   <item>0x14 u32 ProDOS block count</item>
///   <item>0x18 u32 data offset = 64</item>
///   <item>0x1C u32 data length = blocks * 512</item>
///   <item>0x20 u32 comment offset (0 or right after data)</item>
///   <item>0x24 u32 comment length</item>
///   <item>0x28 u32 creator data offset = 0</item>
///   <item>0x2C u32 creator data length = 0</item>
///   <item>0x30 ..0x3F reserved (zero)</item>
/// </list></para>
///
/// <para><b>Delegation contract.</b> Everything about file storage,
/// subdirectory layout, the bitmap and the volume directory chain is the
/// ProDOS writer's job. This wrapper adds only the 2IMG header and an
/// optional ASCII comment block — exactly what makes the volume read as
/// GS/OS rather than bare ProDOS to detection.</para>
/// </summary>
public sealed class GsOsWriter {

  /// <summary>Default 2IMG creator code we stamp at offset 4. Four ASCII bytes.</summary>
  public const string DefaultCreator = "CmpW";

  /// <summary>2IMG header magic "2IMG" at offset 0.</summary>
  internal static readonly byte[] Magic = "2IMG"u8.ToArray();

  /// <summary>2IMG header size — always 64 bytes for the canonical spec.</summary>
  internal const int HeaderSize = 64;

  /// <summary>2IMG image-format code for ProDOS block-ordered data.</summary>
  internal const uint ProDosBlockOrder = 1;

  private readonly List<(string Name, byte[] Data)> _files = [];
  private string _comment = "";

  /// <summary>Adds a file under the given (possibly subdirectory-qualified) name.
  /// Names are sanitised by the ProDOS writer (uppercased, restricted character set,
  /// 15-char-per-component maximum).</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data));
  }

  /// <summary>Sets the 2IMG comment block — surfaced by readers as the
  /// volume comment in metadata.ini.</summary>
  public void SetComment(string comment) => this._comment = comment ?? "";

  /// <summary>
  /// Builds the 2IMG-wrapped GS/OS image.
  /// </summary>
  /// <param name="volumeName">ProDOS volume label (1..15 chars; letters,
  /// digits, periods; must start with a letter).</param>
  /// <param name="totalBlocks">ProDOS block count: 280 (140 KB 5.25" floppy)
  /// or 1600 (800 KB 3.5" floppy).</param>
  /// <param name="creator">Four-character 2IMG creator code. Defaults to
  /// <see cref="DefaultCreator"/>.</param>
  public byte[] Build(string volumeName = "GSOS", int totalBlocks = ProDosWriter.FloppyTotalBlocks,
      string creator = DefaultCreator) {

    var prodos = new ProDosWriter();
    foreach (var (name, data) in this._files)
      prodos.AddFile(name, data);
    var inner = prodos.Build(volumeName, totalBlocks);

    if (string.IsNullOrEmpty(creator)) creator = DefaultCreator;
    if (creator.Length > 4) creator = creator[..4];
    while (creator.Length < 4) creator += " ";

    var commentBytes = string.IsNullOrEmpty(this._comment)
      ? Array.Empty<byte>()
      : Encoding.ASCII.GetBytes(this._comment);

    var image = new byte[HeaderSize + inner.Length + commentBytes.Length];

    // 0x00 char[4] "2IMG"
    Magic.CopyTo(image, 0);
    // 0x04 char[4] creator
    Encoding.ASCII.GetBytes(creator).CopyTo(image, 4);
    // 0x08 u16 header size
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(8, 2), (ushort)HeaderSize);
    // 0x0A u16 version
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(10, 2), 1);
    // 0x0C u32 image format = ProDOS block order
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12, 4), ProDosBlockOrder);
    // 0x10 u32 flags = 0
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16, 4), 0);
    // 0x14 u32 ProDOS block count
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20, 4), (uint)totalBlocks);
    // 0x18 u32 data offset = 64
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24, 4), HeaderSize);
    // 0x1C u32 data length
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(28, 4), (uint)inner.Length);
    if (commentBytes.Length > 0) {
      // 0x20 u32 comment offset (right after the ProDOS payload)
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32, 4), (uint)(HeaderSize + inner.Length));
      // 0x24 u32 comment length
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(36, 4), (uint)commentBytes.Length);
    }
    // 0x28..0x2C creator data = 0 (already zero)
    // 0x30..0x3F reserved (already zero)

    inner.CopyTo(image, HeaderSize);
    if (commentBytes.Length > 0) commentBytes.CopyTo(image, HeaderSize + inner.Length);

    return image;
  }
}
