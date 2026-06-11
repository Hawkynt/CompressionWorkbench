using System.Buffers.Binary;
using System.Text;
using FileFormat.Par2;

namespace Compression.Tests.Par2;

[TestFixture]
public class Par2Tests {

  private static readonly byte[] Magic = "PAR2\0PKT"u8.ToArray();

  // Builds one PAR2 packet: 8-byte magic + u64 length + 16-byte md5 + 16-byte setId
  // + 16-byte type + body. Length covers the whole packet and is a multiple of 4.
  private static byte[] BuildPacket(byte[] setId, string typeSuffix, byte[] body) {
    var type = new byte[16];
    var prefix = "PAR 2.0\0"u8;
    prefix.CopyTo(type);
    var suffixBytes = Encoding.ASCII.GetBytes(typeSuffix);
    Array.Copy(suffixBytes, 0, type, 8, Math.Min(suffixBytes.Length, 8));

    // Pad body to multiple of 4 so total length stays a multiple of 4.
    var pad = (4 - (body.Length % 4)) % 4;
    var paddedBody = new byte[body.Length + pad];
    Array.Copy(body, paddedBody, body.Length);

    var total = 64 + paddedBody.Length;
    var packet = new byte[total];
    Magic.CopyTo(packet, 0);
    BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8, 8), (ulong)total);
    // md5 (16) left zero for the synthetic sample.
    Array.Copy(setId, 0, packet, 32, 16);
    Array.Copy(type, 0, packet, 48, 16);
    Array.Copy(paddedBody, 0, packet, 64, paddedBody.Length);
    return packet;
  }

  private static byte[] BuildFileDescBody(string name, ulong length) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    // 16 file-id + 16 md5 + 16 md5-16k + 8 length + name.
    var body = new byte[56 + nameBytes.Length];
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(48, 8), length);
    Array.Copy(nameBytes, 0, body, 56, nameBytes.Length);
    return body;
  }

  private static byte[] BuildSyntheticPar2() {
    var setId = new byte[16];
    for (var i = 0; i < 16; ++i) setId[i] = (byte)(i + 1);

    var mainBody = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(mainBody.AsSpan(0, 8), 65536); // block size

    using var ms = new MemoryStream();
    var main = BuildPacket(setId, "Main", mainBody);
    var fd1 = BuildPacket(setId, "FileDesc", BuildFileDescBody("hello.txt", 1234));
    var fd2 = BuildPacket(setId, "FileDesc", BuildFileDescBody("world.bin", 9999));
    var recv = BuildPacket(setId, "RecvSlic", new byte[32]);
    ms.Write(main); ms.Write(fd1); ms.Write(fd2); ms.Write(recv);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new Par2FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Par2"));
    Assert.That(d.Extensions, Contains.Item(".par2"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataFilesAndPackets() {
    var img = BuildSyntheticPar2();
    var d = new Par2FormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.par2"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "files.ini"), Is.True);
    Assert.That(entries.Count(e => e.Name.StartsWith("packets/")), Is.EqualTo(4));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullByteIdenticalAndParsesFiles() {
    var img = BuildSyntheticPar2();
    var d = new Par2FormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "par2_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);

      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.par2"));
      Assert.That(full, Is.EqualTo(img));

      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("packet_count=4"));
      Assert.That(meta, Does.Contain("protected_file_count=2"));
      Assert.That(meta, Does.Contain("block_size=65536"));
      Assert.That(meta, Does.Contain("parse_status=ok"));

      var files = File.ReadAllText(Path.Combine(dir, "files.ini"));
      Assert.That(files, Does.Contain("name=hello.txt"));
      Assert.That(files, Does.Contain("length=1234"));
      Assert.That(files, Does.Contain("name=world.bin"));
      Assert.That(files, Does.Contain("length=9999"));

      Assert.That(Directory.GetFiles(Path.Combine(dir, "packets")), Has.Length.EqualTo(4));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = new byte[200];
    Array.Fill(garbage, (byte)0x55);
    var d = new Par2FormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "par2_bad_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(garbage);
      Assert.DoesNotThrow(() => d.List(ms, null));
      ms.Position = 0;
      Assert.DoesNotThrow(() => d.Extract(ms, dir, null, null));
      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.par2"));
      Assert.That(full, Is.EqualTo(garbage));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
