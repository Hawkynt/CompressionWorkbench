#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Ampk;

/// <summary>
/// Creates AMPK archives with stored (uncompressed) files.
/// </summary>
public sealed class AmpkWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  public void WriteTo(Stream output) {
    // Magic
    output.Write("AMPK"u8);

    // File count (BE)
    var buf = new byte[4];
    BinaryPrimitives.WriteInt32BigEndian(buf, _files.Count);
    output.Write(buf);

    foreach (var (name, data) in _files) {
      var nameBytes = Encoding.ASCII.GetBytes(name);

      // Name length (BE)
      BinaryPrimitives.WriteInt32BigEndian(buf, nameBytes.Length);
      output.Write(buf);

      // Name
      output.Write(nameBytes);

      // Original size (BE)
      BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
      output.Write(buf);

      // Compressed size = same (stored)
      BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
      output.Write(buf);

      // Data
      output.Write(data);
    }
  }
}
