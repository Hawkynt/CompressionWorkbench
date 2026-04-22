#pragma warning disable CS1591
using FileFormat.NetAssembly;

namespace Compression.Tests.NetAssembly;

[TestFixture]
public class NetAssemblyTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new NetAssemblyFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("NetAssembly"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".dll"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    // PE 'MZ' magic, low confidence (extension-based routing).
    Assert.That(d.MagicSignatures[0].Bytes[0], Is.EqualTo((byte)'M'));
    Assert.That(d.MagicSignatures[0].Bytes[1], Is.EqualTo((byte)'Z'));
  }

  [Test, Category("HappyPath")]
  public void ReadsOwnAssembly_EmitsReferencesAndStreams() {
    // The test assembly itself is a fully-formed managed .NET PE. Reading it exercises
    // the PE walk, CLI header resolution, metadata-stream enumeration, and the shallow
    // #~ parse for ManifestResource / AssemblyRef.
    var selfPath = typeof(NetAssemblyTests).Assembly.Location;
    if (!File.Exists(selfPath)) Assert.Ignore("Cannot locate this test assembly on disk");

    using var fs = File.OpenRead(selfPath);
    var entries = new NetAssemblyFormatDescriptor().List(fs, null);
    Assert.That(entries, Is.Not.Empty, "A .NET assembly must surface at least its metadata streams");
    Assert.That(entries.Any(e => e.Name.StartsWith("streams/")), Is.True,
      "#~ / #Strings / #Blob / #GUID / #US streams should be emitted.");
  }

  [Test, Category("SadPath")]
  public void NonNetPe_ReturnsEmpty() {
    // MZ-prefixed bytes but with no CLI header → List returns an empty set rather than
    // throwing. (This is the "native PE" case.)
    // Minimum PE: DOS stub with e_lfanew at 0x3C pointing to 'PE\0\0'.
    var buf = new byte[256];
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    buf[0x3C] = 0x80;                       // e_lfanew = 0x80
    buf[0x80] = (byte)'P'; buf[0x81] = (byte)'E';
    // NumSections=0, OptHdrSize=0 in COFF header at 0x80+4.
    using var ms = new MemoryStream(buf);
    var entries = new NetAssemblyFormatDescriptor().List(ms, null);
    Assert.That(entries, Is.Empty);
  }
}
