#pragma warning disable CS1591
using System.Buffers.Binary;
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
  private readonly byte[] _data;
  private readonly List<Tux2Entry> _entries = [];

  public IReadOnlyList<Tux2Entry> Entries => _entries;

  public uint Version { get; private set; }
  public uint FileCount { get; private set; }
  public bool ValidHeader { get; private set; }

  public static readonly byte[] Magic = "TUX2FS\0\0"u8.ToArray();

  public Tux2Reader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 16)
      throw new InvalidDataException("Tux2: image too small for header.");

    if (!_data.AsSpan(0, 8).SequenceEqual(Magic))
      throw new InvalidDataException("Tux2: missing TUX2FS magic at offset 0.");

    this.Version = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(8));
    this.FileCount = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(12));
    this.ValidHeader = true;

    // Always emit metadata + raw image so the descriptor is useful even on
    // images we can't fully parse (research-grade).
    _entries.Add(new Tux2Entry { Name = "FULL.tux2", Size = _data.Length, Data = _data });
    _entries.Add(new Tux2Entry { Name = "metadata.ini", Data = BuildMetadata() });

    // Walk synthetic per-file records if the file_count looks sane.
    var pos = 16;
    var count = 0u;
    while (count < this.FileCount && pos + 2 <= _data.Length) {
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(pos));
      pos += 2;
      if (pos + nameLen + 4 > _data.Length) break;
      var name = Encoding.UTF8.GetString(_data, pos, nameLen);
      pos += nameLen;
      var dataLen = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(pos));
      pos += 4;
      if (dataLen > int.MaxValue || pos + dataLen > _data.Length) break;
      var data = _data.AsSpan(pos, (int)dataLen).ToArray();
      pos += (int)dataLen;

      _entries.Add(new Tux2Entry { Name = name, Size = data.Length, Data = data });
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

  public byte[] Extract(Tux2Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  public void Dispose() { }
}
