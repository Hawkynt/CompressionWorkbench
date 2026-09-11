#pragma warning disable CS1591
using System.Text;
using FileSystem.TahoeLafs;

namespace Compression.Tests.TahoeLafs;

[TestFixture]
public sealed class TahoeLafsCapabilityTests {

  [TestCase("URI:CHK:a:b:3:10:1", TahoeLafsCapabilityKind.File, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Chk, false)]
  [TestCase("URI:LIT:abc", TahoeLafsCapabilityKind.File, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Lit, false)]
  [TestCase("URI:SSK:a:b", TahoeLafsCapabilityKind.File, TahoeLafsCapabilityAccess.ReadWrite, TahoeLafsObjectFormat.Sdmf, false)]
  [TestCase("URI:SSK-RO:a:b", TahoeLafsCapabilityKind.File, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Sdmf, false)]
  [TestCase("URI:MDMF:a:b", TahoeLafsCapabilityKind.File, TahoeLafsCapabilityAccess.ReadWrite, TahoeLafsObjectFormat.Mdmf, false)]
  [TestCase("URI:MDMF-RO:a:b", TahoeLafsCapabilityKind.File, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Mdmf, false)]
  [TestCase("URI:DIR2:a:b", TahoeLafsCapabilityKind.Directory, TahoeLafsCapabilityAccess.ReadWrite, TahoeLafsObjectFormat.Sdmf, true)]
  [TestCase("URI:DIR2-RO:a:b", TahoeLafsCapabilityKind.Directory, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Sdmf, true)]
  [TestCase("URI:DIR2-MDMF:a:b", TahoeLafsCapabilityKind.Directory, TahoeLafsCapabilityAccess.ReadWrite, TahoeLafsObjectFormat.Mdmf, true)]
  [TestCase("URI:DIR2-MDMF-RO:a:b", TahoeLafsCapabilityKind.Directory, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Mdmf, true)]
  [TestCase("URI:DIR2-CHK:a:b:3:10:1", TahoeLafsCapabilityKind.Directory, TahoeLafsCapabilityAccess.ReadOnly, TahoeLafsObjectFormat.Chk, true)]
  [TestCase("URI:SSK-Verifier:a:b", TahoeLafsCapabilityKind.Verifier, TahoeLafsCapabilityAccess.Verify, TahoeLafsObjectFormat.Sdmf, false)]
  public void Parse_KnownFamilies_ClassifiesAuthority(
      string text,
      TahoeLafsCapabilityKind kind,
      TahoeLafsCapabilityAccess access,
      TahoeLafsObjectFormat format,
      bool isDirectory) {
    var capability = TahoeLafsCapability.Parse(text);
    Assert.Multiple(() => {
      Assert.That(capability.Kind, Is.EqualTo(kind));
      Assert.That(capability.Access, Is.EqualTo(access));
      Assert.That(capability.Format, Is.EqualTo(format));
      Assert.That(capability.IsDirectory, Is.EqualTo(isDirectory));
      Assert.That(capability.CanWrite, Is.EqualTo(access == TahoeLafsCapabilityAccess.ReadWrite));
      Assert.That(capability.CanRead, Is.EqualTo(access != TahoeLafsCapabilityAccess.Verify));
    });
  }

  [Test]
  public void FutureReadOnlyWrapper_IsPreservedAndNotDecoded() {
    const string text = "ro.future-capability-that-this-version-does-not-understand";
    var capability = TahoeLafsCapability.Parse(text);
    Assert.Multiple(() => {
      Assert.That(capability.Kind, Is.EqualTo(TahoeLafsCapabilityKind.Unknown));
      Assert.That(capability.Access, Is.EqualTo(TahoeLafsCapabilityAccess.ReadOnly));
      Assert.That(capability.Value, Is.EqualTo(text));
    });
  }

  [Test]
  public void ToString_NeverLeaksBearerCapability() {
    const string secret = "URI:DIR2:very-secret-write-key:fingerprint";
    var capability = TahoeLafsCapability.Parse(secret);
    Assert.That(capability.ToString(), Does.Not.Contain(secret));
    Assert.That(capability.ToString(), Does.Not.Contain("very-secret-write-key"));
  }

  [Test]
  public void ConnectionDocument_RoundTripsWithoutChangingCapability() {
    const string secret = "URI:DIR2:write-key:fingerprint";
    var original = new TahoeLafsConnection(
      new Uri("http://127.0.0.1:3456"),
      TahoeLafsCapability.Parse(secret));
    using var stream = new MemoryStream(original.Serialize());

    var parsed = TahoeLafsConnection.Parse(stream);
    Assert.Multiple(() => {
      Assert.That(parsed.NodeUri.AbsoluteUri, Is.EqualTo("http://127.0.0.1:3456/"));
      Assert.That(parsed.RootCapability.Value, Is.EqualTo(secret));
      Assert.That(parsed.CanWrite, Is.True);
      Assert.That(parsed.ToString(), Does.Not.Contain(secret));
      Assert.That(Encoding.UTF8.GetString(parsed.Serialize()), Does.StartWith(TahoeLafsConnection.Magic + "\n"));
    });
  }

  [Test]
  public void ConnectionDocument_RejectsNonDirectoryOrNonHttpRoots() {
    Assert.Throws<ArgumentException>(() => new TahoeLafsConnection(
      new Uri("file:///tmp/tahoe"),
      TahoeLafsCapability.Parse("URI:DIR2:a:b")));
    Assert.Throws<ArgumentException>(() => new TahoeLafsConnection(
      new Uri("http://127.0.0.1:3456/"),
      TahoeLafsCapability.Parse("URI:CHK:a:b:3:10:1")));
  }

  [Test]
  public void Parse_RejectsControlCharacters() {
    Assert.That(TahoeLafsCapability.TryParse("URI:DIR2:a:b\nleak", out _), Is.False);
  }
}
