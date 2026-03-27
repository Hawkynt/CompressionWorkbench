#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Wrapster;

/// <summary>
/// Creates Wrapster v2 files — data wrapped in fake MP3 frames.
/// </summary>
public sealed class WrapsterWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  public void WriteTo(Stream output) {
    using var bw = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);

    // Write fake MP3 frame header (MPEG1 Layer3 128kbps stereo 44100Hz)
    bw.Write((byte)0xFF);
    bw.Write((byte)0xFB);
    bw.Write((byte)0x90); // 128kbps
    bw.Write((byte)0x00);

    // Write "wrapster" signature
    bw.Write(Encoding.ASCII.GetBytes("wrapster"));

    // Total data size
    var totalSize = 0;
    foreach (var (_, data) in _files) totalSize += data.Length;
    bw.Write(totalSize);

    // File count
    bw.Write(_files.Count);

    // File entries (256-byte name + 4-byte size + 4-byte offset)
    var currentOffset = 0;
    foreach (var (name, data) in _files) {
      var nameBytes = new byte[256];
      var srcBytes = Encoding.ASCII.GetBytes(name);
      Array.Copy(srcBytes, nameBytes, Math.Min(srcBytes.Length, 255));
      bw.Write(nameBytes);
      bw.Write(data.Length);
      bw.Write(currentOffset);
      currentOffset += data.Length;
    }

    // File data
    foreach (var (_, data) in _files)
      bw.Write(data);
  }
}
