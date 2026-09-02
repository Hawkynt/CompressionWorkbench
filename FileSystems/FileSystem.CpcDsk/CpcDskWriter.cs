#pragma warning disable CS1591
using System.Text;
using static FileSystem.CpcDsk.CpcDskAmsdos;

namespace FileSystem.CpcDsk;

/// <summary>
/// Writes a Standard CPC DSK image holding an AMSDOS DATA-format filesystem.
/// </summary>
/// <remarks>
/// <para>The container is the easy half: a disk info header, then each track's
/// info block followed by its sectors. The filesystem inside it is what a CPC
/// actually reads, and it is ordinary CP/M 2.2 — kilobyte allocation blocks
/// numbered from the start of the disk, the directory in the first two of them,
/// and a directory entry for every sixteen kilobytes of every file.</para>
///
/// <para>A disk that numbers its blocks any other way still looks like a disk and
/// still lists filenames; it is only when something follows those numbers to the
/// data that the difference shows, which is why this follows the format rather
/// than a convention of its own.</para>
/// </remarks>
public sealed class CpcDskWriter : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly int _tracks;
  private readonly int _sides;
  private readonly int _sectorsPerTrack;
  private readonly int _sectorSize;
  private readonly List<(string Name, byte[] Data)> _files = [];
  private bool _finished;

    /// <summary>
  /// Initializes a new instance of <see cref="CpcDskWriter"/>.
  /// </summary>
public CpcDskWriter(Stream stream, bool leaveOpen = false,
      int tracks = 40, int sides = 1,
      int sectorsPerTrack = SectorsPerTrack, int sectorSize = SectorSize) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    this._leaveOpen = leaveOpen;
    this._tracks = tracks;
    this._sides = sides;
    this._sectorsPerTrack = sectorsPerTrack;
    this._sectorSize = sectorSize;
  }

  /// <summary>The geometry this writer will lay down.</summary>
  internal Geometry Layout => Geometry.Standard(this._tracks, this._sides,
    this._sectorsPerTrack, this._sectorSize);

    /// <summary>
  /// Performs the add file operation.
  /// </summary>
public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (this._finished)
      throw new InvalidOperationException("CPC DSK: the image has been written; nothing more can be added to it.");
    this._files.Add((name, data));
  }

  /// <summary>Lays the directory and the file data down and writes the image.</summary>
  public void Finish() {
    if (this._finished)
      throw new InvalidOperationException("CPC DSK: the image has already been written.");
    this._finished = true;

    var geometry = this.Layout;

    // The directory is built first because it is what decides where everything
    // goes, and because it is where running out of room is discovered.
    var directory = BuildDirectory(this._files, geometry, out var placement);

    // A flat picture of the disk's sectors, filled in and then written out.
    var totalSectors = this._tracks * this._sides * this._sectorsPerTrack;
    var surface = new byte[totalSectors * this._sectorSize];

    var directoryAt = 0;
    for (var block = 0; block < DirectoryBlocks; ++block)
      foreach (var sector in geometry.SectorsOfBlock(block)) {
        Array.Copy(directory, directoryAt, surface, sector * this._sectorSize, this._sectorSize);
        directoryAt += this._sectorSize;
      }

    foreach (var (name, data) in this._files) {
      if (!placement.TryGetValue(name, out var blocks)) continue;

      var written = 0;
      foreach (var block in blocks) {
        foreach (var sector in geometry.SectorsOfBlock(block)) {
          if (written >= data.Length) break;

          var chunk = Math.Min(this._sectorSize, data.Length - written);
          data.AsSpan(written, chunk).CopyTo(surface.AsSpan(sector * this._sectorSize));
          written += chunk;
        }
      }
    }

    this._stream.Write(BuildDiskInfo(this._tracks, this._sides, this._sectorsPerTrack, this._sectorSize));
    for (var t = 0; t < this._tracks; ++t)
      for (var s = 0; s < this._sides; ++s) {
        this._stream.Write(BuildTrackInfo(t, s, this._sectorsPerTrack, this._sectorSize));
        var first = ((t * this._sides) + s) * this._sectorsPerTrack;
        this._stream.Write(surface, first * this._sectorSize, this._sectorsPerTrack * this._sectorSize);
      }

    this._stream.Flush();
  }

  /// <summary>The 256-byte header a Standard DSK opens with.</summary>
  internal static byte[] BuildDiskInfo(int tracks, int sides, int sectorsPerTrack, int sectorSize) {
    var header = new byte[DiskInfoSize];
    Encoding.ASCII.GetBytes("MV - CPCEMU Disk-File\r\nDisk-Info\r\n").CopyTo(header, 0);
    Encoding.ASCII.GetBytes("CompressionWorkbench").CopyTo(header, 34);
    header[48] = (byte)tracks;
    header[49] = (byte)sides;
    var trackBytes = TrackInfoSize + sectorsPerTrack * sectorSize;
    header[50] = (byte)(trackBytes & 0xFF);
    header[51] = (byte)(trackBytes >> 8);
    return header;
  }

  /// <summary>The 256-byte block that opens one track, and names its sectors.</summary>
  internal static byte[] BuildTrackInfo(int track, int side, int sectorsPerTrack, int sectorSize) {
    var info = new byte[TrackInfoSize];
    Encoding.ASCII.GetBytes("Track-Info\r\n").CopyTo(info, 0);
    info[12] = 0;
    info[16] = (byte)track;
    info[17] = (byte)side;
    info[20] = (byte)SizeCode(sectorSize);
    info[21] = (byte)sectorsPerTrack;
    info[22] = 0x4E;                       // GAP#3
    info[23] = Unused;                     // filler

    for (var i = 0; i < sectorsPerTrack; ++i) {
      var at = 24 + i * 8;
      info[at + 0] = (byte)track;
      info[at + 1] = (byte)side;
      info[at + 2] = (byte)(FirstSectorId + i);
      info[at + 3] = (byte)SizeCode(sectorSize);
    }

    return info;
  }

  private static int SizeCode(int sectorSize) {
    var code = 0;
    var size = 128;
    while (size < sectorSize && code < 7) { size <<= 1; ++code; }
    return code;
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() {
    if (!this._finished) this.Finish();
    if (!this._leaveOpen) this._stream.Dispose();
  }
}
