#pragma warning disable CS1591
using System.Text;
using FileSystem.Stacker;

namespace Compression.Tests.Stacker;

/// <summary>
/// Proves the <see cref="StackerWriter"/> -> <see cref="StackerReader"/> path:
/// files written into a STACVOL extract back byte-exact, covering STORED
/// (incompressible) and Stac-LZS (compressible) cluster payloads, multi-cluster
/// chains, and several files in one volume.
/// </summary>
[TestFixture]
public class StackerRoundTripTests {

  private static byte[] WriteAndExtract(string name, byte[] data, bool compress) {
    var w = new StackerWriter { Compress = compress, SectorsPerCluster = 2 };
    w.AddFile(name, data);
    var img = w.Build();
    using var r = new StackerReader(new MemoryStream(img));
    var entry = r.Entries.First(e => e.Name == name.ToUpperInvariant());
    return r.Extract(entry);
  }

  [Test, Category("HappyPath")]
  public void Stored_SmallFile_RoundTripsExact() {
    var data = Encoding.ASCII.GetBytes("The quick brown fox.");
    var got = WriteAndExtract("FOX.TXT", data, compress: false);
    Assert.That(got, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Compressible_File_RoundTripsExact() {
    var data = Encoding.ASCII.GetBytes(new string('A', 4000) + new string('B', 4000));
    var got = WriteAndExtract("BIG.TXT", data, compress: true);
    Assert.That(got, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Incompressible_File_StoredAndRoundTripsExact() {
    var rng = new Random(1234);
    var data = new byte[5000];
    rng.NextBytes(data);
    var got = WriteAndExtract("RAND.BIN", data, compress: true);
    Assert.That(got, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void MultipleFiles_AllRoundTrip() {
    var a = Encoding.ASCII.GetBytes("alpha file contents " + new string('x', 2000));
    var b = new byte[3333];
    new Random(7).NextBytes(b);
    var c = Encoding.ASCII.GetBytes("gamma");

    var w = new StackerWriter { Compress = true, SectorsPerCluster = 4 };
    w.AddFile("ALPHA.TXT", a);
    w.AddFile("BETA.BIN", b);
    w.AddFile("GAMMA.DAT", c);
    var img = w.Build();

    using var r = new StackerReader(new MemoryStream(img));
    byte[] Get(string n) => r.Extract(r.Entries.First(e => e.Name == n));
    Assert.Multiple(() => {
      Assert.That(Get("ALPHA.TXT"), Is.EqualTo(a));
      Assert.That(Get("BETA.BIN"), Is.EqualTo(b));
      Assert.That(Get("GAMMA.DAT"), Is.EqualTo(c));
    });
  }

  [Test, Category("HappyPath")]
  public void EmptyVolume_ListsInnerLabel() {
    var img = new StackerWriter().Build();
    using var r = new StackerReader(new MemoryStream(img));
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Entries, Has.Some.Matches<StackerEntry>(e => e.IsDirectory));
  }
}
