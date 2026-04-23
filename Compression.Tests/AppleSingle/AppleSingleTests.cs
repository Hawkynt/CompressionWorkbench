using System.Buffers.Binary;
using System.Text;
using FileFormat.AppleSingle;

namespace Compression.Tests.AppleSingle;

[TestFixture]
public class AppleSingleTests {

  // Build an AppleSingle file with: real_name (id=3) = "Foo.txt" and
  // data_fork (id=1) = "hello".
  private static byte[] BuildAs() {
    // Header: magic(4) + version(4) + filler(16) + nEntries(2) = 26 bytes, then
    // 2 entry descriptors (24 bytes), then the bodies.
    var realName = Encoding.ASCII.GetBytes("Foo.txt");
    var dataFork = Encoding.ASCII.GetBytes("hello");

    var entry1Offset = 26 + 2 * 12; // 50
    var entry2Offset = entry1Offset + realName.Length;
    var total = entry2Offset + dataFork.Length;

    var buf = new byte[total];
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0), AppleSingleReader.MagicSingle);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), 0x00020000);      // version 2
    // Filler 8..24 stays zero.
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(24), 2);               // 2 entries

    // Entry 1: real name
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(26), 3);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(30), (uint)entry1Offset);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(34), (uint)realName.Length);
    realName.CopyTo(buf.AsSpan(entry1Offset));

    // Entry 2: data fork
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(38), 1);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(42), (uint)entry2Offset);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(46), (uint)dataFork.Length);
    dataFork.CopyTo(buf.AsSpan(entry2Offset));

    return buf;
  }

  [Test, Category("HappyPath")]
  public void Read_ParsesEntryTable() {
    var c = AppleSingleReader.Read(BuildAs());
    Assert.That(c.IsDouble, Is.False);
    Assert.That(c.Entries, Has.Count.EqualTo(2));
    Assert.That(c.Entries[0].EntryId, Is.EqualTo(3u));
    Assert.That(c.Entries[0].Name, Is.EqualTo("real_name.txt"));
    Assert.That(c.Entries[1].EntryId, Is.EqualTo(1u));
    Assert.That(c.Entries[1].Name, Is.EqualTo("data_fork.bin"));
    Assert.That(Encoding.ASCII.GetString(c.Entries[1].Data), Is.EqualTo("hello"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_WritesFiles() {
    var data = BuildAs();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new AppleSingleFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "real_name.txt")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "data_fork.bin")), Is.True);
      Assert.That(File.ReadAllText(Path.Combine(tmp, "data_fork.bin")), Is.EqualTo("hello"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Read_BadMagic_Throws() {
    var buf = new byte[30];
    Assert.That(() => AppleSingleReader.Read(buf), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("HappyPath")]
  public void Read_DetectsAppleDoubleMagic() {
    var buf = new byte[30];
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0), AppleSingleReader.MagicDouble);
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(24), 0); // zero entries
    var c = AppleSingleReader.Read(buf);
    Assert.That(c.IsDouble, Is.True);
  }
}
