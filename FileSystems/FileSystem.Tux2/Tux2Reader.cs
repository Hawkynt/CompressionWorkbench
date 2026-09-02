#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Globalization;
using System.Text;

namespace FileSystem.Tux2;

/// <summary>
/// Detection-only / synthetic-image reader for TUX2 — Daniel Phillips's
/// 2000-era "phase tree" filesystem proposal. TUX2 was a research design
/// (atomic phase-tree commits, copy-on-write metadata) that never reached
/// a stable on-disk layout shipped to end users. No public spec for the
/// in-progress prototype's on-disk format ever stabilised; the project
/// was eventually superseded by TUX3.
///
/// Because no canonical TUX2 images exist in the wild, this reader
/// recognises a deterministic synthetic header — a chosen 8-byte ASCII
/// magic "TUX2FS\0\0" at offset 0 followed by a small JSON-ish payload —
/// so that the descriptor at least round-trips its own synthetic images
/// for testing. Real TUX2 prototype dumps (if any survive) would need a
/// custom parser matching the specific cvs-era code path that produced
/// them.
///
/// Synthetic header layout (little-endian):
///   0x00 8 bytes  Magic = "TUX2FS\0\0"
///   0x08 u32      version (1)
///   0x0C u32      file_count
///   0x10 ...      per-file records:
///                   u16 name_len
///                   name (UTF-8, name_len bytes)
///                   u32 data_len
///                   data (data_len bytes)
/// </summary>
public sealed class Tux2Reader : IDisposable {
  private readonly ImageAccessor _img;
  private readonly long _len;
  private readonly List<Tux2Entry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<Tux2Entry> Entries => _entries;

  /// <summary>
  /// Gets or sets the version.
  /// </summary>
  public uint Version { get; private set; }
  /// <summary>
  /// Gets or sets the file count.
  /// </summary>
  public uint FileCount { get; private set; }
  /// <summary>
  /// Gets a value indicating whether valid header.
  /// </summary>
  public bool ValidHeader { get; private set; }

  /// <summary>
  /// Provides the magic value.
  /// </summary>
  public static readonly byte[] Magic = "TUX2FS\0\0"u8.ToArray();

  /// <summary>
  /// Initializes a new instance of <see cref="Tux2Reader"/>.
  /// </summary>
  public Tux2Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    // Records are located on demand: copying the image in, and then every file's
    // bytes out of it, held the payload twice over.
    _img = new ImageAccessor(stream);
    _len = _img.Length;
    Parse();
  }

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._len;

  private void Parse() {
    if (_len < 16)
      throw new InvalidDataException("Tux2: image too small for header.");

    if (!_img.Read(0, 8).AsSpan().SequenceEqual(Magic))
      throw new InvalidDataException("Tux2: missing TUX2FS magic at offset 0.");

    this.Version = _img.ReadUInt32(8);
    this.FileCount = _img.ReadUInt32(12);
    this.ValidHeader = true;

    // Always emit metadata + raw image so the descriptor is useful even on
    // images we can't fully parse (research-grade).
    _entries.Add(new Tux2Entry { Name = "FULL.tux2", Size = _len, Offset = 0 });
    _entries.Add(new Tux2Entry { Name = "metadata.ini", Data = BuildMetadata() });

    // Walk synthetic per-file records if the file_count looks sane.
    var pos = 16L;
    var count = 0u;
    while (count < this.FileCount && pos + 2 <= _len) {
      var nameLen = _img.ReadUInt16(pos);
      pos += 2;
      if (pos + nameLen + 4 > _len) break;
      var name = Encoding.UTF8.GetString(_img.Read(pos, nameLen));
      pos += nameLen;
      var dataLen = _img.ReadUInt32(pos);
      pos += 4;
      if (pos + dataLen > _len) break;

      // The bytes stay where they are; the entry records where to find them.
      _entries.Add(new Tux2Entry { Name = name, Size = dataLen, Offset = pos });
      pos += dataLen;
      count++;
    }

    // Finalise metadata after walking so file_walk_status is set.
    _entries[1] = new Tux2Entry { Name = "metadata.ini", Data = BuildMetadata(count) };
  }

  private byte[] BuildMetadata(uint? walked = null) {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=ok\n");
    bldr.Append("format=TUX2 (synthetic research format)\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version={this.Version}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"file_count={this.FileCount}\n");
    if (walked.HasValue)
      bldr.Append(CultureInfo.InvariantCulture, $"files_walked={walked.Value}\n");
    bldr.Append("note=TUX2 was Daniel Phillips's 2000 phase-tree proposal; no canonical on-disk format ever shipped.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(Tux2Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Offset < 0) return entry.Data;
    if (entry.Size > Array.MaxLength)
      throw new IOException(
        $"Tux2: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    return _img.Read(entry.Offset, (int)entry.Size);
  }

  /// <summary>Writes <paramref name="entry" />'s bytes into <paramref name="destination" />.</summary>
  public long ExtractTo(Tux2Entry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.Offset < 0) {
      destination.Write(entry.Data);
      return entry.Data.Length;
    }
    var take = Math.Min(entry.Size, _len - entry.Offset);
    if (take <= 0) return 0;
    _img.CopyTo(entry.Offset, destination, take);
    return take;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() => this._img.Dispose();
}
