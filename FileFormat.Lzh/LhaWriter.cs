using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzh;

namespace FileFormat.Lzh;

/// <summary>
/// Creates LHA/LZH archives.
/// </summary>
public sealed class LhaWriter {
  private readonly List<(string name, byte[] data)> _files = [];
  private readonly string _method;
  private readonly int _positionBits;

  /// <summary>
  /// Initializes a new <see cref="LhaWriter"/>.
  /// </summary>
  /// <param name="method">Compression method (default "-lh5-").</param>
  public LhaWriter(string method = LhaConstants.MethodLh5) {
    this._method = method;
    this._positionBits = method switch {
      LhaConstants.MethodLh0 => 0,
      LhaConstants.MethodLzs => -1, // sentinel: use LzsEncoder
      LhaConstants.MethodLz5 => -2, // sentinel: use Lz5Encoder
      LhaConstants.MethodLh1 => -3, // sentinel: use Lh1Encoder
      LhaConstants.MethodPm0 => -4, // sentinel: PMA store
      LhaConstants.MethodPm1 => -5, // sentinel: PMA PPMd order-2
      LhaConstants.MethodPm2 => -6, // sentinel: PMA PPMd order-3
      LhaConstants.MethodLh2 => LzhConstants.Lh5PositionBits, // uses lh5-compatible format
      LhaConstants.MethodLh3 => LzhConstants.Lh5PositionBits, // uses lh5-compatible format
      LhaConstants.MethodLh4 => LzhConstants.Lh4PositionBits,
      LhaConstants.MethodLh5 => LzhConstants.Lh5PositionBits,
      LhaConstants.MethodLh6 => LzhConstants.Lh6PositionBits,
      LhaConstants.MethodLh7 => LzhConstants.Lh7PositionBits,
      _ => LzhConstants.Lh5PositionBits
    };
  }

  /// <summary>
  /// Adds a file to the archive.
  /// </summary>
  /// <param name="name">The file name.</param>
  /// <param name="data">The file data.</param>
  public void AddFile(string name, byte[] data) {
    this._files.Add((name, data));
  }

  /// <summary>
  /// Writes the archive to a stream.
  /// </summary>
  /// <param name="output">The stream to write to.</param>
  public void WriteTo(Stream output) {
    foreach (var (name, data) in this._files)
      WriteEntry(output, name, data);
  }

  /// <summary>
  /// Creates an LHA archive as a byte array.
  /// </summary>
  /// <returns>The LHA archive bytes.</returns>
  public byte[] ToArray() {
    using var ms = new MemoryStream();
    this.WriteTo(ms);
    return ms.ToArray();
  }

  private void WriteEntry(Stream output, string name, byte[] data) {
    byte[] compressed;
    string method;

    if (this._method == LhaConstants.MethodLh0 || this._method == LhaConstants.MethodPm0 || data.Length == 0) {
      compressed = data;
      method = this._method == LhaConstants.MethodPm0 ? LhaConstants.MethodPm0 : LhaConstants.MethodLh0;
    } else if (this._positionBits == -5) {
      // -pm1- PMA PPMd order-2
      compressed = PmaEncoder.Encode(data, 2);
      method = this._method;
      if (compressed.Length >= data.Length) {
        compressed = data;
        method = LhaConstants.MethodPm0;
      }
    } else if (this._positionBits == -6) {
      // -pm2- PMA PPMd order-3
      compressed = PmaEncoder.Encode(data, 3);
      method = this._method;
      if (compressed.Length >= data.Length) {
        compressed = data;
        method = LhaConstants.MethodPm0;
      }
    } else if (this._positionBits == -1) {
      // -lzs- plain LZSS
      compressed = LzsEncoder.Encode(data);
      method = this._method;
      if (compressed.Length >= data.Length) {
        compressed = data;
        method = LhaConstants.MethodLh0;
      }
    } else if (this._positionBits == -2) {
      // -lz5- plain LZSS
      compressed = Lz5Encoder.Encode(data);
      method = this._method;
      if (compressed.Length >= data.Length) {
        compressed = data;
        method = LhaConstants.MethodLh0;
      }
    } else if (this._positionBits == -3) {
      // -lh1- adaptive Huffman
      compressed = Lh1Encoder.Encode(data);
      method = this._method;
      if (compressed.Length >= data.Length) {
        compressed = data;
        method = LhaConstants.MethodLh0;
      }
    } else {
      var encoder = new LzhEncoder(this._positionBits);
      compressed = encoder.Encode(data);
      method = this._method;

      // If compression didn't help, store instead
      if (compressed.Length >= data.Length) {
        compressed = data;
        method = LhaConstants.MethodLh0;
      }
    }

    var crc = Crc16.Compute(data);
    var nameBytes = Encoding.ASCII.GetBytes(name);

    // Write level 1 header
    // header_size = bytes from offset 2 through end of base header (including first ext-size field)
    // = 5(method) + 4(compressed) + 4(original) + 4(timestamp) + 1(reserved) + 1(level)
    //   + 1(nameLen) + nameLen + 2(crc) + 1(osId) + 2(nextExtSize=0)
    var headerPayloadSize = 5 + 4 + 4 + 4 + 1 + 1 + 1 + nameBytes.Length + 2 + 1 + 2;

    // Build header payload bytes for checksum computation
    using var headerMs = new MemoryStream();
    using var hw = new BinaryWriter(headerMs, Encoding.ASCII, leaveOpen: true);
    hw.Write(Encoding.ASCII.GetBytes(method)); // method (5 bytes)
    hw.Write((uint)compressed.Length); // compressed size
    hw.Write((uint)data.Length); // original size
    hw.Write(MsdosTimestamp(DateTime.Now)); // timestamp
    hw.Write((byte)0x20); // reserved
    hw.Write((byte)1); // header level = 1
    hw.Write((byte)nameBytes.Length); // name length
    hw.Write(nameBytes); // name
    hw.Write(crc); // CRC-16
    hw.Write((byte)0); // OS: generic
    hw.Write((ushort)0); // no extended headers
    hw.Flush();

    var headerPayload = headerMs.ToArray();
    byte checksum = 0;
    foreach (var b in headerPayload)
      checksum += b;

    using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
    writer.Write((byte)headerPayloadSize); // header size
    writer.Write(checksum); // checksum
    writer.Write(headerPayload);

    // Write compressed data
    writer.Write(compressed);
  }

  /// <summary>
  /// Creates an LHA archive split into multiple volumes.
  /// </summary>
  /// <param name="maxVolumeSize">Maximum size of each volume in bytes.</param>
  /// <param name="entries">The entries to add (name, data pairs).</param>
  /// <param name="method">Compression method (default "-lh5-").</param>
  /// <returns>An array of byte arrays, one per volume.</returns>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries,
      string method = LhaConstants.MethodLh5) {
    var writer = new LhaWriter(method);
    foreach (var (name, data) in entries)
      writer.AddFile(name, data);
    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(writer.ToArray(), maxVolumeSize);
  }

  private static uint MsdosTimestamp(DateTime dt) {
    var time = (dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2);
    var date = ((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day;
    return (uint)((date << 16) | time);
  }
}
