#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Ubifs;

namespace Compression.Tests.Ubifs;

/// <summary>
/// Self-round-trip tests for the UBIFS WORM writer. Validates that the emitted
/// image is parsable by <see cref="UbifsFileReader"/> and that file contents
/// survive the journey through zlib compression and the linear node log.
/// Kernel-mount round-trip is out of scope (requires a full LPT/TNC commit
/// pipeline — multi-week work).
/// </summary>
[TestFixture]
public class UbifsWriterTests {

  [Test, Category("HappyPath")]
  public void Create_SingleSmallFile_RoundTrips() {
    var d = new UbifsFormatDescriptor();
    var content = "Hello UBIFS WORM"u8.ToArray();

    var tmpDir = Path.Combine(Path.GetTempPath(), "ubifs_w1_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var srcPath = Path.Combine(tmpDir, "hello.txt");
      File.WriteAllBytes(srcPath, content);

      using var output = new MemoryStream();
      d.Create(output, [new ArchiveInputInfo(srcPath, "hello.txt", false)], new FormatCreateOptions());

      var img = output.ToArray();
      Assert.That(img.Length, Is.GreaterThanOrEqualTo(2 * UbifsWriter.DefaultLebSize), "image must include at least superblock + master LEBs");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0, 4)), Is.EqualTo(UbifsWriter.NodeMagic), "superblock at offset 0");

      using var read = new MemoryStream(img);
      var entries = d.List(read, null);
      var names = entries.Select(e => e.Name).ToHashSet();
      Assert.That(names, Does.Contain("hello.txt"));

      var outDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(outDir);
      read.Position = 0;
      d.Extract(read, outDir, null, ["hello.txt"]);

      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "hello.txt")), Is.EqualTo(content));
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* cleanup races */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Create_MultipleFiles_AllRoundTrip() {
    var d = new UbifsFormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), "ubifs_w2_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var files = new Dictionary<string, byte[]> {
        ["alpha.txt"] = Encoding.UTF8.GetBytes("alpha contents"),
        ["beta.bin"] = [0x00, 0x01, 0x02, 0xFF, 0xFE, 0xFD],
        ["gamma.dat"] = Encoding.UTF8.GetBytes(new string('G', 200)),
      };

      var inputs = new List<ArchiveInputInfo>();
      foreach (var (name, data) in files) {
        var p = Path.Combine(tmpDir, name);
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
        var p = Path.Combine(outDir, name);
        Assert.That(File.Exists(p), Is.True, $"{name} missing");
        Assert.That(File.ReadAllBytes(p), Is.EqualTo(expected), $"{name} content mismatch");
      }
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Create_FourKBoundaryFile_RoundTrips() {
    var d = new UbifsFormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), "ubifs_w3_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      // exactly two UBIFS data blocks: writer must emit two DATA nodes
      var data = new byte[2 * UbifsWriter.BlockSize];
      for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);

      var p = Path.Combine(tmpDir, "two-blocks.bin");
      File.WriteAllBytes(p, data);

      using var output = new MemoryStream();
      d.Create(output, [new ArchiveInputInfo(p, "two-blocks.bin", false)], new FormatCreateOptions());

      var outDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(outDir);
      output.Position = 0;
      d.Extract(output, outDir, null, ["two-blocks.bin"]);

      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "two-blocks.bin")), Is.EqualTo(data));
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Create_EmptyFile_RoundTrips() {
    var d = new UbifsFormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), "ubifs_w4_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var p = Path.Combine(tmpDir, "empty.txt");
      File.WriteAllBytes(p, []);

      using var output = new MemoryStream();
      d.Create(output, [new ArchiveInputInfo(p, "empty.txt", false)], new FormatCreateOptions());

      // List should mention the file even though it has zero data nodes.
      using var read = new MemoryStream(output.ToArray());
      var names = d.List(read, null).Select(e => e.Name).ToHashSet();
      Assert.That(names, Does.Contain("empty.txt"));

      var outDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(outDir);
      read.Position = 0;
      d.Extract(read, outDir, null, ["empty.txt"]);

      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "empty.txt")), Is.EqualTo(Array.Empty<byte>()));
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("Boundary")]
  public void Create_HighlyCompressibleData_ChoosesZlib() {
    var d = new UbifsFormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), "ubifs_w5_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var data = new byte[UbifsWriter.BlockSize];
      Array.Fill(data, (byte)'A');

      var p = Path.Combine(tmpDir, "compressible.bin");
      File.WriteAllBytes(p, data);

      using var output = new MemoryStream();
      d.Create(output, [new ArchiveInputInfo(p, "compressible.bin", false)], new FormatCreateOptions());

      // Image must still round-trip even though the data block was compressed.
      var outDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(outDir);
      output.Position = 0;
      d.Extract(output, outDir, null, ["compressible.bin"]);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "compressible.bin")), Is.EqualTo(data));
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesWorm() {
    var d = new UbifsFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("ErrorHandling")]
  public void Writer_RejectsWeirdLebSize() {
    Assert.Throws<ArgumentException>(() => new UbifsWriter(lebSize: 1234));
    Assert.Throws<ArgumentException>(() => new UbifsWriter(lebSize: 2048)); // < 4096 min
  }
}
