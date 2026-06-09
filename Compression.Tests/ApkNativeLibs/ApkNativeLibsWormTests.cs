#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.ApkNativeLibs;
using FileFormat.Zip;

namespace Compression.Tests.ApkNativeLibs;

/// <summary>
/// WORM contract tests for the APK native-libraries view. Creation delegates to
/// <see cref="ZipWriter"/>; entries supplied through the synthetic
/// <c>native_libs/&lt;abi&gt;/*.so</c> view are rewritten back to
/// <c>lib/&lt;abi&gt;/*.so</c> so the produced archive is a standard split-APK
/// fragment.
/// </summary>
[TestFixture]
public class ApkNativeLibsWormTests {

  private static byte[] CreateArchive(IEnumerable<(string Name, byte[] Data)> entries) {
    var d = new ApkNativeLibsFormatDescriptor();
    var inputs = entries.Select(e => ArchiveInputInfo.InMemory(e.Name, e.Data)).ToList();
    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Capabilities_IncludeCanCreate() {
    var d = new ApkNativeLibsFormatDescriptor();
    Assert.That(d.Capabilities & FormatCapabilities.CanCreate, Is.EqualTo(FormatCapabilities.CanCreate));
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_NativeLibsView_RewritesToLib() {
    var so1 = new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 0xAA, 0xBB };
    var so2 = new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 0xCC, 0xDD };
    var bytes = CreateArchive([
      ("native_libs/arm64-v8a/libfoo.so", so1),
      ("native_libs/x86_64/libbar.so", so2),
    ]);

    using var ms = new MemoryStream(bytes);
    using var r = new ZipReader(ms, leaveOpen: true);
    var names = r.Entries.Select(e => e.FileName).ToList();
    Assert.That(names, Does.Contain("lib/arm64-v8a/libfoo.so"));
    Assert.That(names, Does.Contain("lib/x86_64/libbar.so"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_RoundTrip_ViaDescriptorList() {
    var so = new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 0x01, 0x02, 0x03 };
    var bytes = CreateArchive([("native_libs/armeabi-v7a/libnative.so", so)]);

    var d = new ApkNativeLibsFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var listed = d.List(ms, password: null);

    Assert.That(listed, Has.Count.EqualTo(1));
    Assert.That(listed[0].Name, Is.EqualTo("native_libs/armeabi-v7a/libnative.so"));
    Assert.That(listed[0].OriginalSize, Is.EqualTo(so.Length));
  }

  [Test, Category("HappyPath")]
  public void Create_PassesThroughLibPrefixedNames() {
    var so = new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' };
    var bytes = CreateArchive([("lib/arm64-v8a/libpassthrough.so", so)]);

    using var ms = new MemoryStream(bytes);
    using var r = new ZipReader(ms, leaveOpen: true);
    Assert.That(r.Entries.Any(e => e.FileName == "lib/arm64-v8a/libpassthrough.so"), Is.True);
  }

  [Test, Category("EdgeCase")]
  public void Create_EmptyInput_ProducesValidEmptyZip() {
    var bytes = CreateArchive([]);
    using var ms = new MemoryStream(bytes);
    using var r = new ZipReader(ms, leaveOpen: true);
    Assert.That(r.Entries, Is.Empty);
  }
}
