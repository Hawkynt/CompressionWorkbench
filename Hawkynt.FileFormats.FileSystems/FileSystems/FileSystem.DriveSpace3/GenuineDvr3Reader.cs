#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.DriveSpace3;

/// <summary>
/// Reads a <b>genuine</b> Microsoft DriveSpace 3 CVF — the real
/// <c>MSDBL6.0</c>-family layout that <see cref="GenuineDvr3Writer"/> emits and
/// that the independent <c>dmsdos</c> driver mounts. This is the read half of
/// the genuine DriveSpace 3 round trip; together with the driver-proven writer
/// it gives a full read/write path over the genuine on-disk format.
/// <para>
/// Layout (little-endian): MDBPB at sector 0 carries the BPB plus the CVF
/// geometry substructure — inner-volume base sector @0x27, MDFAT start @0x24,
/// cluster index base @0x2D, root-dir offset @0x29; sectors/cluster @0x0D = 64;
/// version_flag @0x33 = 3. The inner FAT16 volume lives at the base sector; file
/// data clusters are located through the 5-byte DRVSP3 MDFAT (102 entries per
/// sector + 2 pad bytes). Stored clusters (MDFAT flags = used + uncompressed)
/// are read verbatim; the inner directory's file size truncates the tail.
/// </para>
/// </summary>
public sealed class GenuineDvr3Reader : IDisposable {

  private const int Ss = 512;

  private readonly byte[] _data;
  private readonly List<DriveSpace3Entry> _entries = [];

  private int _spc;
  private int _innerBase;
  private int _mdfatStart;
  private int _sDcluster;
  private int _fatStart;
  private int _rootStart;
  private int _rootEntries;

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<DriveSpace3Entry> Entries => this._entries;

  /// <summary>The inner volume label (0x08 root entry), or "" when none was written.</summary>
  public string VolumeLabel { get; private set; } = "";

  /// <summary>
  /// Initializes a new instance of <see cref="GenuineDvr3Reader"/>.
  /// </summary>
  public GenuineDvr3Reader(Stream stream) {
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
      throw new InvalidDataException("DriveSpace3 (genuine): missing MSDBL6.0 signature.");
    if (this._data[0x33] != 3 && this._data[0x0D] <= 16)
      throw new InvalidDataException("DriveSpace3 (genuine): not a DriveSpace 3 CVF (version/cluster mismatch).");

    this._spc = this._data[0x0D];
    var resv = this.U16(0x0E);
    this._innerBase = this.U16(0x27);
    this._mdfatStart = this.U16(0x24) + 1;
    this._sDcluster = this.U16(0x2D);
    this._rootEntries = this.U16(0x11);
    var rootLog = this.U16(0x29);
    this._fatStart = this._innerBase + resv;
    this._rootStart = this._innerBase + rootLog;

    this.ReadRootDirectory();
  }

  private void ReadRootDirectory() {
    var rootOff = this._rootStart * Ss;
    for (var i = 0; i < this._rootEntries; i++) {
      var de = rootOff + i * 32;
      if (de + 32 > this._data.Length) break;
      var first = this._data[de];
      if (first == 0x00) break;            // end of directory
      if (first == 0xE5) continue;          // deleted
      var attr = this._data[de + 11];
      if (attr == 0x0F) continue;           // LFN slot
      if ((attr & 0x08) != 0) {             // volume label
        this.VolumeLabel = Encoding.ASCII.GetString(this._data, de, 11).TrimEnd(' ');
        continue;
      }
      if ((attr & 0x10) != 0) continue;     // directory (flat root only)

      var name = DecodeShortName(this._data, de);
      var firstCluster = this.U16(de + 26);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(de + 28));
      this._entries.Add(new DriveSpace3Entry {
        Name = name,
        Size = size,
        StartCluster = firstCluster,
        IsDirectory = false,
      });
    }
  }

  private static string DecodeShortName(byte[] data, int de) {
    var stem = Encoding.ASCII.GetString(data, de, 8).TrimEnd(' ');
    var ext = Encoding.ASCII.GetString(data, de + 8, 3).TrimEnd(' ');
    return ext.Length > 0 ? $"{stem}.{ext}" : stem;
  }

  // 5-byte DRVSP3 MDFAT entry position for a cluster.
  private int MdfatBytePos(int cluster) =>
    (this._sDcluster + cluster) * 5 + ((this._sDcluster + cluster) / 102) * 2 + Ss * this._mdfatStart;

  private ushort NextCluster(int cluster) =>
    BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(this._fatStart * Ss + cluster * 2));

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(DriveSpace3Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.Size == 0) return [];

    var output = new byte[entry.Size];
    var written = 0;
    var cluster = entry.StartCluster;
    var guard = 0;
    var maxClusters = this._data.Length / (this._spc * Ss) + 2;

    while (cluster >= 2 && cluster < 0xFFF8 && written < entry.Size && guard++ < maxClusters) {
      var p = this.MdfatBytePos(cluster);
      if (p + 5 > this._data.Length) break;

      var sm1 = this._data[p] | (this._data[p + 1] << 8) | (this._data[p + 2] << 16);
      var sizeLo = (this._data[p + 3] >> 2) & 0x3F;     // on-disk sectors - 1
      var flags = (this._data[p + 4] >> 6) & 3;          // bit0 uncompressed, bit1 used

      if ((flags & 2) != 0) {                            // used cluster
        var srcOff = (sm1 + 1) * Ss;
        var clusterBytes = this._spc * Ss;
        var want = Math.Min(clusterBytes, (int)entry.Size - written);
        if ((flags & 1) != 0) {                          // stored
          if (want > 0 && srcOff + want <= this._data.Length)
            Array.Copy(this._data, srcOff, output, written, want);
        } else {                                         // compressed
          var inLen = (sizeLo + 1) * Ss;
          if (srcOff + inLen <= this._data.Length) {
            var payload = new byte[inLen];
            Array.Copy(this._data, srcOff, payload, 0, inLen);
            var full = Compression.Registry.Cvf.CvfLzCodec.Decompress(payload, inLen, clusterBytes);
            Array.Copy(full, 0, output, written, want);
          }
        }
        written += want;
      }

      cluster = this.NextCluster(cluster);
    }

    return output;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
