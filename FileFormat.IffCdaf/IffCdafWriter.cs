#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.IffCdaf;

/// <summary>
/// Creates IFF CDAF archives with FNAM+FDAT chunk pairs.
/// </summary>
public sealed class IffCdafWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  public void WriteTo(Stream output) {
    // Build chunks in memory first to calculate FORM size
    using var body = new MemoryStream();
    var buf4 = new byte[4];

    foreach (var (name, data) in _files) {
      // FNAM chunk
      var nameBytes = Encoding.ASCII.GetBytes(name + '\0');
      body.Write("FNAM"u8);
      BinaryPrimitives.WriteInt32BigEndian(buf4, nameBytes.Length);
      body.Write(buf4);
      body.Write(nameBytes);
      if ((nameBytes.Length & 1) != 0) body.WriteByte(0); // pad

      // FDAT chunk
      body.Write("FDAT"u8);
      BinaryPrimitives.WriteInt32BigEndian(buf4, data.Length);
      body.Write(buf4);
      body.Write(data);
      if ((data.Length & 1) != 0) body.WriteByte(0); // pad
    }

    var bodyBytes = body.ToArray();

    // FORM header
    output.Write("FORM"u8);
    BinaryPrimitives.WriteInt32BigEndian(buf4, 4 + bodyBytes.Length); // CDAF + chunks
    output.Write(buf4);
    output.Write("CDAF"u8);
    output.Write(bodyBytes);
  }
}
