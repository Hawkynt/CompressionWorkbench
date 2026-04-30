namespace Compression.Tests.Pfs0;

[TestFixture]
public class Pfs0Tests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Switch NCA payload"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Pfs0.Pfs0Writer(ms, leaveOpen: true))
      w.AddEntry("main.nca", data);
    ms.Position = 0;

    var r = new FileFormat.Pfs0.Pfs0Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("main.nca"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    // Provide names out-of-order so we can confirm the writer sorts them alphabetically.
    var dataZ = new byte[64];
    var dataA = new byte[128];
    var dataM = new byte[32];
    Array.Fill(dataZ, (byte)0x11);
    Array.Fill(dataA, (byte)0x22);
    Array.Fill(dataM, (byte)0x33);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Pfs0.Pfs0Writer(ms, leaveOpen: true)) {
      w.AddEntry("zoo.nca", dataZ);
      w.AddEntry("alpha.nca", dataA);
      w.AddEntry("middle.nca", dataM);
    }
    ms.Position = 0;

    var r = new FileFormat.Pfs0.Pfs0Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("alpha.nca"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("middle.nca"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("zoo.nca"));

    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(dataA));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(dataM));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(dataZ));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[1024 * 1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i & 0xFF);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Pfs0.Pfs0Writer(ms, leaveOpen: true))
      w.AddEntry("big.nca", data);
    ms.Position = 0;

    var r = new FileFormat.Pfs0.Pfs0Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[Pfs0Constants_HeaderSize];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Pfs0.Pfs0Reader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsHfs0() {
    // Synth a header with HFS0 magic — the rest can be zeros.
    var buf = new byte[Pfs0Constants_HeaderSize];
    buf[0] = (byte)'H';
    buf[1] = (byte)'F';
    buf[2] = (byte)'S';
    buf[3] = (byte)'0';
    using var ms = new MemoryStream(buf);
    var ex = Assert.Throws<NotSupportedException>(() => _ = new FileFormat.Pfs0.Pfs0Reader(ms));
    Assert.That(ex!.Message, Does.Contain("HFS0"));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsPfs0Bytes() {
    var d = new FileFormat.Pfs0.Pfs0FormatDescriptor();
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x50, 0x46, 0x53, 0x30 }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Pfs0.Pfs0FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Pfs0"));
    Assert.That(d.DisplayName, Is.EqualTo("Nintendo PartitionFS"));
    Assert.That(d.Extensions, Contains.Item(".nsp"));
    Assert.That(d.Extensions, Contains.Item(".pfs0"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".nsp"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("PFS0"u8.ToArray()));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("pfs0"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("PFS0"));
  }

  // The header size is internal to FileFormat.Pfs0; mirror it here for the malformed-input tests.
  private const int Pfs0Constants_HeaderSize = 16;
}
