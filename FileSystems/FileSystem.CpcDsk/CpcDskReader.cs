#pragma warning disable CS1591
using System.Text;
using static FileSystem.CpcDsk.CpcDskAmsdos;

namespace FileSystem.CpcDsk;

/// <summary>
/// Reads the files out of an Amstrad CPC DSK image.
/// </summary>
/// <remarks>
/// <para>This used to enumerate sectors, and name them after where they sat —
/// <c>T00S0_C1</c> and so on. That is a true description of a DSK container and
/// no description at all of what is on the disk: every file written to it came
/// back as a list of sectors, so a volume that round-tripped its bytes perfectly
/// reported every one of its files as missing. What a CPC reads is the AMSDOS
/// directory, so that is what this reads.</para>
///
/// <para>CP/M records a length only as a count of 128-byte records, so a file
/// comes back rounded up to the next record, padded with zeros. That is the
/// format's own granularity, not a loss in the reading of it.</para>
/// </remarks>
public sealed class CpcDskReader {

  private readonly byte[] _data;
  private readonly List<CpcDskEntry> _entries = [];
  private Geometry? _geometry;

    /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<CpcDskEntry> Entries => this._entries;

    /// <summary>
  /// Gets a value indicating whether is extended.
  /// </summary>
public bool IsExtended { get; private set; }
    /// <summary>
  /// Gets or sets the tracks.
  /// </summary>
public int Tracks { get; private set; }
    /// <summary>
  /// Gets or sets the sides.
  /// </summary>
public int Sides { get; private set; }

  /// <summary>The disk's layout, once the header has been read.</summary>
  internal Geometry? Layout => this._geometry;

    /// <summary>
  /// Initializes a new instance of <see cref="CpcDskReader"/>.
  /// </summary>
public CpcDskReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    stream.Position = 0;
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    this._data = buffer.ToArray();
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < DiskInfoSize)
      throw new InvalidDataException(
        $"CPC DSK: an image is at least {DiskInfoSize} bytes of disk info; this one is {this._data.Length}.");

    var magic = Encoding.ASCII.GetString(this._data, 0, 8);
    if (magic.StartsWith("EXTENDED", StringComparison.Ordinal)) this.IsExtended = true;
    else if (!magic.StartsWith("MV - CPC", StringComparison.Ordinal))
      throw new InvalidDataException($"CPC DSK: unrecognised magic '{magic}'.");

    this.Tracks = this._data[48];
    this.Sides = this._data[49];
    if (this.Tracks == 0 || this.Sides == 0) return;

    var geometry = this.ReadGeometry();
    if (geometry == null) return;
    this._geometry = geometry;

    foreach (var file in ReadDirectory(this._data, geometry)) {
      var first = file.Blocks.Count > 0 ? geometry.SectorsOfBlock(file.Blocks[0]).First() : 0;
      var cylinder = first / geometry.SectorsPerCylinder;
      var withinCylinder = first % geometry.SectorsPerCylinder;

      this._entries.Add(new CpcDskEntry {
        Name = file.Name,
        Track = cylinder,
        Side = withinCylinder / geometry.SectorsPerTrackCount,
        SectorId = (byte)(FirstSectorId + withinCylinder % geometry.SectorsPerTrackCount),
        Size = (int)file.Length,
        DataOffset = geometry.SectorOffset(first),
        Blocks = file.Blocks,
      });
    }
  }

  /// <summary>
  /// Walks the track info blocks to learn where each track sits and how its
  /// sectors are sized.
  /// </summary>
  /// <remarks>
  /// An extended image sizes each track separately in the header's size table, so
  /// the offsets are accumulated rather than assumed; a standard one repeats the
  /// same track length throughout.
  /// </remarks>
  private Geometry? ReadGeometry() {
    var count = this.Tracks * this.Sides;
    var offsets = new long[count];
    var at = (long)DiskInfoSize;
    var sectorsPerTrack = 0;
    var sectorSize = 0;

    for (var i = 0; i < count; ++i) {
      long trackBytes;
      if (this.IsExtended) {
        var high = this._data[52 + i];
        trackBytes = high * 256L;
        if (high == 0) { offsets[i] = -1; continue; }
      } else {
        trackBytes = this._data[50] | (this._data[51] << 8);
      }

      if (at + TrackInfoSize > this._data.Length) { offsets[i] = -1; continue; }

      var marker = Encoding.ASCII.GetString(this._data, (int)at, 10);
      if (!marker.StartsWith("Track-Info", StringComparison.Ordinal)) { offsets[i] = -1; continue; }

      offsets[i] = at;
      if (sectorsPerTrack == 0) {
        sectorsPerTrack = this._data[at + 21];
        sectorSize = 128 << this._data[at + 20];
      }

      at += trackBytes;
    }

    if (sectorsPerTrack <= 0 || sectorSize <= 0) return null;

    return new Geometry {
      Tracks = this.Tracks, Sides = this.Sides,
      SectorsPerTrackCount = sectorsPerTrack, SectorBytes = sectorSize,
      TrackOffsets = offsets,
    };
  }

  /// <summary>Returns one file's bytes, gathered from the blocks the directory gives it.</summary>
  public byte[] Extract(CpcDskEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (this._geometry == null) return [];

    var result = new byte[entry.Size];
    var written = 0;
    foreach (var block in entry.Blocks) {
      foreach (var sector in this._geometry.SectorsOfBlock(block)) {
        if (written >= result.Length) break;

        var offset = this._geometry.SectorOffset(sector);
        if (offset < 0 || offset + this._geometry.SectorBytes > this._data.Length) continue;

        var chunk = Math.Min(this._geometry.SectorBytes, result.Length - written);
        Array.Copy(this._data, offset, result, written, chunk);
        written += chunk;
      }
    }

    return result;
  }
}
