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
      nameof(RarCompatibility.Rar2_0),
      nameof(RarCompatibility.Rar1_5),
    }));
  }

  [TestCase(RarCompatibility.Rar2_0, 20, false, 0)]
  [TestCase(RarCompatibility.Rar1_5, 15, false, 0)]
  [TestCase(RarCompatibility.Rar4, 20, true, 2)]
  [NUnit.Framework.Category("Compatibility"), NUnit.Framework.Category("RoundTrip")]
  public void Given_TimestampedInput_When_WrittenForATarget_Then_OnlyRar29AndLaterGetTheExtendedTimeField(
      RarCompatibility target, int unpackVersion, bool extendedTime, int hostOs) {
    var data = "stamped"u8.ToArray();
    var when = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero).AddTicks(1234567);
    using var archive = new MemoryStream();
    var options = new FormatCreateOptions("store") {
      FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [FormatOptionKeys.TargetCompatibility] = target.ToString() },
    };
    new RarFormatDescriptor().Create(archive,
      [ArchiveInputInfo.InMemory("stamped.txt", data, new ArchiveEntryMetadata(when, when, when))], options);

    var bytes = archive.ToArray();
    const int fileHeaderOffset = 20;
    var flags = bytes[fileHeaderOffset + 3] | bytes[fileHeaderOffset + 4] << 8;
    Assert.Multiple(() => {
      Assert.That((flags & 0x1000) != 0, Is.EqualTo(extendedTime), "LHD_EXTTIME");
      Assert.That(bytes[fileHeaderOffset + 15], Is.EqualTo(hostOs), "HOST_OS");
      Assert.That(bytes[fileHeaderOffset + 24], Is.EqualTo(unpackVersion), "UNP_VER");
      Assert.That(bytes[fileHeaderOffset + 25], Is.EqualTo(0x30), "Store");
    });
    archive.Position = 0;
    using var reader = new RarReader(archive, leaveOpen: true);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Test, NUnit.Framework.Category("Compatibility")]
  public void Given_ANameOutsideTheOemCodePage_When_WrittenForRar20_Then_ItIsRefused() {
    var w = new Rar4Writer(new MemoryStream(), method: RarConstants.Rar4MethodStore, targetCompatibility: RarCompatibility.Rar2_0);
    Assert.That(() => w.AddFile("\u65e5\u672c.txt", "x"u8), Throws.TypeOf<NotSupportedException>());
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

  [TestCase(RarCompatibility.Rar1_5)]
  [TestCase(RarCompatibility.Rar2_0)]
  [NUnit.Framework.Category("Compatibility")]
  public void Rar15Writer_RejectsNewerOnlyFeatures(RarCompatibility target) {
    Assert.Multiple(() => {
      Assert.That(() => new Rar4Writer(
          new MemoryStream(),
          method: RarConstants.Rar4MethodNormal,
          targetCompatibility: target),
        Throws.TypeOf<NotSupportedException>());

      Assert.That(() => new Rar4Writer(
          new MemoryStream(),
          method: RarConstants.Rar4MethodStore,
          solid: true,
          targetCompatibility: target),
        Throws.TypeOf<NotSupportedException>());

      Assert.That(() => new Rar4Writer(
          new MemoryStream(),
          method: RarConstants.Rar4MethodStore,
          password: "secret",
          targetCompatibility: target),
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
