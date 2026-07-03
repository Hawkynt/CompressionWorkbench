using FileFormat.AndroidSparse;

namespace Compression.Tests.AndroidSparse;

[TestFixture]
public class AndroidSparseTests {
  private const uint BlockSize = 4096;

  // A raw image with a leading non-zero block, a zero block-run (-> DONT_CARE),
  // and a trailing non-zero block. Size is an exact multiple of the block size so
  // the sparse round-trip is byte-identical.
  private static byte[] BuildRawImage() {
    var raw = new byte[BlockSize * 4];
    for (var i = 0; i < BlockSize; ++i)
      raw[i] = (byte)(i & 0xFF);                       // block 0: data
    // blocks 1..2 stay zero -> DONT_CARE
    for (var i = 0; i < BlockSize; ++i)
      raw[BlockSize * 3 + i] = (byte)0xA5;             // block 3: data
    return raw;
  }

  private static byte[] BuildSyntheticSparse() {
    var raw = BuildRawImage();
    return AndroidSparseCodec.Build(raw, BlockSize);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new AndroidSparseFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d.Id, Is.EqualTo("AndroidSparse"));
      Assert.That(d.Extensions, Contains.Item(".simg"));
      Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
      // On-disk magic bytes 3A FF 26 ED (LE u32 0xED26FF3A, per libsparse).
      Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x3A, 0xFF, 0x26, 0xED }));
    });
  }

  [Test, Category("HappyPath")]
  public void List_ExposesImageAndMetadata() {
    var sparse = BuildSyntheticSparse();
    var d = new AndroidSparseFormatDescriptor();
    using var ms = new MemoryStream(sparse);
    var entries = d.List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    var img = entries.FirstOrDefault(e => e.Name == "image.raw");
    Assert.That(img, Is.Not.Null);
    Assert.That(img!.OriginalSize, Is.EqualTo(BlockSize * 4));
  }

  [Test, Category("HappyPath")]
  public void Extract_ExpandsToRawByteIdentical() {
    var raw = BuildRawImage();
    var sparse = AndroidSparseCodec.Build(raw, BlockSize);
    var d = new AndroidSparseFormatDescriptor();
    var dir = FreshDir("asparse_x");
    try {
      using var ms = new MemoryStream(sparse);
      d.Extract(ms, dir, null, null);
      var expanded = File.ReadAllBytes(Path.Combine(dir, "image.raw"));
      Assert.That(expanded, Is.EqualTo(raw));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("block_size=4096"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Create_ThenExtract_RoundTrips() {
    var raw = BuildRawImage();
    var d = new AndroidSparseFormatDescriptor();
    using var sparseMs = new MemoryStream();
    d.Create(sparseMs, [Compression.Registry.ArchiveInputInfo.InMemory("image.raw", raw)],
      new Compression.Registry.FormatCreateOptions());
    var sparse = sparseMs.ToArray();

    // Sparse must be materially smaller than raw thanks to the DONT_CARE run.
    Assert.That(sparse.Length, Is.LessThan(raw.Length));

    var dir = FreshDir("asparse_rt");
    try {
      using var read = new MemoryStream(sparse);
      d.Extract(read, dir, null, null);
      var expanded = File.ReadAllBytes(Path.Combine(dir, "image.raw"));
      Assert.That(expanded, Is.EqualTo(raw));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = new byte[64];
    Array.Fill(garbage, (byte)0x44);
    var d = new AndroidSparseFormatDescriptor();
    var dir = FreshDir("asparse_bad");
    try {
      using var ms = new MemoryStream(garbage);
      Assert.DoesNotThrow(() => d.Extract(ms, dir, null, null));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  // ── WSL-gated interop: proves our reader/writer agree with libsparse ──────────

  [Test, Category("ExternalTool")]
  public void Wsl_Simg2Img_ReproducesRaw() {
    RequireSparseTools();
    var raw = BuildRawImage();
    var d = new AndroidSparseFormatDescriptor();

    var dir = FreshDir("asparse_wsl_w");
    try {
      var simgPath = Path.Combine(dir, "ours.simg");
      var outRaw = Path.Combine(dir, "out.raw");
      using (var fs = File.Create(simgPath))
        d.Create(fs, [Compression.Registry.ArchiveInputInfo.InMemory("image.raw", raw)],
          new Compression.Registry.FormatCreateOptions());

      var res = FsInteropToolbox.RunWsl(
        $"simg2img {FsInteropToolbox.WinToWsl(simgPath)} {FsInteropToolbox.WinToWsl(outRaw)}");
      Assert.That(res.ExitCode, Is.EqualTo(0), $"simg2img failed: {res.StdErr}");
      Assert.That(File.ReadAllBytes(outRaw), Is.EqualTo(raw),
        "simg2img did not reproduce the raw image from our sparse output.");
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test, Category("ExternalTool")]
  public void Wsl_Img2Simg_ReadsBackViaOurReader() {
    RequireSparseTools();
    var raw = BuildRawImage();

    var dir = FreshDir("asparse_wsl_r");
    try {
      var rawPath = Path.Combine(dir, "in.raw");
      var simgPath = Path.Combine(dir, "ref.simg");
      File.WriteAllBytes(rawPath, raw);

      var res = FsInteropToolbox.RunWsl(
        $"img2simg {FsInteropToolbox.WinToWsl(rawPath)} {FsInteropToolbox.WinToWsl(simgPath)} {BlockSize}");
      Assert.That(res.ExitCode, Is.EqualTo(0), $"img2simg failed: {res.StdErr}");

      var sparse = File.ReadAllBytes(simgPath);
      var expanded = AndroidSparseCodec.Expand(sparse);
      Assert.That(expanded, Is.EqualTo(raw),
        "Our reader did not reproduce the raw image from img2simg's sparse output.");
    } finally {
      Directory.Delete(dir, true);
    }
  }

  private static void RequireSparseTools() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed. Run `wsl --install` in Admin PowerShell and reboot.");
    if (!FsInteropToolbox.WslHasTool("simg2img") || !FsInteropToolbox.WslHasTool("img2simg")) {
      // Attempt an unattended install (sudo password 1234, per project convention).
      FsInteropToolbox.RunWsl("echo 1234 | sudo -S apt-get install -y android-sdk-libsparse-utils");
      // WslHasTool caches per name, so re-probe directly after the install attempt.
      var probe = FsInteropToolbox.RunWsl("command -v simg2img && command -v img2simg");
      if (probe.ExitCode != 0)
        Assert.Ignore("simg2img/img2simg not installed. Install via " +
                      "`sudo apt-get install -y android-sdk-libsparse-utils` inside WSL.");
    }
  }

  private static string FreshDir(string prefix) {
    var dir = Path.Combine(Path.GetTempPath(), prefix + "_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    return dir;
  }
}
