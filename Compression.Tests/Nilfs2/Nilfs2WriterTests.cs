#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Nilfs2;

namespace Compression.Tests.Nilfs2;

/// <summary>
/// Self-round-trip tests for the NILFS2 WORM writer. Validates that the emitted
/// image carries a spec-compliant superblock (so external NILFS2 sniffers see a
/// valid 0x3434 signature with rev_level &gt;= 2) and that the writer-private
/// directory + payload region round-trips through <see cref="Nilfs2Reader"/>.
/// Kernel mount is deliberately out of scope (would require the full DAT/IFile/
/// CPFile/SUFile + segment-log replay pipeline).
/// </summary>
[TestFixture]
public class Nilfs2WriterTests {

  [Test, Category("HappyPath")]
  public void Build_HasValidSuperblock() {
    var w = new Nilfs2Writer();
    w.AddFile("a.txt", "alpha"u8.ToArray());
    var img = w.Build();

    // Magic 0x3434 at offset 1024+6 = 1030.
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(1030, 2)), Is.EqualTo((ushort)0x3434));
    // rev_level == 2.
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(1024, 4)), Is.EqualTo(2u));
    // s_dev_size matches actual image length.
    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan(1024 + 0x20, 8)), Is.EqualTo((ulong)img.Length));
  }

  [Test, Category("HappyPath")]
  public void Build_HasWriterMagicAtSegmentStart() {
    var w = new Nilfs2Writer();
    w.AddFile("x", new byte[] { 1, 2, 3 });
    var img = w.Build();

    // Writer magic at SegmentStart = 2048, and nothing readable as text: a marker
    // that spells something names whoever chose the letters.
    var magic = img.AsSpan(2048, 8).ToArray();
    Assert.That(magic, Is.EqualTo(new byte[] { 0x8F, 0xD3, 0x1A, 0xE7, 0x05, 0xBC, 0x92, 0x14 }));
    Assert.That(magic, Has.None.InRange((byte)0x20, (byte)0x7E),
      "A marker made of printable characters is one a third party can read off the volume.");
  }

  [Test, Category("HappyPath")]
  public void Create_SingleFile_RoundTrips() {
    var d = new Nilfs2FormatDescriptor();
    var content = "Hello NILFS2!"u8.ToArray();

    var tmpDir = Path.Combine(Path.GetTempPath(), "nilfs2_w1_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var srcPath = Path.Combine(tmpDir, "hello.txt");
      File.WriteAllBytes(srcPath, content);

      using var output = new MemoryStream();
      d.Create(output, [new ArchiveInputInfo(srcPath, "hello.txt", false)], new FormatCreateOptions());

      using var read = new MemoryStream(output.ToArray());
      var names = d.List(read, null).Select(e => e.Name).ToHashSet();
      Assert.That(names, Does.Contain("FULL.nilfs2"));
      Assert.That(names, Does.Contain("metadata.ini"));
      Assert.That(names, Does.Contain("hello.txt"));

      var outDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(outDir);
      read.Position = 0;
      d.Extract(read, outDir, null, ["hello.txt"]);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "hello.txt")), Is.EqualTo(content));
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Create_MultipleFiles_AllRoundTrip() {
    var d = new Nilfs2FormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), "nilfs2_w2_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var files = new Dictionary<string, byte[]> {
        ["alpha.txt"] = Encoding.UTF8.GetBytes("alpha"),
        ["beta.bin"] = [0x00, 0x01, 0x02, 0xFE, 0xFF],
        ["docs/intro.md"] = Encoding.UTF8.GetBytes("# Intro\n"),
      };

      var inputs = new List<ArchiveInputInfo>();
      foreach (var (name, data) in files) {
        var p = Path.Combine(tmpDir, name.Replace('/', '_'));
        File.WriteAllBytes(p, data);
        inputs.Add(new ArchiveInputInfo(p, name, false));
      }

      using var output = new MemoryStream();
      d.Create(output, inputs, new FormatCreateOptions());

      var outDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(outDir);
      output.Position = 0;
      d.Extract(output, outDir, null, null);

      foreach (var (name, expected) in files) {
        var p = Path.Combine(outDir, name.Replace('/', Path.DirectorySeparatorChar));
        Assert.That(File.Exists(p), Is.True, $"{name} missing");
        Assert.That(File.ReadAllBytes(p), Is.EqualTo(expected), $"{name} content mismatch");
      }
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Create_EmptyFileList_StillValidSuperblock() {
    var d = new Nilfs2FormatDescriptor();
    using var output = new MemoryStream();
    d.Create(output, [], new FormatCreateOptions());
    var img = output.ToArray();

    Assert.That(img.Length, Is.GreaterThanOrEqualTo(64 * 1024));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(1030, 2)), Is.EqualTo((ushort)0x3434));

    // Reader should still produce the surface triage entries.
    using var read = new MemoryStream(img);
    var names = d.List(read, null).Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.nilfs2"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("superblock.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void Build_RejectsBadBlockSize() {
    var w = new Nilfs2Writer();
    Assert.Throws<ArgumentException>(() => w.Build(blockSize: 1234));
    Assert.Throws<ArgumentException>(() => w.Build(blockSize: 512));
    Assert.Throws<ArgumentException>(() => w.Build(blockSize: 131072));
  }

  [Test, Category("HappyPath")]
  public void Build_HonorsVolumeLabel() {
    var w = new Nilfs2Writer();
    w.AddFile("x", new byte[] { 1 });
    var img = w.Build(volumeLabel: "TEST_VOL");
    // s_volume_name lives at superblock+0xA8 (verified byte-for-byte against
    // mkfs.nilfs2 output); the earlier +0x80 was a non-spec offset.
    var label = Encoding.ASCII.GetString(img.AsSpan(1024 + 0xA8, 8));
    Assert.That(label, Is.EqualTo("TEST_VOL"));
  }
}
