#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.Cromemco;

/// <summary>
/// Reads Cromemco RDOS (Z-80 system disk) volumes. RDOS is a CP/M-like
/// filesystem with 8.3 filenames, 128-byte sectors, and a fixed
/// directory area in the system tracks. The bootblock at block 0 starts
/// with a JP-instruction (0xC3 low high) followed by an embedded
/// "CROMEMCO" ASCII tag that identifies the volume.
/// <para>
/// Bootblock layout (block 0, little-endian; first 32 bytes):
///   0x00 byte    0xC3   (Z-80 JP instruction)
///   0x01 u16     entry-point address
///   0x03 char[8] reserved (zero-padded)
///   0x0B char[8] "CROMEMCO" ASCII (signature; may also appear at
///                varying offsets up to 0x40 in late RDOS variants —
///                we scan the first 64 bytes)
/// </para>
/// <para>
/// Directory entry layout (32 bytes; back-to-back in the directory
/// area starting at sector 2 = file offset 0x100):
///   0x00 byte    user code (0xE5 = deleted)
///   0x01 char[8] filename (space-padded ASCII)
///   0x09 char[3] extension (space-padded ASCII)
///   0x0C u16     start block (LE)
///   0x0E u16     length in 128-byte records (LE)
///   0x10..0x1F  reserved
/// </para>
/// </summary>
public sealed class CromemcoReader : IDisposable {
  /// <summary>
  /// Defines the sector size constant value.
  /// </summary>
  public const int SectorSize = 128;
  /// <summary>
  /// Defines the directory offset constant value.
  /// </summary>
  public const int DirectoryOffset = 0x100;
  /// <summary>
  /// Defines the entry size constant value.
  /// </summary>
  public const int EntrySize = 32;
  /// <summary>
  /// Defines the max entries constant value.
  /// </summary>
  public const int MaxEntries = 64;

  private readonly byte[] _data;
  private readonly List<CromemcoEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<CromemcoEntry> Entries => _entries;
  /// <summary>
  /// Gets a value indicating whether valid volume.
  /// </summary>
  public bool ValidVolume { get; private set; }
  /// <summary>
  /// Gets or sets the signature offset.
  /// </summary>
  public int SignatureOffset { get; private set; }

  /// <summary>
  /// Provides the signature value.
  /// </summary>
  public static readonly byte[] Signature = "CROMEMCO"u8.ToArray();

  /// <summary>
  /// Initializes a new instance of <see cref="CromemcoReader"/>.
  /// </summary>
  public CromemcoReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < DirectoryOffset + EntrySize) return;
    // Bootblock must start with JP (0xC3).
    if (_data[0] != 0xC3) return;
    // Scan first 64 bytes for "CROMEMCO".
    var sigPos = -1;
    var scanLen = Math.Min(64, _data.Length - Signature.Length);
    for (var i = 0; i < scanLen; i++) {
      if (_data.AsSpan(i, Signature.Length).SequenceEqual(Signature)) {
        sigPos = i;
        break;
      }
    }
    if (sigPos < 0) return;
    this.SignatureOffset = sigPos;
    this.ValidVolume = true;

    for (var i = 0; i < MaxEntries; i++) {
      var off = DirectoryOffset + i * EntrySize;
      if (off + EntrySize > _data.Length) break;
      var entry = _data.AsSpan(off, EntrySize);
      var userCode = entry[0];
      if (userCode == 0xE5) continue; // Deleted entry.
      if (userCode == 0x00 && IsAllZeroOrSpace(entry[1..12])) {
        // Empty slot — stop scanning forward (RDOS directory is densely packed).
        break;
      }
      var name = ReadCpmName(entry.Slice(1, 8));
      var ext = ReadCpmName(entry.Slice(9, 3));
      var startBlock = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(12, 2));
      var records = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(14, 2));
      // Byte 0x10: bytes-in-last-sector (0 == sector full / unknown).
      // Used to recover exact file length when not a multiple of SectorSize.
      var tailBytes = entry[16];
      var fullName = string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";
      if (string.IsNullOrEmpty(name)) continue;
      long size = records * (long)SectorSize;
      if (records > 0 && tailBytes > 0 && tailBytes < SectorSize)
        size = (records - 1L) * SectorSize + tailBytes;
      _entries.Add(new CromemcoEntry {
        Name = fullName,
        Size = size,
        IsDirectory = false,
        StartBlock = startBlock,
        BlockCount = records,
      });
    }
  }

  private static bool IsAllZeroOrSpace(ReadOnlySpan<byte> span) {
    foreach (var b in span)
      if (b != 0 && b != 0x20) return false;
    return true;
  }

  private static string ReadCpmName(ReadOnlySpan<byte> span) {
    Span<char> chars = stackalloc char[span.Length];
    var len = 0;
    foreach (var b in span) {
      var c = (byte)(b & 0x7F); // strip CP/M attribute high-bits
      if (c == 0 || c == 0x20) break;
      chars[len++] = (char)c;
    }
    return new string(chars[..len]);
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(CromemcoEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var offset = entry.StartBlock * SectorSize;
    if (offset < 0 || offset >= _data.Length) return [];
    var size = (int)Math.Min(entry.Size, _data.Length - offset);
    return size <= 0 ? [] : _data.AsSpan(offset, size).ToArray();
  }

  /// <summary>
  /// Performs the build surface metadata operation.
  /// </summary>
  public byte[] BuildSurfaceMetadata() {
    var b = new StringBuilder();
    b.Append("parse_status=").Append(this.ValidVolume ? "ok" : "invalid").Append('\n');
    b.Append("format=Cromemco RDOS\n");
    b.Append(CultureInfo.InvariantCulture, $"signature_offset={this.SignatureOffset}\n");
    b.Append(CultureInfo.InvariantCulture, $"file_count={this.Entries.Count}\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
