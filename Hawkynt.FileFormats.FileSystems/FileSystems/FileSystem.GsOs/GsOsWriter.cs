#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.GsOs;

/// <summary>
/// Builds Apple IIgs GS/OS 2IMG disk images. The container is a 64-byte
/// header (creator code, image format = ProDOS-ordered, data offset/length,
/// flags) followed by the embedded ProDOS volume; this writer emits a
/// ProDOS payload via <see cref="FileSystem.ProDos.ProDosWriter"/> and
/// prepends the 2IMG header so the result is recognised by GS/OS-aware
/// emulators (CiderPress, ASIMOV2, Bernie ][ The Rescue, Catakig).
///
/// <para>2IMG header layout (little-endian, 64 bytes) — see
/// <see cref="GsOsReader"/> for the field-by-field breakdown. The flags
/// word is left zero (unlocked, DOS-3.3 volume number = 0).</para>
/// </summary>
public sealed class GsOsWriter {

  private const int HeaderSize = 64;
  private const int ImageFormatProDos = 1;
  private static readonly byte[] Magic = "2IMG"u8.ToArray();
    // The 2IMG creator field names the program that made the image. CiderPress's
  // code is the one all but a handful of images in circulation carry.
  private static readonly byte[] DefaultCreator = "CdrP"u8.ToArray();

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Adds a file to the inner ProDOS volume.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data));
  }

  /// <summary>
  /// Builds the 2IMG-wrapped ProDOS image. The inner volume is built via
  /// <see cref="FileSystem.ProDos.ProDosWriter"/>; the 64-byte 2IMG header
  /// is prepended in front. The data block count records the ProDOS payload
  /// size in 512-byte blocks; the data offset is fixed at 64.
  /// </summary>
  /// <param name="volumeName">ProDOS volume name (max 15 chars).</param>
  /// <param name="totalBlocks">Inner ProDOS volume size in 512-byte blocks.</param>
  public byte[] Build(
      string volumeName = "WORM",
      int totalBlocks = FileSystem.ProDos.ProDosWriter.FloppyTotalBlocks) {
    var inner = new FileSystem.ProDos.ProDosWriter();
    foreach (var (name, data) in this._files)
      inner.AddFile(name, data);
    var prodos = inner.Build(volumeName, totalBlocks);
    return WrapWithHeader(prodos);
  }

  /// <summary>
  /// Wraps an existing ProDOS payload in a 2IMG header. Exposed so the
  /// in-place modifier can recompute the header after the inner payload
  /// length changes.
  /// </summary>
  public static byte[] WrapWithHeader(byte[] prodosPayload) {
    ArgumentNullException.ThrowIfNull(prodosPayload);
    var image = new byte[HeaderSize + prodosPayload.Length];
    var blocks = prodosPayload.Length / 512;
    WriteHeader(image.AsSpan(0, HeaderSize), (uint)blocks, (uint)prodosPayload.Length);
    Buffer.BlockCopy(prodosPayload, 0, image, HeaderSize, prodosPayload.Length);
    return image;
  }

  internal static void WriteHeader(Span<byte> header, uint dataBlockCount, uint dataLength) {
    Magic.CopyTo(header[..4]);
    DefaultCreator.CopyTo(header.Slice(4, 4));
    BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(8, 2), HeaderSize);   // header size
    BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(10, 2), 1);            // version
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(12, 4), ImageFormatProDos);
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), 0);            // flags
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(20, 4), dataBlockCount);
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24, 4), HeaderSize);   // data offset
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(28, 4), dataLength);
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(32, 4), 0);            // comment offset
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(36, 4), 0);            // comment length
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(40, 4), 0);            // creator data offset
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(44, 4), 0);            // creator data length
    // bytes 48..63 reserved — left zero.
  }
}
