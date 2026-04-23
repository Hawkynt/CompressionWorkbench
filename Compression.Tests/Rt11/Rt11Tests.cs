using System.Text;
using Compression.Registry;
using FileSystem.Rt11;

namespace Compression.Tests.Rt11;

[TestFixture]
public class Rt11Tests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void BuildRead_SingleFile_RoundTrips() {
    var body = Encoding.ASCII.GetBytes("HELLO RT11");
    var image = Rt11Writer.Build([("HELLO.TXT", body)]);

    var v = Rt11Reader.Read(image);
    Assert.That(v.Files, Has.Count.EqualTo(1));
    Assert.That(v.Files[0].Name, Is.EqualTo("HELLO.TXT"));
    Assert.That(v.Files[0].NameStem, Is.EqualTo("HELLO"));
    Assert.That(v.Files[0].Extension, Is.EqualTo("TXT"));

    var extracted = Rt11Reader.Extract(v, v.Files[0]);
    // Files round to whole 512-byte blocks; the prefix matches.
    Assert.That(extracted.AsSpan(0, body.Length).ToArray(), Is.EqualTo(body).AsCollection);
    Assert.That(extracted.Length, Is.EqualTo(512));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void BuildRead_MultiFile_RoundTrips() {
    var files = new (string, byte[])[] {
      ("FILE1.DAT", new byte[300]),
      ("FILE2.BIN", Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray()),
      ("X.SAV", Encoding.ASCII.GetBytes("save state")),
    };
    var image = Rt11Writer.Build(files);
    var v = Rt11Reader.Read(image);

    Assert.That(v.Files, Has.Count.EqualTo(3));
    Assert.That(v.Files.Select(f => f.Name),
      Is.EquivalentTo(new[] { "FILE1.DAT", "FILE2.BIN", "X.SAV" }));

    // FILE2.BIN is 1024 bytes — exactly 2 blocks → byte-exact round-trip.
    var f2 = v.Files.Single(f => f.Name == "FILE2.BIN");
    var b = Rt11Reader.Extract(v, f2);
    Assert.That(b.Length, Is.EqualTo(1024));
    Assert.That(b, Is.EqualTo(files[1].Item2).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void Read_HomeBlockSignature_IsPresent() {
    var image = Rt11Writer.Build([("A.X", new byte[1])]);
    var sig = Encoding.ASCII.GetString(image, 1 * 512 + 0x1F0, 12);
    Assert.That(sig, Is.EqualTo("DECRT11A    "));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsThroughReader() {
    var tmp1 = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName().Replace(".", "") + ".dat");
    var tmp2 = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName().Replace(".", "") + ".bin");
    File.WriteAllBytes(tmp1, Encoding.ASCII.GetBytes("alpha"));
    File.WriteAllBytes(tmp2, Encoding.ASCII.GetBytes("beta"));
    try {
      var desc = new Rt11FormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [
        new ArchiveInputInfo(tmp1, "FILEA.DAT", false),
        new ArchiveInputInfo(tmp2, "FILEB.BIN", false),
      ], new FormatCreateOptions());
      ms.Position = 0;
      var listed = desc.List(ms, null);
      Assert.That(listed, Has.Count.EqualTo(2));
      Assert.That(listed.Select(e => e.Name), Is.EquivalentTo(new[] { "FILEA.DAT", "FILEB.BIN" }));
    } finally {
      File.Delete(tmp1);
      File.Delete(tmp2);
    }
  }

  [Test, Category("EdgeCase")]
  public void CanAccept_RejectsLongStem() {
    var desc = new Rt11FormatDescriptor();
    var ok = desc.CanAccept(new ArchiveInputInfo("/tmp/a", "TOOLONGSTEM.X", false), out var reason);
    Assert.That(ok, Is.False);
    Assert.That(reason, Does.Contain("6 characters"));
  }

  [Test, Category("EdgeCase")]
  public void CanAccept_RejectsRad50UnsafeChars() {
    var desc = new Rt11FormatDescriptor();
    var ok = desc.CanAccept(new ArchiveInputInfo("/tmp/a", "FILE_X.DAT", false), out var reason);
    Assert.That(ok, Is.False);
    Assert.That(reason, Does.Contain("RAD-50"));
  }

  [Test, Category("EdgeCase")]
  public void Read_BadSignature_Throws() {
    var buf = new byte[512 * 10];
    Assert.That(() => Rt11Reader.Read(buf), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Build_RejectsRad50UnsafeFilename() {
    Assert.That(
      () => Rt11Writer.Build([("FILE_X.DAT", new byte[1])]),
      Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("RAD-50"));
  }

  [Test, Category("HappyPath")]
  public void Rad50_Encode_Decode_RoundTrips() {
    var (h, l) = Rad50.EncodeName6("HELLO");
    var stem = Rad50.DecodeName6(h, l);
    Assert.That(stem, Is.EqualTo("HELLO"));

    var t = Rad50.EncodeType3("TXT");
    Assert.That(Rad50.DecodeType3(t), Is.EqualTo("TXT"));

    var (h2, l2) = Rad50.EncodeName6("AB12.X");
    Assert.That(Rad50.DecodeName6(h2, l2), Is.EqualTo("AB12.X"));
  }
}
