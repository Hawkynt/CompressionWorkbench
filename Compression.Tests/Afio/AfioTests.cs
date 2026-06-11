using System.IO.Compression;
using System.Text;
using FileFormat.Afio;

namespace Compression.Tests.Afio;

[TestFixture]
public class AfioTests {

  // Writes one portable-ASCII (070707) cpio/afio member.
  private static void WriteMember(Stream s, string name, byte[] data, int mode) {
    var nameWithNul = name + "\0";
    var nameBytes = Encoding.ASCII.GetBytes(nameWithNul);
    var sb = new StringBuilder();
    sb.Append("070707");                    // magic
    sb.Append("000000");                    // dev
    sb.Append("000001");                    // ino
    sb.Append(Convert.ToString(mode, 8).PadLeft(6, '0')); // mode
    sb.Append("000000");                    // uid
    sb.Append("000000");                    // gid
    sb.Append("000001");                    // nlink
    sb.Append("000000");                    // rdev
    sb.Append("00000000000");               // mtime (11)
    sb.Append(Convert.ToString(nameBytes.Length, 8).PadLeft(6, '0')); // namesize (6)
    sb.Append(Convert.ToString(data.Length, 8).PadLeft(11, '0'));     // filesize (11)
    var header = Encoding.ASCII.GetBytes(sb.ToString());
    s.Write(header, 0, header.Length);
    s.Write(nameBytes, 0, nameBytes.Length);
    s.Write(data, 0, data.Length);
  }

  private static byte[] Gzip(byte[] data) {
    using var ms = new MemoryStream();
    using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
      gz.Write(data, 0, data.Length);
    return ms.ToArray();
  }

  private static readonly byte[] StoredPayload = "hello afio"u8.ToArray();
  private static readonly byte[] CompressedOriginal = Encoding.ASCII.GetBytes(new string('Z', 200));

  private static byte[] BuildSyntheticAfio() {
    using var ms = new MemoryStream();
    WriteMember(ms, "plain.txt", StoredPayload, 0x81A4); // 0100644
    WriteMember(ms, "packed.bin", Gzip(CompressedOriginal), 0x81A4);
    WriteMember(ms, "subdir", [], 0x41ED);               // 0040755 directory
    WriteMember(ms, "TRAILER!!!", [], 0);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new AfioFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Afio"));
    Assert.That(d.Extensions, Contains.Item(".afio"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void List_SurfacesMembersWithDecompressedSizes() {
    var img = BuildSyntheticAfio();
    var d = new AfioFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);

    var plain = entries.Single(e => e.Name == "plain.txt");
    Assert.That(plain.OriginalSize, Is.EqualTo(StoredPayload.Length));
    Assert.That(plain.Method, Is.EqualTo("Stored"));

    var packed = entries.Single(e => e.Name == "packed.bin");
    Assert.That(packed.Method, Is.EqualTo("Gzip"));
    Assert.That(packed.OriginalSize, Is.EqualTo(CompressedOriginal.Length));

    var dir = entries.Single(e => e.Name == "subdir");
    Assert.That(dir.IsDirectory, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_DecompressesGzipMembers() {
    var img = BuildSyntheticAfio();
    var d = new AfioFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "afio_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);

      var plain = File.ReadAllBytes(Path.Combine(dir, "plain.txt"));
      Assert.That(plain, Is.EqualTo(StoredPayload));

      var packed = File.ReadAllBytes(Path.Combine(dir, "packed.bin"));
      Assert.That(packed, Is.EqualTo(CompressedOriginal));

      Assert.That(Directory.Exists(Path.Combine(dir, "subdir")), Is.True);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_FilterSelectsSingleMember() {
    var img = BuildSyntheticAfio();
    var d = new AfioFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "afio_f_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, ["plain.txt"]);
      Assert.That(File.Exists(Path.Combine(dir, "plain.txt")), Is.True);
      Assert.That(File.Exists(Path.Combine(dir, "packed.bin")), Is.False);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = new byte[120];
    Array.Fill(garbage, (byte)0x44);
    var d = new AfioFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "afio_bad_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(garbage);
      Assert.DoesNotThrow(() => d.List(ms, null));
      ms.Position = 0;
      Assert.DoesNotThrow(() => d.Extract(ms, dir, null, null));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
