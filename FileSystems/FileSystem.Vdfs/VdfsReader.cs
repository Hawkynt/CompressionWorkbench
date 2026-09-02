#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Vdfs;

/// <summary>
/// Reads the entry table of a Gothic-engine VDFS archive and extracts the files it holds.
/// </summary>
public sealed class VdfsReader : IDisposable {
  private static readonly byte[] Magic = "PSVDSC_V2.00\n\r\n\r"u8.ToArray();
  private const int HeaderSize = 16;
  private const int EntrySize = 80;

  /// <summary>
  /// Bytes of fixed header the entry table follows when the header names no
  /// offset of its own.
  /// </summary>
  public const int DefaultEntryTableOffset = 36;

  /// <summary>Byte offset the entry table actually starts at.</summary>
  public long EntryTableOffset { get; private set; }

  /// <summary>Bytes the entry table occupies.</summary>
  public long EntryTableLength { get; private set; }

  /// <summary>
  /// Random-access view over the container. Copying it into a byte[] capped the
  /// reader at the array limit, which the 32-bit entry offsets do not.
  /// </summary>
  private readonly ImageAccessor _data;
  private readonly List<VdfsEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<VdfsEntry> Entries => _entries;

  /// <summary>
  /// Initializes a new instance of <see cref="VdfsReader"/>.
  /// </summary>
  public VdfsReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    _data = new ImageAccessor(stream, leaveOpen: true);
    Parse();
  }

  private void Parse() {
    if (_data.Length < HeaderSize + 20)
      throw new InvalidDataException("VDFS: file too small.");

    if (!_data.Read(0, Magic.Length).AsSpan().SequenceEqual(Magic))
      throw new InvalidDataException("VDFS: invalid magic.");

    var entryCount = (int)_data.ReadUInt32(16);
    var rootOffset = (int)_data.ReadUInt32(32);

    // Entries start at rootOffset (or at offset 36 if rootOffset is 0)
    var entriesStart = rootOffset > 0 ? rootOffset : DefaultEntryTableOffset;
    this.EntryTableOffset = entriesStart;
    this.EntryTableLength = (long)entryCount * EntrySize;

    for (int i = 0; i < entryCount; i++) {
      var off = entriesStart + i * EntrySize;
      if (off + EntrySize > _data.Length) break;

      // Name: 64 bytes, null/space terminated
      var nameEnd = off + 64;
      var nameLen = 64;
      for (int j = 0; j < 64; j++) {
        if (_data.ReadByte(off + j) == 0 || _data.ReadByte(off + j) == 0x20 && (j + 1 >= 64 || _data.ReadByte(off + j + 1) == 0)) {
          nameLen = j;
          break;
        }
      }
      var name = Encoding.ASCII.GetString(_data.Read(off, nameLen)).TrimEnd();

      var dataOffset = _data.ReadUInt32(off + 64);
      var size = _data.ReadUInt32(off + 68);
      var type = _data.ReadUInt32(off + 72);

      var isDir = (type & 0x01) != 0 && (type & 0x02) == 0;
      // Some VDFS implementations use: type & 0x01 for directory, rest are files
      // Use bitmask: if bit 0 set and not bit 1 -> directory

      if (string.IsNullOrEmpty(name)) continue;

      _entries.Add(new VdfsEntry {
        Name = name,
        Size = isDir ? 0 : size,
        IsDirectory = isDir,
        DataOffset = dataOffset,
      });
    }
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(VdfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.DataOffset + entry.Size > _data.Length) return [];
    return _data.Read(entry.DataOffset, (int)entry.Size);
  }

  /// <summary>
  /// Copies an entry's bytes into <paramref name="destination" /> a block at a
  /// time, so an entry larger than a byte[] can hold is extracted like any other.
  /// </summary>
  public void ExtractTo(VdfsEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory) return;
    if (entry.DataOffset + entry.Size > _data.Length) return;
    _data.CopyTo(entry.DataOffset, destination, entry.Size);
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
