#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.AndroidBundle;
using FileFormat.Zip;

namespace Compression.Tests.AndroidBundle;

/// <summary>
/// WORM contract tests for the Android App Bundle writer. Creation delegates to
/// <see cref="ZipWriter"/> and synthesises a placeholder <c>BundleConfig.pb</c>
/// if the caller didn't provide one, so produced archives always carry the
/// configuration entry the AAB spec mandates at the root.
/// </summary>
[TestFixture]
public class AndroidBundleWormTests {

  private static byte[] CreateArchive(IEnumerable<(string Name, byte[] Data)> entries) {
    var d = new AndroidBundleFormatDescriptor();
    var inputs = entries.Select(e => ArchiveInputInfo.InMemory(e.Name, e.Data)).ToList();
    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Capabilities_IncludeCanCreate() {
    var d = new AndroidBundleFormatDescriptor();
    Assert.That(d.Capabilities & FormatCapabilities.CanCreate, Is.EqualTo(FormatCapabilities.CanCreate));
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_TwoModulesPlusBundleConfig_RoundTripsViaList() {
    var manifest = "<manifest/>"u8.ToArray();
    var splitApk = "split-data"u8.ToArray();
    var bundleConfig = new byte[] { 0x08, 0x01, 0x12, 0x04, (byte)'t', (byte)'e', (byte)'s', (byte)'t' };
    var bytes = CreateArchive([
      ("base/AndroidManifest.xml", manifest),
      ("splits/config.arm64_v8a.apk", splitApk),
      ("BundleConfig.pb", bundleConfig),
    ]);

    var d = new AndroidBundleFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("base/AndroidManifest.xml"));
    Assert.That(names, Does.Contain("splits/config.arm64_v8a.apk"));
    Assert.That(names, Does.Contain("BundleConfig.pb"));
  }

  [Test, Category("HappyPath")]
  public void Create_AutoSynthesisesBundleConfigWhenAbsent() {
    var bytes = CreateArchive([
      ("base/AndroidManifest.xml", "<manifest/>"u8.ToArray()),
    ]);

    using var ms = new MemoryStream(bytes);
    using var r = new ZipReader(ms, leaveOpen: true);
    var names = r.Entries.Select(e => e.FileName).ToList();
    Assert.That(names, Does.Contain("BundleConfig.pb"),
      "AAB writer should auto-emit a placeholder BundleConfig.pb when missing.");
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_PreservesEntryBytesExactly() {
    var manifest = new byte[] { 0x01, 0x02, 0x03, 0xFF, 0x00, 0xAB };
    var bytes = CreateArchive([
      ("base/AndroidManifest.xml", manifest),
    ]);

    using var ms = new MemoryStream(bytes);
    using var r = new ZipReader(ms, leaveOpen: true);
    var match = r.Entries.First(e => e.FileName == "base/AndroidManifest.xml");
    Assert.That(r.ExtractEntry(match), Is.EqualTo(manifest));
  }

  [Test, Category("EdgeCase")]
  public void Create_EmptyInputs_StillEmitsBundleConfig() {
    var bytes = CreateArchive([]);

    using var ms = new MemoryStream(bytes);
    using var r = new ZipReader(ms, leaveOpen: true);
    Assert.That(r.Entries.Any(e => e.FileName == "BundleConfig.pb"), Is.True);
  }
}
