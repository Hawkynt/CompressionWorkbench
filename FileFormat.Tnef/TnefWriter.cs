#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Tnef;

/// <summary>
/// Creates MS-TNEF (winmail.dat) files with file attachments.
/// </summary>
public sealed class TnefWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  public void WriteTo(Stream output) {
    using var bw = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);

    // Signature + key
    bw.Write(TnefReader.TnefSignature);
    bw.Write((ushort)0); // key

    foreach (var (name, data) in _files) {
      // AttachRendData (marks new attachment) - 14 bytes of zeros
      WriteAttribute(bw, 0x02, 0x00019002, new byte[14]);

      // AttachTitle (filename, null-terminated)
      var nameBytes = Encoding.ASCII.GetBytes(name + '\0');
      WriteAttribute(bw, 0x02, 0x00018010, nameBytes);

      // AttachData
      WriteAttribute(bw, 0x02, 0x0001800F, data);
    }
  }

  private static void WriteAttribute(BinaryWriter bw, byte level, uint attrId, byte[] data) {
    bw.Write(level);
    bw.Write(attrId);
    bw.Write(data.Length);
    bw.Write(data);
    // Checksum (sum of all data bytes mod 65536)
    ushort checksum = 0;
    foreach (var b in data)
      checksum += b;
    bw.Write(checksum);
  }
}
