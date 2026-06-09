using System.Text;
using Compression.Registry;
using FileFormat.Snap;
using FileSystem.SquashFs;

namespace Compression.Tests.Snap;

[TestFixture]
public class SnapWriterTests {

  [Test, Category("HappyPath")]
  public void Capabilities_IncludeWormCreate() {
    var d = new SnapFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_WithoutManifest_SynthesisesMetaSnapYaml() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("bin/hello", Encoding.UTF8.GetBytes("hello snap")),
    };

    using var output = new MemoryStream();
    new SnapFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    using var r = new SquashFsReader(output);
    Assert.That(r.Entries.Any(e => e.FullPath == "meta/snap.yaml"), Is.True);
    Assert.That(r.Entries.Any(e => e.FullPath == "bin/hello"), Is.True);

    var manifestEntry = r.Entries.First(e => e.FullPath == "meta/snap.yaml");
    var manifestText = Encoding.UTF8.GetString(r.Extract(manifestEntry));
    Assert.That(manifestText, Does.Contain("name:"));
    Assert.That(manifestText, Does.Contain("version:"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_WithExplicitManifest_PreservesOriginalManifest() {
    const string OriginalYaml = "name: custom-snap\nversion: 9.9\nsummary: keep me\nbase: core24\n";
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("meta/snap.yaml", Encoding.UTF8.GetBytes(OriginalYaml)),
      ArchiveInputInfo.InMemory("bin/hello", Encoding.UTF8.GetBytes("hello")),
    };

    using var output = new MemoryStream();
    new SnapFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    using var r = new SquashFsReader(output);
    var manifest = r.Entries.First(e => e.FullPath == "meta/snap.yaml");
    var manifestText = Encoding.UTF8.GetString(r.Extract(manifest));
    Assert.That(manifestText, Is.EqualTo(OriginalYaml));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_DescriptorListSurfacesAllInputs() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("payload/data.bin", new byte[] { 1, 2, 3, 4 }),
      ArchiveInputInfo.InMemory("scripts/start.sh", Encoding.UTF8.GetBytes("#!/bin/sh\necho hi\n")),
    };

    using var output = new MemoryStream();
    new SnapFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    var entries = new SnapFormatDescriptor().List(output, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("payload/data.bin"));
    Assert.That(names, Does.Contain("scripts/start.sh"));
    Assert.That(names, Does.Contain("meta/snap.yaml"));
  }

  [Test, Category("HappyPath")]
  public void Create_OptionsOverrideManifestFields() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("README", Encoding.UTF8.GetBytes("readme")),
    };
    var options = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["snap_name"] = "my-package",
        ["snap_version"] = "7.42",
        ["snap_summary"] = "A custom summary",
        ["snap_base"] = "core24",
      },
    };

    using var output = new MemoryStream();
    new SnapFormatDescriptor().Create(output, inputs, options);

    output.Position = 0;
    using var r = new SquashFsReader(output);
    var manifest = Encoding.UTF8.GetString(r.Extract(r.Entries.First(e => e.FullPath == "meta/snap.yaml")));
    Assert.That(manifest, Does.Contain("name: my-package"));
    Assert.That(manifest, Does.Contain("version: 7.42"));
    Assert.That(manifest, Does.Contain("summary: A custom summary"));
    Assert.That(manifest, Does.Contain("base: core24"));
  }

  [Test, Category("EdgeCase")]
  public void Create_EmptyInputs_StillProducesValidSnap() {
    using var output = new MemoryStream();
    new SnapFormatDescriptor().Create(output, new List<ArchiveInputInfo>(), new FormatCreateOptions());

    output.Position = 0;
    using var r = new SquashFsReader(output);
    Assert.That(r.Entries.Any(e => e.FullPath == "meta/snap.yaml"), Is.True);
  }
}
