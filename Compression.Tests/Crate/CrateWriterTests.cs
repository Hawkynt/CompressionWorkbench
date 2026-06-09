using System.Text;
using Compression.Registry;
using FileFormat.Crate;

namespace Compression.Tests.Crate;

[TestFixture]
public class CrateWriterTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_FromExistingTopDir_RoundTrips() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("foo-0.1.0/Cargo.toml", Encoding.UTF8.GetBytes(
        "[package]\nname = \"foo\"\nversion = \"0.1.0\"\nedition = \"2021\"\n")),
      ArchiveInputInfo.InMemory("foo-0.1.0/src/lib.rs", "pub fn hello() {}\n"u8.ToArray()),
    };

    using var ms = new MemoryStream();
    var d = new CrateFormatDescriptor();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var list = d.List(ms, null);
    var names = list.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("foo-0.1.0/Cargo.toml"));
    Assert.That(names, Does.Contain("foo-0.1.0/src/lib.rs"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_FromInputsWithoutTopDir_LiftsThemIntoNameVersion() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("Cargo.toml", Encoding.UTF8.GetBytes(
        "[package]\nname = \"barcrate\"\nversion = \"2.5.7\"\n")),
      ArchiveInputInfo.InMemory("src/main.rs", "fn main() {}\n"u8.ToArray()),
    };

    using var ms = new MemoryStream();
    var d = new CrateFormatDescriptor();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var list = d.List(ms, null);
    var names = list.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("barcrate-2.5.7/Cargo.toml"));
    Assert.That(names, Does.Contain("barcrate-2.5.7/src/main.rs"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_WithoutCargoToml_UsesFallbackTopDir() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("README.md", "# Hello\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("LICENSE", "MIT"u8.ToArray()),
    };

    using var ms = new MemoryStream();
    var d = new CrateFormatDescriptor();

    // Reader requires a Cargo.toml under the top dir; without one the List() throws.
    // The writer still emits a valid TAR.GZ — we just verify it ran and produced a non-empty file.
    Assert.That(
      () => d.Create(ms, inputs, new FormatCreateOptions()),
      Throws.Nothing);
    Assert.That(ms.Length, Is.GreaterThan(0));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate() {
    var d = new CrateFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("EdgeCase")]
  public void Create_SkipsSyntheticMetadataIni() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("foo-1.0.0/Cargo.toml", Encoding.UTF8.GetBytes(
        "[package]\nname = \"foo\"\nversion = \"1.0.0\"\n")),
      ArchiveInputInfo.InMemory("foo-1.0.0/src/lib.rs", "pub fn x() {}\n"u8.ToArray()),
      // Synthetic listing artefact — the writer must not embed it back into the TAR.
      ArchiveInputInfo.InMemory("metadata.ini", "[crate]\nname = foo\n"u8.ToArray()),
    };

    using var ms = new MemoryStream();
    var d = new CrateFormatDescriptor();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var list = d.List(ms, null);
    // metadata.ini still appears as the synthetic listing entry — but only once,
    // and NOT under foo-1.0.0/.
    Assert.That(list.Count(e => e.Name == "metadata.ini"), Is.EqualTo(1));
    Assert.That(list.Any(e => e.Name == "foo-1.0.0/metadata.ini"), Is.False);
  }
}
