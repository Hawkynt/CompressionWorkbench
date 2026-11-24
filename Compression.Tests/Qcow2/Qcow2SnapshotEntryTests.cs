using System.Reflection;
using System.Security.Cryptography;
using FileFormat.Qcow2;

namespace Compression.Tests.Qcow2;

/// <summary>
/// Internal snapshots listed and extracted as <c>snapshots/&lt;name&gt;.raw</c>: each must be the
/// guest disk <c>qemu-img convert -l snapshot.name=...</c> produces from it: at the image's current
/// virtual size, zeros past what the snapshot's own tables cover.
/// </summary>
[TestFixture]
public sealed class Qcow2SnapshotEntryTests {

  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Qcow2Vectors." + name)
      ?? throw new InvalidOperationException($"Missing embedded vector {name}.");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  private static Dictionary<string, string> Manifest(string text)
    => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r').Split("  "))
      .ToDictionary(f => f[1], f => f[0]);

  private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  /// <summary>
  /// A 256 KiB disk snapshotted (s1), grown to 384 KiB, written and snapshotted again ("second
  /// one", a name with a space), then written once more.
  /// </summary>
  private const string Make =
    "qemu-img create -q -f qcow2 -o cluster_size=4096 snapshots.qcow2 256K && " +
    "qemu-io -f qcow2 -c 'write -P 0x11 0 70000' -c 'write -P 0x12 200000 10000' snapshots.qcow2 && " +
    "qemu-img snapshot -c s1 snapshots.qcow2 && qemu-img resize -q snapshots.qcow2 384K && " +
    "qemu-io -f qcow2 -c 'write -P 0x21 4096 8192' -c 'write -P 0x22 300000 20000' snapshots.qcow2 && " +
    "qemu-img snapshot -c 'second one' snapshots.qcow2 && " +
    "qemu-io -f qcow2 -c 'write -P 0x31 0 4096' snapshots.qcow2 && qemu-img check -q snapshots.qcow2 && " +
    "qemu-img convert -O raw snapshots.qcow2 active.raw && " +
    "qemu-img convert -l snapshot.name=s1 -O raw snapshots.qcow2 s1.raw && " +
    "qemu-img convert -l 'snapshot.name=second one' -O raw snapshots.qcow2 second.raw && " +
    "printf '%s  %s\\n' $(sha256sum < active.raw | cut -c1-64) disk.img $(sha256sum < s1.raw | cut -c1-64) snapshots/s1.raw " +
    "$(sha256sum < second.raw | cut -c1-64) 'snapshots/second one.raw' > qemu-snapshots.sha256";

  private static void AssertListsAndExtractsLikeQemu(byte[] image, Dictionary<string, string> expected) {
    var descriptor = new Qcow2FormatDescriptor();
    var list = descriptor.List(new MemoryStream(image), null);
    Assert.That(list.Where(e => e.Name.StartsWith("snapshots/", StringComparison.Ordinal)).Select(e => (e.Name, e.OriginalSize)),
      Is.EquivalentTo(new[] { ("snapshots/s1.raw", 384L * 1024), ("snapshots/second one.raw", 384L * 1024) }));

    var dir = Directory.CreateTempSubdirectory("cwb-qcow2-snapx-").FullName;
    try {
      descriptor.Extract(new MemoryStream(image), dir, null, null);
      foreach (var (name, sha) in expected)
        Assert.That(Sha256(File.ReadAllBytes(Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar)))), Is.EqualTo(sha), name);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test, Category("ThirdPartyProof")]
  public void Given_AQemuImageWithSnapshots_When_ListedAndExtracted_Then_EachSnapshotIsWhatQemuImgConvertsItTo()
    => AssertListsAndExtractsLikeQemu(Resource("snapshots.qcow2"),
      Manifest(System.Text.Encoding.UTF8.GetString(Resource("qemu-snapshots.sha256"))));

  [Test, Category("HappyPath")]
  public void Given_AFilterNamingOneSnapshot_When_Extracted_Then_OnlyThatSnapshotIsWritten() {
    var dir = Directory.CreateTempSubdirectory("cwb-qcow2-snapx-").FullName;
    try {
      new Qcow2FormatDescriptor().Extract(new MemoryStream(Resource("snapshots.qcow2")), dir, null, ["snapshots/s1.raw"]);
      Assert.That(Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(Path.GetFileName), Is.EqualTo(new[] { "s1.raw" }));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Given_ASnapshotStream_When_Written_Then_ItIsReadOnly() {
    using var stream = new MemoryStream();
    stream.Write(Resource("snapshots.qcow2"));
    using var snapshot = Qcow2Stream.OpenSnapshot(stream, "s1");
    Assert.Multiple(() => {
      Assert.That(snapshot.CanWrite, Is.False);
      Assert.That(snapshot.Length, Is.EqualTo(384 * 1024), "read at the image's current size, as qemu-img convert -l reads it");
    });
  }

  [Test, Category("Exception")]
  public void Given_AnUnknownSnapshotName_When_Opened_Then_ItIsRefused()
    => Assert.Throws<ArgumentException>(() => Qcow2Stream.OpenSnapshot(new MemoryStream(Resource("snapshots.qcow2")), "s3"));

  [Test, Category("HappyPath")]
  public void Given_AnImageWithoutSnapshots_When_Listed_Then_NoSnapshotEntriesAppear() {
    var list = new Qcow2FormatDescriptor().List(new MemoryStream(Resource("zstd.qcow2")), null);
    Assert.That(list.Any(e => e.Name.StartsWith("snapshots/", StringComparison.Ordinal)), Is.False);
  }

  /// <summary>
  /// Rebuilds the vector with qemu-img / qemu-io and checks it decodes to the committed hashes;
  /// with <c>CWB_QCOW2_WRITE_VECTORS=1</c> it writes them.
  /// </summary>
  [Test]
  [Category("ExternalInterop")]
  public void Given_QemuTools_When_TheSnapshotVectorIsRegenerated_Then_ItListsAndExtractsLikeQemu() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("qemu-img") || !FsInteropToolbox.WslHasTool("qemu-io"))
      Assert.Ignore("qemu-img and qemu-io are needed in WSL (sudo apt install -y qemu-utils).");
    var dir = Directory.CreateTempSubdirectory("cwb-qcow2-snapgen-").FullName;
    try {
      var (stdout, stderr, code) = FsInteropToolbox.RunWsl($"cd {FsInteropToolbox.WinToWsl(dir)} && {Make}");
      Assert.That(code, Is.Zero, stdout + stderr);
      if (Environment.GetEnvironmentVariable("CWB_QCOW2_WRITE_VECTORS") == "1") {
        var vectors = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "Qcow2", "ReferenceVectors");
        foreach (var name in new[] { "snapshots.qcow2", "qemu-snapshots.sha256" })
          File.Copy(Path.Combine(dir, name), Path.Combine(vectors, name), overwrite: true);
        Assert.Pass("Vectors written; rebuild to embed them.");
      }
      AssertListsAndExtractsLikeQemu(File.ReadAllBytes(Path.Combine(dir, "snapshots.qcow2")),
        Manifest(File.ReadAllText(Path.Combine(dir, "qemu-snapshots.sha256"))));
    } finally {
      Directory.Delete(dir, true);
    }
  }
}
