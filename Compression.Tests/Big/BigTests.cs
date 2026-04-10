namespace Compression.Tests.Big;

[TestFixture]
public class BigTests {

  private static MemoryStream BuildBig(params (string path, byte[] data)[] files) {
    var ms = new MemoryStream();
    var w = new FileFormat.Big.BigWriter(ms, leaveOpen: true);
    foreach (var (path, data) in files)
      w.AddFile(path, data);
    w.Finish();
    ms.Position = 0;
    return ms;
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello BIG archive!"u8.ToArray();

    using var ms = BuildBig(("hello.txt", data));
    var r = new FileFormat.Big.BigReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Path, Is.EqualTo("hello.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));

    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var file1 = "First file content"u8.ToArray();
    var file2 = "Second file content"u8.ToArray();
    var file3 = "Third file content"u8.ToArray();

    using var ms = BuildBig(
      ("a.dat", file1),
      ("b.dat", file2),
      ("c.dat", file3));
    var r = new FileFormat.Big.BigReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(file1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(file2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(file3));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_NestedPaths() {
    var wallTex = new byte[256];
    Array.Fill(wallTex, (byte)0xAB);
    var floorTex = new byte[128];
    Array.Fill(floorTex, (byte)0xCD);

    using var ms = BuildBig(
      ("data/textures/wall.bmp", wallTex),
      ("data/textures/floor.bmp", floorTex),
      ("data/sounds/shoot.wav", "RIFFWAVE"u8.ToArray()));
    var r = new FileFormat.Big.BigReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Path, Is.EqualTo("data/textures/wall.bmp"));
    Assert.That(r.Entries[1].Path, Is.EqualTo("data/textures/floor.bmp"));
    Assert.That(r.Entries[2].Path, Is.EqualTo("data/sounds/shoot.wav"));

    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(wallTex));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(floorTex));
  }

  [Test, Category("Format")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Big.BigFormatDescriptor();

    Assert.That(desc.Id, Is.EqualTo("Big"));
    Assert.That(desc.DisplayName, Is.EqualTo("BIG"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".big"));
    Assert.That(desc.Extensions, Contains.Item(".big"));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("BIGF"u8.ToArray()));
    Assert.That(desc.MagicSignatures[1].Bytes, Is.EqualTo("BIG4"u8.ToArray()));
    Assert.That(desc.Description, Is.EqualTo("EA Games resource archive"));
  }

  [Test, Category("Validation")]
  public void BadMagic_Throws() {
    var buf = new byte[64];
    Array.Fill(buf, (byte)0xFF);
    // Write a plausible but wrong magic
    buf[0] = (byte)'Z'; buf[1] = (byte)'I'; buf[2] = (byte)'P'; buf[3] = (byte)' ';

    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Big.BigReader(ms));
  }

  [Test, Category("Validation")]
  public void TooSmall_Throws() {
    var buf = new byte[8]; // Less than the 16-byte minimum header
    buf[0] = (byte)'B'; buf[1] = (byte)'I'; buf[2] = (byte)'G'; buf[3] = (byte)'F';

    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Big.BigReader(ms));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_EmptyFile() {
    var empty = Array.Empty<byte>();

    using var ms = BuildBig(("empty.bin", empty));
    var r = new FileFormat.Big.BigReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(0));
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_And_List() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, "EA game data"u8.ToArray());
      var desc = new FileFormat.Big.BigFormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "gamedata/config.ini", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("gamedata/config.ini")); // FilesOnly preserves full archive path
    } finally {
      File.Delete(tmpFile);
    }
  }

  [Test, Category("Format")]
  public void BigF_Magic_Written() {
    using var ms = BuildBig(("x.bin", new byte[4]));
    var header = new byte[4];
    ms.Position = 0;
    ms.ReadExactly(header);
    Assert.That(header, Is.EqualTo("BIGF"u8.ToArray()));
  }

  [Test, Category("Format")]
  public void TotalSize_Field_Correct() {
    var data = new byte[100];
    Array.Fill(data, (byte)0x55);
    using var ms = BuildBig(("file.bin", data));
    var allBytes = ms.ToArray();

    // totalSize is 4 bytes at offset 4, big-endian
    var totalSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(allBytes.AsSpan(4, 4));
    Assert.That(totalSize, Is.EqualTo((uint)allBytes.Length));
  }
}
