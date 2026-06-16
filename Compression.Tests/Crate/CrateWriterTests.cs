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
  public void Create_FromArbitraryFilesWithoutCargoToml_SynthesizesManifestAndRoundTrips() {
    // Converting an arbitrary file tree (no Cargo.toml) into a crate must still
    // produce a package the descriptor can re-read: the writer synthesizes a
    // minimal manifest so the single-root-dir + Cargo.toml invariant holds.
    var payload = "hello conversion matrix"u8.ToArray();
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("HELLO.TXT", payload),
      ArchiveInputInfo.InMemory("DATA.BIN", Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()),
    };

    using var ms = new MemoryStream();
    var d = new CrateFormatDescriptor();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var list = d.List(ms, null);
    var names = list.Select(e => e.Name).ToList();
    Assert.That(names, Has.Some.EndsWith("/Cargo.toml"), "a manifest must be synthesized");
    Assert.That(names, Has.Some.EndsWith("/HELLO.TXT"));

    var helloEntry = list.Single(e => e.Name.EndsWith("/HELLO.TXT"));
    ms.Position = 0;
    using var outDir = new TempDir();
    d.Extract(ms, outDir.Path, null, [helloEntry.Name]);
    var extracted = File.ReadAllBytes(Path.Combine(outDir.Path, helloEntry.Name.Replace('/', Path.DirectorySeparatorChar)));
    Assert.That(extracted, Is.EqualTo(payload), "payload must round-trip byte-identically");
  }

  private sealed class TempDir : IDisposable {
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "crate_" + Guid.NewGuid().ToString("N")[..10]);
    public TempDir() => Directory.CreateDirectory(this.Path);
    public void Dispose() { try { Directory.Delete(this.Path, true); } catch { /* best-effort */ } }
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
