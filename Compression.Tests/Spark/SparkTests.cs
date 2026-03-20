namespace Compression.Tests.Spark;

[TestFixture]
public class SparkTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello, Spark/RISC OS!"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Spark.SparkWriter(ms, leaveOpen: true))
      w.AddFile("TestFile", data, new DateTime(1995, 3, 14));
    ms.Position = 0;

    var r = new FileFormat.Spark.SparkReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].FileName, Is.EqualTo("TestFile"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var data1 = new byte[256];
    var data2 = new byte[128];
    Random.Shared.NextBytes(data1);
    Random.Shared.NextBytes(data2);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Spark.SparkWriter(ms, leaveOpen: true)) {
      w.AddFile("File1", data1);
      w.AddFile("File2", data2);
    }
    ms.Position = 0;

    var r = new FileFormat.Spark.SparkReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_Directory() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Spark.SparkWriter(ms, leaveOpen: true)) {
      w.BeginDirectory("mydir");
      w.AddFile("inside", "content"u8.ToArray());
      w.EndDirectory();
    }
    ms.Position = 0;

    var r = new FileFormat.Spark.SparkReader(ms);
    Assert.That(r.Entries.Any(e => e.IsDirectory), Is.True);
    var file = r.Entries.First(e => !e.IsDirectory);
    Assert.That(r.Extract(file), Is.EqualTo("content"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void EntryMarker_Is0x1A() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Spark.SparkWriter(ms, leaveOpen: true))
      w.AddFile("x", [1]);
    ms.Position = 0;
    Assert.That(ms.ReadByte(), Is.EqualTo(0x1A));
  }
}
