using System.ComponentModel;
using System.Diagnostics;
using Compression.Registry;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

[TestFixture]
public class RarCompatibilityTests {

  [Test, NUnit.Framework.Category("Compatibility")]
  public void Descriptor_ExposesTargetCompatibilityAsConstraint() {
    var descriptor = new RarFormatDescriptor();
    var schema = (IFormatOptionsSchema)descriptor;
    var target = schema.OptionsSchema.Single(static option => option.Key == FormatOptionKeys.TargetCompatibility);

    Assert.That(target.IsOptimizationAxis, Is.False);
    Assert.That(target.Default, Is.EqualTo(nameof(RarCompatibility.Rar5)));
    Assert.That(target.AllowedValues, Is.EqualTo(new[] {
      nameof(RarCompatibility.Rar5),
      nameof(RarCompatibility.Rar4),
      nameof(RarCompatibility.Rar1_5),
    }));
  }

  [Test, NUnit.Framework.Category("Compatibility"), NUnit.Framework.Category("RoundTrip")]
  public void Rar15Target_EmitsUnpack15StoredArchive() {
    var data = "Readable by a RAR 1.5 era extractor"u8.ToArray();
    var descriptor = new RarFormatDescriptor();
    using var archive = new MemoryStream();

    descriptor.Create(
      archive,
      [ArchiveInputInfo.InMemory("legacy.txt", data)],
      CompatibilityOptions(RarCompatibility.Rar1_5));

    var bytes = archive.ToArray();
    Assert.That(bytes[..7], Is.EqualTo(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }));

    // Marker (7) + canonical MAIN_HEAD (13) = FILE_HEAD starts at byte 20.
    const int fileHeaderOffset = 20;
    Assert.That(bytes[fileHeaderOffset + 2], Is.EqualTo(0x74), "RAR 1.5 FILE_HEAD type");
    Assert.That(bytes[fileHeaderOffset + 24], Is.EqualTo(15), "UNP_VER must require only RAR 1.5");
    Assert.That(bytes[fileHeaderOffset + 25], Is.EqualTo(0x30), "RAR 1.5 compatibility currently uses Store");

    archive.Position = 0;
    using var reader = new RarReader(archive, leaveOpen: true);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("legacy.txt"));
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Test, NUnit.Framework.Category("Compatibility")]
  public void Rar15Writer_RejectsNewerOnlyFeatures() {
    Assert.Multiple(() => {
      Assert.That(() => new Rar4Writer(
          new MemoryStream(),
          method: RarConstants.Rar4MethodNormal,
          targetCompatibility: RarCompatibility.Rar1_5),
        Throws.TypeOf<NotSupportedException>());

      Assert.That(() => new Rar4Writer(
          new MemoryStream(),
          method: RarConstants.Rar4MethodStore,
          solid: true,
          targetCompatibility: RarCompatibility.Rar1_5),
        Throws.TypeOf<NotSupportedException>());

      Assert.That(() => new Rar4Writer(
          new MemoryStream(),
          method: RarConstants.Rar4MethodStore,
          password: "secret",
          targetCompatibility: RarCompatibility.Rar1_5),
        Throws.TypeOf<NotSupportedException>());
    });
  }

  [Test, NUnit.Framework.Category("Compatibility"), NUnit.Framework.Category("RoundTrip")]
  public void LegacyRar15MethodAlias_StillSelectsCompatibilityTarget() {
    var data = "alias"u8.ToArray();
    var descriptor = new RarFormatDescriptor();
    using var archive = new MemoryStream();

    descriptor.Create(
      archive,
      [ArchiveInputInfo.InMemory("alias.txt", data)],
      new FormatCreateOptions("rar15"));

    var bytes = archive.ToArray();
    Assert.That(bytes[20 + 24], Is.EqualTo(15));
  }

  [Test, NUnit.Framework.Category("ExternalInterop")]
  public void SevenZip_ExtractsRar15Target() {
    var root = Path.Combine(Path.GetTempPath(), "cwb-rar15-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try {
      var archivePath = Path.Combine(root, "legacy.rar");
      var outputPath = Path.Combine(root, "out");
      Directory.CreateDirectory(outputPath);
      var data = "external RAR 1.5 compatibility oracle"u8.ToArray();

      var descriptor = new RarFormatDescriptor();
      using (var archive = File.Create(archivePath))
        descriptor.Create(
          archive,
          [ArchiveInputInfo.InMemory("legacy.txt", data)],
          CompatibilityOptions(RarCompatibility.Rar1_5));

      var psi = new ProcessStartInfo {
        FileName = "7z",
        Arguments = $"x -y -o\"{outputPath}\" \"{archivePath}\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      };

      Process? process;
      try {
        process = Process.Start(psi);
      } catch (Win32Exception) {
        Assert.Ignore("7z is not available on PATH; CI installs it for ExternalInterop.");
        return;
      }

      using (process) {
        Assert.That(process, Is.Not.Null);
        var stdOut = process!.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.EqualTo(0), $"7z failed. stdout: {stdOut}\nstderr: {stdErr}");
      }

      Assert.That(File.ReadAllBytes(Path.Combine(outputPath, "legacy.txt")), Is.EqualTo(data));
    } finally {
      try { Directory.Delete(root, recursive: true); } catch { /* best-effort test cleanup */ }
    }
  }

  private static FormatCreateOptions CompatibilityOptions(RarCompatibility compatibility)
    => new() {
      FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        [FormatOptionKeys.TargetCompatibility] = compatibility.ToString(),
      },
    };
}
