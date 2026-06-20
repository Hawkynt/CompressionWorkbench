#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.DoubleSpace;

/// <summary>
/// Reads a <b>genuine</b> MS-DOS DoubleSpace/DriveSpace (v1/v2) CVF — the real
/// <c>MSDBL6.0</c> container that <see cref="GenuineCvfWriter"/> emits and that
/// the independent <c>dmsdos</c> driver mounts ("drivespace CVF version 2").
/// This is the read half of the genuine v2 round trip; with the driver-proven
/// writer it gives a full read/write path over the genuine on-disk format.
/// <para>
/// MDBPB at sector 0 carries the BPB plus the CVF geometry (inner base @0x27,
/// MDFAT start @0x24, cluster index base @0x2D, root offset @0x29). The inner
/// FAT12 volume lives at the base sector; file data clusters are located through
/// the 4-byte MDFAT (DBLSP/DRVSP packing: physical sector in bits 0..20, stored
/// run length and flags in the high bits). Stored clusters are read verbatim and
/// truncated by the directory's file size.
/// </para>
/// </summary>
public sealed class GenuineCvfReader : IDisposable {

  private const int Ss = 512;

  private readonly byte[] _data;
  private readonly List<DoubleSpaceEntry> _entries = [];

  private int _spc;
  private int _mdfatStart;
  private int _sDcluster;
  private int _fatStart;
  private int _rootStart;
  private int _rootEntries;

  public IReadOnlyList<DoubleSpaceEntry> Entries => this._entries;

  /// <summary>The inner volume label (0x08 root entry), or "" when none was written.</summary>
  public string VolumeLabel { get; private set; } = "";

  public GenuineCvfReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    if (stream.CanSeek) stream.Position = 0;
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

  private ushort U16(int off) => BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(off));

  private void Parse() {
    if (this._data.Length < Ss || Encoding.ASCII.GetString(this._data, 3, 8) != "MSDBL6.0")
      throw new InvalidDataException("DoubleSpace (genuine): missing MSDBL6.0 signature.");

    this._spc = this._data[0x0D];
    var resv = this.U16(0x0E);
    var innerBase = this.U16(0x27);
    this._mdfatStart = this.U16(0x24) + 1;
    this._sDcluster = this.U16(0x2D);
    this._rootEntries = this.U16(0x11);
    var rootLog = this.U16(0x29);
    this._fatStart = innerBase + resv;
    this._rootStart = innerBase + rootLog;
    if (this._spc <= 0) throw new InvalidDataException("DoubleSpace (genuine): invalid sectors/cluster.");

    this.ReadRootDirectory();
  }

  private void ReadRootDirectory() {
    var rootOff = this._rootStart * Ss;
    for (var i = 0; i < this._rootEntries; i++) {
      var de = rootOff + i * 32;
      if (de + 32 > this._data.Length) break;
      var first = this._data[de];
      if (first == 0x00) break;
      if (first == 0xE5) continue;
      var attr = this._data[de + 11];
      if (attr == 0x0F) continue;                       // LFN slot
      if ((attr & 0x08) != 0) {                          // volume label
        this.VolumeLabel = Encoding.ASCII.GetString(this._data, de, 11).TrimEnd(' ');
        continue;
      }
      if ((attr & 0x10) != 0) continue;                  // directory

      var stem = Encoding.ASCII.GetString(this._data, de, 8).TrimEnd(' ');
      var ext = Encoding.ASCII.GetString(this._data, de + 8, 3).TrimEnd(' ');
      var name = ext.Length > 0 ? $"{stem}.{ext}" : stem;
      var firstCluster = this.U16(de + 26);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(de + 28));
      this._entries.Add(new DoubleSpaceEntry {
        Name = name, Size = size, StartCluster = firstCluster, IsDirectory = false,
      });
    }
  }

  private int MdfatBytePos(int cluster) =>
    (this._sDcluster + cluster) * 4 + Ss * this._mdfatStart;

  private int NextCluster(int cluster) {
    var o = this._fatStart * Ss + cluster * 3 / 2;
    var pair = this._data[o] | (this._data[o + 1] << 8);
    return (cluster & 1) == 0 ? pair & 0xFFF : (pair >> 4) & 0xFFF;
  }

  public byte[] Extract(DoubleSpaceEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.Size == 0) return [];

    var output = new byte[entry.Size];
    var written = 0;
    var cluster = entry.StartCluster;
    var guard = 0;
    var maxClusters = this._data.Length / (this._spc * Ss) + 2;

    while (cluster >= 2 && cluster < 0xFF8 && written < entry.Size && guard++ < maxClusters) {
      var p = this.MdfatBytePos(cluster);
      if (p + 4 > this._data.Length) break;
      var res = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(p));
      var flags = (res >> 30) & 3;
      if ((flags & 2) != 0) {
        var physSector = (int)(res & 0x1FFFFF) + 1;
        var srcOff = physSector * Ss;
        var copy = Math.Min(this._spc * Ss, (int)entry.Size - written);
        if (copy > 0 && srcOff + copy <= this._data.Length)
          Array.Copy(this._data, srcOff, output, written, copy);
        written += copy;
      }
      cluster = this.NextCluster(cluster);
    }

    return output;
  }

  public void Dispose() { }
}
