using System.Diagnostics;
using System.Text;
using Compression.Registry;
using FileSystem.MinixV1;

namespace Compression.Tests.MinixV1;

[TestFixture]
public class MinixV1WriterTests {

  private static readonly (string Path, string Content)[] SampleTree = [
    ("readme.txt", "Top-level readme for the Minix v1 volume."),
    ("docs/guide.txt", "A guide that lives one directory deep."),
    ("docs/api/reference.txt", "Reference material nested two directories deep."),
  ];

  // Builds an image from the sample tree and returns the raw bytes.
  private static byte[] BuildSampleImage() {
    using var ms = new MemoryStream();
    using (var w = new MinixV1Writer(ms, leaveOpen: true)) {
      foreach (var (path, content) in SampleTree)
        w.AddFile(path, Encoding.ASCII.GetBytes(content));
      w.Finish();
    }
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Build_NestedFiles_RoundTripsThroughReader() {
    var image = BuildSampleImage();

    using var ms = new MemoryStream(image);
    var reader = new MinixV1Reader(ms);

    Assert.That(reader.Magic, Is.EqualTo((ushort)0x137F));
    Assert.That(reader.NameLength, Is.EqualTo(14));

    foreach (var (path, content) in SampleTree) {
      var entry = reader.Entries.FirstOrDefault(e => e.Name == path && !e.IsDirectory);
      Assert.That(entry, Is.Not.Null, $"expected file '{path}' in the image");
      var data = reader.Extract(entry!);
      Assert.That(Encoding.ASCII.GetString(data), Is.EqualTo(content),
        $"content mismatch for '{path}'");
    }
  }

  [Test, Category("HappyPath")]
  public void Build_NestedFiles_CreatesIntermediateDirectories() {
    var image = BuildSampleImage();
    using var ms = new MemoryStream(image);
    var reader = new MinixV1Reader(ms);

    Assert.That(reader.Entries.Any(e => e.Name == "docs" && e.IsDirectory), Is.True);
    Assert.That(reader.Entries.Any(e => e.Name == "docs/api" && e.IsDirectory), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Build_LargeFile_UsesIndirectZonesAndRoundTrips() {
    // > 7 KiB forces use of the single-indirect zone (7 direct zones = 7168 bytes).
    var big = new byte[20000];
    for (var i = 0; i < big.Length; i++) big[i] = (byte)(i * 31 + 7);

    using var ms = new MemoryStream();
    using (var w = new MinixV1Writer(ms, leaveOpen: true)) {
      w.AddFile("big.bin", big);
      w.Finish();
    }
    ms.Position = 0;

    var reader = new MinixV1Reader(ms);
    var entry = reader.Entries.Single(e => e.Name == "big.bin");
    Assert.That(entry.Size, Is.EqualTo(big.Length));
    Assert.That(reader.Extract(entry), Is.EqualTo(big));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_IsCreatable_AndCreateProducesReadableImage() {
    var d = new MinixV1FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());

    var inputs = SampleTree
      .Select(t => ArchiveInputInfo.InMemory(t.Path, Encoding.ASCII.GetBytes(t.Content)))
      .ToList();

    using var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(ms, inputs, new FormatCreateOptions());
    ms.Position = 0;

    var listed = d.List(ms, null);
    foreach (var (path, _) in SampleTree)
      Assert.That(listed.Any(e => e.Name == path), Is.True, $"missing '{path}' in created image");
  }

  [Test, Category("External")]
  public void Build_Image_PassesFsckMinix() {
    var fsck = FindOnPath("fsck.minix");
    if (fsck is null) {
      Assert.Ignore("fsck.minix not installed; skipping external validation.");
      return;
    }

    var image = BuildSampleImage();
    var tmp = Path.Combine(Path.GetTempPath(), $"minixv1-{Guid.NewGuid():N}.img");
    File.WriteAllBytes(tmp, image);
    try {
      var (exit, output) = RunFsck(fsck, tmp);
      Assert.That(exit, Is.Zero,
        $"fsck.minix reported errors (exit {exit}):\n{output}");
    } finally {
      File.Delete(tmp);
    }
  }

  private static string? FindOnPath(string name) {
    var path = Environment.GetEnvironmentVariable("PATH") ?? "";
    foreach (var dir in path.Split(Path.PathSeparator)) {
      if (dir.Length == 0) continue;
      var candidate = Path.Combine(dir, name);
      if (File.Exists(candidate)) return candidate;
    }
    return null;
  }

  private static (int Exit, string Output) RunFsck(string fsck, string image) {
    var psi = new ProcessStartInfo(fsck) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      RedirectStandardInput = true,
      UseShellExecute = false,
    };
    psi.ArgumentList.Add("-f"); // force full check even if "clean"
    psi.ArgumentList.Add(image);
    using var p = Process.Start(psi)!;
    p.StandardInput.Close();
    var stdout = p.StandardOutput.ReadToEnd();
    var stderr = p.StandardError.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, stdout + stderr);
  }
}
