#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.GsOs;

/// <summary>
/// Reads Apple IIgs GS/OS disk images packaged in the 2IMG container
/// (the canonical emulator format for IIgs disks). GS/OS is an
/// extended ProDOS filesystem that adds Mac-HFS-style resource forks
/// and longer filenames; volumes can be Extended ProDOS (version &gt;= 5),
/// HFS, or DOS 3.3 — this reader handles the 2IMG header parse and
/// surfaces the embedded volume as an opaque entry for delegation
/// to a ProDOS / HFS reader downstream.
/// <para>
/// 2IMG header layout (little-endian, 64 bytes):
///   0x00 char[4] "2IMG"
///   0x04 char[4] creator code (e.g. "CTKG"=Catakig, "ASIM"=ASIMOV2, "B2TR"=Bernie ][ The Rescue)
///   0x08 u16     header size (always 64)
///   0x0A u16     version
///   0x0C u32     image format (0=DOS 3.3 order, 1=ProDOS order, 2=NIB)
///   0x10 u32     flags (bit 0x80000000 = locked; low byte = volume number for DOS 3.3)
///   0x14 u32     data block count (ProDOS blocks)
///   0x18 u32     data offset (relative to file start)
///   0x1C u32     data length (bytes)
///   0x20 u32     comment offset
///   0x24 u32     comment length
///   0x28 u32     creator data offset
///   0x2C u32     creator data length
///   0x30..0x3F  reserved
/// </para>
/// </summary>
public sealed class GsOsReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<GsOsEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<GsOsEntry> Entries => _entries;
  /// <summary>
  /// Gets a value indicating whether valid header.
  /// </summary>
  public bool ValidHeader { get; private set; }
  /// <summary>
  /// Gets or sets the creator.
  /// </summary>
  public string Creator { get; private set; } = "";
  /// <summary>
  /// Gets or sets the version.
  /// </summary>
  public int Version { get; private set; }
  /// <summary>
  /// Gets or sets the image format.
  /// </summary>
  public int ImageFormat { get; private set; }
  /// <summary>
  /// Gets or sets the flags.
  /// </summary>
  public uint Flags { get; private set; }
  /// <summary>
  /// Gets or sets the data block count.
  /// </summary>
  public uint DataBlockCount { get; private set; }
  /// <summary>
  /// Gets or sets the data offset.
  /// </summary>
  public uint DataOffset { get; private set; }
  /// <summary>
  /// Gets or sets the data length.
  /// </summary>
  public uint DataLength { get; private set; }
  /// <summary>
  /// Gets or sets the comment.
  /// </summary>
  public string Comment { get; private set; } = "";

  /// <summary>
  /// Provides the magic value.
  /// </summary>
  public static readonly byte[] Magic = "2IMG"u8.ToArray();

  /// <summary>
  /// Initializes a new instance of <see cref="GsOsReader"/>.
  /// </summary>
  public GsOsReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 64) return;
    if (!_data.AsSpan(0, 4).SequenceEqual(Magic)) return;
    var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(8, 2));
    if (headerSize is < 52 or > 64) return;
    this.ValidHeader = true;

    this.Creator = Encoding.ASCII.GetString(_data, 4, 4);
    this.Version = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(10, 2));
    this.ImageFormat = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(12, 4));
    this.Flags = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(16, 4));
    this.DataBlockCount = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(20, 4));
    this.DataOffset = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(24, 4));
    this.DataLength = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(28, 4));

    if (this.DataOffset >= _data.Length) return;

    var commentOffset = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(32, 4));
    var commentLength = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(36, 4));
    if (commentOffset > 0 && commentOffset < _data.Length &&
        commentOffset + commentLength <= _data.Length && commentLength < 1024) {
      this.Comment = Encoding.ASCII.GetString(_data, (int)commentOffset, (int)commentLength);
    }

    // Surface the embedded ProDOS / HFS volume as a single opaque entry.
    // The descriptor stub does not delegate to FileSystem.ProDos (sibling-agent
    // boundary); callers can route the .po payload through the ProDOS descriptor.
    var dataLen = (int)Math.Min(this.DataLength, _data.Length - this.DataOffset);
    if (dataLen > 0) {
      var name = this.ImageFormat switch {
        0 => "gsos-dos33-image.dsk",
        1 => "gsos-prodos-volume.po",
        2 => "gsos-nib-image.nib",
        _ => "gsos-image.bin",
      };
      _entries.Add(new GsOsEntry {
        Name = name,
        Size = dataLen,
        IsDirectory = false,
        DataOffset = (int)this.DataOffset,
      });
    }
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(GsOsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.DataOffset < 0 || entry.DataOffset + entry.Size > _data.Length) return [];
    return _data.AsSpan(entry.DataOffset, (int)entry.Size).ToArray();
  }

  /// <summary>
  /// Performs the build surface metadata operation.
  /// </summary>
  public byte[] BuildSurfaceMetadata() {
    var b = new StringBuilder();
    b.Append("parse_status=").Append(this.ValidHeader ? "ok" : "invalid").Append('\n');
    b.Append("format=Apple IIgs GS/OS (2IMG)\n");
    b.Append(CultureInfo.InvariantCulture, $"creator={this.Creator}\n");
    b.Append(CultureInfo.InvariantCulture, $"version={this.Version}\n");
    b.Append(CultureInfo.InvariantCulture, $"image_format={this.ImageFormat}\n");
    b.Append(CultureInfo.InvariantCulture, $"flags=0x{this.Flags:X8}\n");
    b.Append(CultureInfo.InvariantCulture, $"data_block_count={this.DataBlockCount}\n");
    b.Append(CultureInfo.InvariantCulture, $"data_offset={this.DataOffset}\n");
    b.Append(CultureInfo.InvariantCulture, $"data_length={this.DataLength}\n");
    if (!string.IsNullOrEmpty(this.Comment))
      b.Append(CultureInfo.InvariantCulture, $"comment={this.Comment.Replace('\n', ' ')}\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
