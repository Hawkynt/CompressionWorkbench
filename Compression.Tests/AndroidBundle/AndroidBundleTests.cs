#pragma warning disable CS1591
using FileFormat.AndroidBundle;
using FileFormat.Zip;

namespace Compression.Tests.AndroidBundle;

[TestFixture]
public class AndroidBundleTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new AndroidBundleFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("AndroidBundle"));
    Assert.That(d.Extensions, Contains.Item(".aab"));
    Assert.That(d.Extensions, Contains.Item(".apks"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    // PK local-file header at very low confidence — Zip/Apk must win by default.
    Assert.That(d.MagicSignatures[0].Confidence, Is.LessThan(0.5));
  }

  [Test, Category("HappyPath")]
  public void ListsEntries_FromSyntheticBundle() {
    // Synthesise an .aab-shaped ZIP: base/AndroidManifest.xml, base/res/values.xml,
    // splits/config.arm64_v8a.apk, BundleConfig.pb.
    using var ms = new MemoryStream();
    using (var w = new ZipWriter(ms, leaveOpen: true)) {
      w.AddEntry("base/AndroidManifest.xml", "<manifest/>"u8.ToArray());
      w.AddEntry("base/res/values.xml", "<values/>"u8.ToArray());
      w.AddEntry("splits/config.arm64_v8a.apk", "split-data"u8.ToArray());
      w.AddEntry("BundleConfig.pb", new byte[] { 0x08, 0x01, 0x12, 0x04, (byte)'t', (byte)'e', (byte)'s', (byte)'t' });
    }
    ms.Position = 0;
    var entries = new AndroidBundleFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(4));
    Assert.That(entries.Any(e => e.Name == "base/AndroidManifest.xml"), Is.True);
    Assert.That(entries.Any(e => e.Name == "splits/config.arm64_v8a.apk"), Is.True);
    Assert.That(entries.Any(e => e.Name == "BundleConfig.pb"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_EmitsMetadataIniAlongsideBundleConfig() {
    using var ms = new MemoryStream();
    using (var w = new ZipWriter(ms, leaveOpen: true)) {
      w.AddEntry("base/AndroidManifest.xml", "<manifest/>"u8.ToArray());
      // Make BundleConfig.pb include some printable runs so SummarizeBundleConfig emits usable output.
      // Hand-assembled bytes to avoid C#'s variable-length \x hex escapes eating letters.
      var pbBytes = new List<byte> { 0x08, 0x01 };
      pbBytes.AddRange("arm64_v8a"u8.ToArray());
      pbBytes.Add(0x00);
      pbBytes.Add(0x12); pbBytes.Add(0x04);
      pbBytes.AddRange("xxhdpi"u8.ToArray());
      w.AddEntry("BundleConfig.pb", pbBytes.ToArray());
    }
    ms.Position = 0;
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_aab_" + Guid.NewGuid().ToString("N"));
    try {
      new AndroidBundleFormatDescriptor().Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "BundleConfig.pb")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("arm64_v8a"));
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    }
  }
}
