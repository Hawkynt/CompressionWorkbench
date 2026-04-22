#pragma warning disable CS1591
using FileFormat.ResourceDll;

namespace Compression.Tests.ResourceDll;

[TestFixture]
public class ResourceDllTests {
  [Test]
  public void RoundTrip_SingleFile() {
    var w = new ResourceDllWriter();
    var payload = "hello resource dll"u8.ToArray();
    w.AddFile("greeting.txt", payload);

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var entries = new ResourceDllReader().Read(ms);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("greeting.txt"));
    Assert.That(entries[0].Data, Is.EqualTo(payload));
  }

  [Test]
  public void RoundTrip_MultipleFiles() {
    var w = new ResourceDllWriter();
    var files = new (string Name, byte[] Data)[] {
      ("a.bin", [0x01, 0x02, 0x03]),
      ("readme.txt", "hello"u8.ToArray()),
      ("config.json", "{ \"key\": 42 }"u8.ToArray()),
      ("empty-but-not-quite.dat", [0]),
    };
    foreach (var (n, d) in files) w.AddFile(n, d);

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var entries = new ResourceDllReader().Read(ms).OrderBy(e => e.Name).ToList();
    var expected = files.OrderBy(f => f.Name).ToList();
    Assert.That(entries, Has.Count.EqualTo(expected.Count));
    for (var i = 0; i < entries.Count; i++) {
      Assert.That(entries[i].Name, Is.EqualTo(expected[i].Name));
      Assert.That(entries[i].Data, Is.EqualTo(expected[i].Data));
    }
  }

  [Test]
  public void OutputIsValidPe() {
    var w = new ResourceDllWriter();
    w.AddFile("any.bin", [0xAA, 0xBB]);
    using var ms = new MemoryStream();
    w.WriteTo(ms);

    var bytes = ms.ToArray();
    Assert.That(bytes[0], Is.EqualTo((byte)'M'), "DOS magic byte 0");
    Assert.That(bytes[1], Is.EqualTo((byte)'Z'), "DOS magic byte 1");
    var peOff = BitConverter.ToUInt32(bytes, 0x3C);
    Assert.That(peOff, Is.GreaterThan(0u).And.LessThan((uint)bytes.Length - 4));
    Assert.That(bytes[peOff], Is.EqualTo((byte)'P'));
    Assert.That(bytes[peOff + 1], Is.EqualTo((byte)'E'));
    Assert.That(bytes[peOff + 2], Is.EqualTo(0));
    Assert.That(bytes[peOff + 3], Is.EqualTo(0));

    // PE32+ optional header magic
    var optMagic = BitConverter.ToUInt16(bytes, (int)peOff + 4 + 20);
    Assert.That(optMagic, Is.EqualTo(0x020B), "PE32+ optional header magic");

    // DLL characteristic bit (0x2000 in COFF Characteristics)
    var characteristics = BitConverter.ToUInt16(bytes, (int)peOff + 4 + 18);
    Assert.That(characteristics & 0x2000, Is.EqualTo(0x2000), "DLL flag");
  }

  [Test]
  public void EmptyArchive_RoundTrips() {
    var w = new ResourceDllWriter();
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var entries = new ResourceDllReader().Read(ms);
    Assert.That(entries, Is.Empty);
  }

  [Test]
  public void NameWithNonAsciiChars_RoundTrips() {
    var w = new ResourceDllWriter();
    w.AddFile("résumé-π.txt", "héllo"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var entries = new ResourceDllReader().Read(ms);
    Assert.That(entries[0].Name, Is.EqualTo("résumé-π.txt"));
  }

  [Test]
  public void LargePayload_RoundTrips() {
    // Force the section past one file-alignment block (>512 bytes per resource).
    var w = new ResourceDllWriter();
    var rnd = new Random(42);
    var data1 = new byte[1500];
    rnd.NextBytes(data1);
    var data2 = new byte[7000];
    rnd.NextBytes(data2);
    w.AddFile("first.bin", data1);
    w.AddFile("second.bin", data2);

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var entries = new ResourceDllReader().Read(ms).OrderBy(e => e.Name).ToList();
    Assert.That(entries[0].Data, Is.EqualTo(data1));
    Assert.That(entries[1].Data, Is.EqualTo(data2));
  }
}
