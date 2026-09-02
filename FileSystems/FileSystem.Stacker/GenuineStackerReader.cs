#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Stacker;

/// <summary>
/// Reads a <b>genuine</b> Stac Electronics STACVOL — the real obfuscated-SCB
/// layout that <see cref="GenuineStackerWriter"/> emits and that the independent
/// <c>dmsdos</c> driver mounts. This is the read half of the genuine Stacker
/// round trip; together with the driver-proven writer it gives a full read/write
/// path over the genuine on-disk format.
/// <para>
/// The superblock at sector 0 is decoded with the Stacker rolling-XOR cipher
/// (seed at 0x4c) to recover the geometry (version 0x60, sector size 0x62, total
/// sectors 0x6C, emulated-boot-block 0x70, AMAP start 0x74, FAT start 0x76, data
/// start 0x7a). The emulated boot block supplies the BPB; files are walked
/// through the inner FAT and located through the interleaved AMAP (stored
/// clusters read verbatim, tail truncated by the directory's file size).
/// </para>
/// </summary>
public sealed class GenuineStackerReader : IDisposable {

  private const int Ss = 512;

  private readonly byte[] _data;
  private readonly List<StackerEntry> _entries = [];

  private int _spc;
  private int _fatStart;
  private int _rootStart;
  private int _rootEntries;
  private int _maxCluster;
  private bool _fat16;

  /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<StackerEntry> Entries => this._entries;
  /// <summary>
  /// Gets or sets the version.
  /// </summary>
public int Version { get; private set; }

  /// <summary>The inner volume label (0x08 root entry), or "" when none was written.</summary>
  public string VolumeLabel { get; private set; } = "";

  /// <summary>
  /// Initializes a new instance of <see cref="GenuineStackerReader"/>.
  /// </summary>
public GenuineStackerReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    if (stream.CanSeek) stream.Position = 0;
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

  private static int Rol1(int x) => ((x << 1) | (x >> 7)) & 0xff;

  private void Parse() {
    if (this._data.Length < 2 * Ss || Encoding.ASCII.GetString(this._data, 0, 7) != "STACKER")
      throw new InvalidDataException("Stacker (genuine): missing STACKER signature.");

    // Decode the obfuscated superblock (0x50..0x7f) into a local copy.
    var buf = new byte[Ss];
    Array.Copy(this._data, buf, Ss);
    var key = (int)buf[0x4C];
    for (var i = 0; i < 0x30; i++) {
      var t = Rol1((0xc4 - key) & 0xff);
      var enc = buf[0x50 + i];
      buf[0x50 + i] = (byte)(t ^ enc);
      key = enc;
    }
    if (buf[0x4E] != 0x0A || buf[0x4F] != 0x1A)
      throw new InvalidDataException("Stacker (genuine): missing 0x1A0A signature.");

    static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));

    this.Version = U16(buf, 0x60);
    var totalSects = (int)U32(buf, 0x6C);
    var bootBlock = U16(buf, 0x70);
    this._fatStart = U16(buf, 0x76);

    var bb = bootBlock * Ss;
    if (bb + 0x20 > this._data.Length) throw new InvalidDataException("Stacker (genuine): boot block out of range.");
    this._spc = this._data[bb + 0x0D];
    var reserv = U16(this._data, bb + 0x0E);
    var fatCnt = this._data[bb + 0x10];
    this._rootEntries = U16(this._data, bb + 0x11);
    var fatSize = U16(this._data, bb + 0x16);
    if (this._spc <= 0) throw new InvalidDataException("Stacker (genuine): invalid sectors/cluster.");

    var firstRoot = this._fatStart + 3 * fatCnt * fatSize;
    var rootSecs = (this._rootEntries * 32 + Ss - 1) / Ss;
    this._rootStart = firstRoot;
    var bbFirstData = rootSecs + fatCnt * fatSize + reserv;
    var clustCnt = (totalSects - bbFirstData) / this._spc;
    this._fat16 = clustCnt >= 0xFED;
    this._maxCluster = clustCnt + 1;

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
      if ((attr & 0x08) != 0 && attr != 0x0F) {  // volume label
        this.VolumeLabel = Encoding.ASCII.GetString(this._data, de, 11).TrimEnd(' ');
        continue;
      }
      if (attr == 0x0F || (attr & 0x10) != 0) continue;

      var stem = Encoding.ASCII.GetString(this._data, de, 8).TrimEnd(' ');
      var ext = Encoding.ASCII.GetString(this._data, de + 8, 3).TrimEnd(' ');
      var name = ext.Length > 0 ? $"{stem}.{ext}" : stem;
      var firstCluster = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(de + 26));
      var size = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(de + 28));
      this._entries.Add(new StackerEntry {
        Name = name, Size = size, FirstCluster = firstCluster, IsDirectory = false,
      });
    }
  }

  // Physical sector (1-based) + on-disk sector count of a cluster, from the
  // interleaved AMAP. Sectors == s_spc ⇒ stored; fewer ⇒ DS-compressed.
  private (int Phys, int Sectors) AmapEntry(int cluster) {
    var bytesPer = this._fat16 ? 4 : 3;
    var pos = cluster * bytesPer;
    var area = pos / Ss;
    var amapSector = (area / 6) * 9 + (area % 6) + 3 + this._fatStart;
    var p = amapSector * Ss + (pos % Ss);
    if (p + bytesPer > this._data.Length) return (0, 0);
    var sec = this._data[p] | (this._data[p + 1] << 8);
    var sizeLo = this._data[p + 2] & 0x0f;
    if (this._fat16) {
      sec |= (this._data[p + 3] & 0x3f) << 16;
      sizeLo += (this._data[p + 3] >> 2) & 0x30;
    }
    return (sec, sizeLo + 1);
  }

  private int NextCluster(int cluster) {
    var fatOff = this._fatStart * Ss;
    if (this._fat16)
      return BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(fatOff + cluster * 2));
    var o = fatOff + cluster * 3 / 2;
    var pair = this._data[o] | (this._data[o + 1] << 8);
    return (cluster & 1) == 0 ? pair & 0xFFF : (pair >> 4) & 0xFFF;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(StackerEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.Size == 0) return [];

    var output = new byte[entry.Size];
    var written = 0;
    var cluster = entry.FirstCluster;
    var eoc = this._fat16 ? 0xFFF8 : 0xFF8;
    var guard = 0;

    while (cluster >= 2 && cluster < eoc && written < entry.Size && guard++ <= this._maxCluster + 2) {
      var (phys, sectors) = this.AmapEntry(cluster);
      if (phys > 0) {
        var srcOff = phys * Ss;
        var clusterBytes = this._spc * Ss;
        var want = Math.Min(clusterBytes, (int)entry.Size - written);
        if (sectors >= this._spc) {                       // stored
          if (want > 0 && srcOff + want <= this._data.Length)
            Array.Copy(this._data, srcOff, output, written, want);
        } else {                                          // DS-compressed
          var inLen = sectors * Ss;
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
