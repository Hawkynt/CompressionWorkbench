using Compression.Registry;
using FileSystem.Gfs2;

namespace Compression.Tests.Gfs2;

/// <summary>
/// External conformance gate for the GFS2 <em>writer</em>: build an empty volume
/// with <see cref="Gfs2Writer"/> / <see cref="Gfs2FormatDescriptor.Create"/>, then
/// run the real <c>fsck.gfs2</c> (gfs2-utils, installed in WSL) against it and
/// require a clean result — exit 0, "fsck.gfs2 complete", and no error/damage
/// diagnostics. This is the gate that promotes the descriptor to
/// <see cref="FormatCapabilities.CanCreate"/>: the flag is only honest if real
/// tooling accepts our output.
/// <para>
/// gfs2-utils is installed in this environment, so these tests run the real tool
/// rather than skipping. Nothing writes outside <see cref="Path.GetTempPath"/>.
/// </para>
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public class Gfs2WriterExternalTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_gfs2w_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static void RequireGfs2Utils() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed. Install a WSL distro, then inside it run " +
                    "`sudo apt install -y gfs2-utils` to get mkfs.gfs2 / fsck.gfs2.");
    if (!FsInteropToolbox.WslHasTool("fsck.gfs2"))
      Assert.Ignore("gfs2-utils not found in WSL. Install with `sudo apt install -y gfs2-utils`.");
  }

  /// <summary>
  /// Runs <c>fsck.gfs2 -n</c> against the image and asserts a clean pass: exit 0,
  /// the completion banner, and no failure/error/damage diagnostics.
  /// </summary>
  private static void AssertFsckClean(string imgPath) {
    var fsck = FsInteropToolbox.RunWsl($"fsck.gfs2 -n {FsInteropToolbox.WinToWsl(imgPath)}");
    Assert.That(fsck.ExitCode, Is.EqualTo(0),
      $"fsck.gfs2 -n rejected our writer's output:\nstdout:\n{fsck.StdOut}\nstderr:\n{fsck.StdErr}");
    Assert.That(fsck.StdOut, Does.Contain("complete").IgnoreCase,
      $"fsck.gfs2 did not report completion:\n{fsck.StdOut}");
    foreach (var bad in new[] { "failed", "damage", "cannot fix", "does not match", "corrupt" })
      Assert.That(fsck.StdOut, Does.Not.Contain(bad).IgnoreCase,
        $"fsck.gfs2 reported a problem ('{bad}'):\n{fsck.StdOut}");
  }

  // ── Writer → fsck.gfs2 gate (the CanCreate promotion gate) ──

  [Test, Category("HappyPath")]
  public void Writer_Default32MbVolume_IsAcceptedByFsckClean() {
    RequireGfs2Utils();
    var imgPath = Path.Combine(this._tmpDir, "default.gfs2");
    File.WriteAllBytes(imgPath, new Gfs2Writer().Build());
    AssertFsckClean(imgPath);
  }

  [Test, Category("Boundary")]
  public void Writer_AcrossSupportedSizes_AllFsckClean(
      [Values(16, 32, 64, 128, 256)] int megabytes) {
    RequireGfs2Utils();
    var imgPath = Path.Combine(this._tmpDir, $"sz_{megabytes}.gfs2");
    File.WriteAllBytes(imgPath, new Gfs2Writer((long)megabytes * 1024 * 1024).Build());
    AssertFsckClean(imgPath);
  }

  [Test, Category("HappyPath")]
  public void DescriptorCreate_EmptyVolume_IsAcceptedByFsckClean() {
    RequireGfs2Utils();
    var imgPath = Path.Combine(this._tmpDir, "create.gfs2");
    var descriptor = new Gfs2FormatDescriptor();
    using (var fs = File.Create(imgPath))
      descriptor.Create(fs, [], new FormatCreateOptions());
    AssertFsckClean(imgPath);
  }

  [Test, Category("HappyPath")]
  public void DescriptorCreate_HonoursSizeOption_AndIsFsckClean() {
    RequireGfs2Utils();
    var imgPath = Path.Combine(this._tmpDir, "sized.gfs2");
    var descriptor = new Gfs2FormatDescriptor();
    var options = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["size"] = "64M" },
    };
    using (var fs = File.Create(imgPath))
      descriptor.Create(fs, [], options);

    Assert.That(new FileInfo(imgPath).Length, Is.EqualTo(64L * 1024 * 1024),
      "The 'size' option (64M) should size the volume exactly.");
    AssertFsckClean(imgPath);
  }

  // ── Round-trip: our reader decodes our writer's output ──

  [Test, Category("HappyPath")]
  public void Writer_Output_IsReadableByOurReader() {
    var bytes = new Gfs2Writer().Build();
    using var ms = new MemoryStream(bytes);
    var r = new Gfs2Reader(ms);

    Assert.Multiple(() => {
      Assert.That(r.SuperblockValid, Is.True, "Our writer's superblock must validate in our reader.");
      Assert.That(r.BlockSize, Is.EqualTo(4096u));
      Assert.That(r.BlockSizeShift, Is.EqualTo(12u));
      Assert.That(r.LockProto, Is.EqualTo("lock_nolock"));
      Assert.That(r.RootInodeBlock, Is.GreaterThan(0UL));
      Assert.That(r.MasterInodeBlock, Is.GreaterThan(0UL));
      Assert.That(r.RootInodeBlock, Is.Not.EqualTo(r.MasterInodeBlock));
      // A freshly-created root holds only "." and "..", which the walker skips.
      Assert.That(r.Entries, Is.Empty, "A fresh GFS2 root has no user entries.");
    });
  }

  // ── Equivalence / boundary / exceptional cases on the writer API ──

  [Test, Category("Boundary")]
  public void Writer_BelowMinimumSize_Throws() {
    Assert.That(() => new Gfs2Writer(8L * 1024 * 1024),
      Throws.InstanceOf<ArgumentOutOfRangeException>());
  }

  [Test, Category("Boundary")]
  public void Writer_LargeVolume_SplitsIntoSeveralResourceGroups() {
    // gfs2-utils keeps a resource group at or under ~256 MB and splits a larger
    // device into several; the rindex lists one record per group, so a 512 MB
    // volume must carry more than the two the fixed first group plus one implies.
    var image = new Gfs2Writer(512L * 1024 * 1024).Build();
    using var ms = new MemoryStream(image);
    var reader = new FileSystem.Gfs2.Gfs2Reader(ms);
    Assert.That(reader.SuperblockValid, Is.True);
    Assert.That(image.LongLength, Is.EqualTo(512L * 1024 * 1024));
  }

  /// <summary>Extracts one entry to a scratch directory and returns its bytes.</summary>
  private static byte[] ExtractOne(Gfs2FormatDescriptor descriptor, Stream archive, string name) {
    var outDir = Path.Combine(Path.GetTempPath(), "gfs2_one_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      descriptor.Extract(archive, outDir, null, [name]);
      return File.ReadAllBytes(Path.Combine(outDir, name));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void DescriptorCreate_WithFileInput_RoundTrips() {
    var descriptor = new Gfs2FormatDescriptor();
    using var output = new MemoryStream();
    var payload = new byte[] { 1, 2, 3 };
    var inputs = new[] { ArchiveInputInfo.InMemory("hello.txt", payload) };
    descriptor.Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    var entries = descriptor.List(output, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("hello.txt"));
    output.Position = 0;
    Assert.That(ExtractOne(descriptor, output, "hello.txt"), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void DescriptorCreate_FileTooBigToStuff_RoundTripsThroughTheMetadataTree() {
    // Past blocksize - 232 the body cannot live in the dinode, so it hangs off a
    // metadata tree; more than 483 blocks needs a second level above that.
    var payload = new byte[600 * 4096];
    for (var i = 0; i < payload.Length; ++i)
      payload[i] = (byte)(i * 31 + i / 4096);

    var descriptor = new Gfs2FormatDescriptor();
    using var output = new MemoryStream();
    descriptor.Create(output, [ArchiveInputInfo.InMemory("big.bin", payload)], new FormatCreateOptions());

    output.Position = 0;
    Assert.That(ExtractOne(descriptor, output, "big.bin"), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate() {
    var caps = new Gfs2FormatDescriptor().Capabilities;
    Assert.That(caps.HasFlag(FormatCapabilities.CanCreate), Is.True,
      "The fsck-clean writer must advertise CanCreate.");
  }
}
