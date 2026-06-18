using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Compression.Analysis.ExternalTools;
using Compression.Registry;
using FileFormat.Ova;
using FileFormat.Tar;

namespace Compression.Tests.Ova;

[TestFixture]
public class OvaTests {

  [Test, Category("HappyPath")]
  public void SourceGenerator_RegistersAllNewDescriptors() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    foreach (var id in new[] { "Ova", "CloneCd", "Qed", "BochsDisk", "Afio" })
      Assert.That(FormatRegistry.GetById(id), Is.Not.Null, $"Descriptor '{id}' was not registered.");
  }

  private const string OvfXml =
    "<?xml version=\"1.0\"?>\n" +
    "<Envelope xmlns:ovf=\"http://schemas.dmtf.org/ovf/envelope/1\">\n" +
    "  <References><File ovf:href=\"disk1.vmdk\"/></References>\n" +
    "  <DiskSection><Disk ovf:diskId=\"vmdisk1\"/></DiskSection>\n" +
    "  <VirtualSystem ovf:id=\"TestAppliance\">\n" +
    "    <OperatingSystemSection ovf:id=\"94\">\n" +
    "      <Description>Linux 64-bit</Description>\n" +
    "    </OperatingSystemSection>\n" +
    "  </VirtualSystem>\n" +
    "</Envelope>\n";

  private static byte[] BuildSyntheticOva() {
    using var ms = new MemoryStream();
    using (var w = new TarWriter(ms, leaveOpen: true)) {
      w.AddEntry(new TarEntry { Name = "appliance.ovf" }, Encoding.UTF8.GetBytes(OvfXml));
      w.AddEntry(new TarEntry { Name = "disk1.vmdk" }, new byte[] { 1, 2, 3, 4, 5 });
      w.AddEntry(new TarEntry { Name = "appliance.mf" }, Encoding.UTF8.GetBytes("SHA1(disk1.vmdk)=deadbeef\n"));
      w.Finish();
    }
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new OvaFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ova"));
    Assert.That(d.Extensions, Contains.Item(".ova"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndMembers() {
    var img = BuildSyntheticOva();
    var d = new OvaFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);

    Assert.That(entries[0].Name, Is.EqualTo("FULL.ova"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(img.Length));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "appliance.ovf"), Is.True);
    Assert.That(entries.Any(e => e.Name == "disk1.vmdk"), Is.True);
    Assert.That(entries.Any(e => e.Name == "appliance.mf"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_FullByteIdenticalAndMetadataParsesOvf() {
    var img = BuildSyntheticOva();
    var d = new OvaFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "ova_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);

      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.ova"));
      Assert.That(full, Is.EqualTo(img));

      var disk = File.ReadAllBytes(Path.Combine(dir, "disk1.vmdk"));
      Assert.That(disk, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));

      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("disk_count=1"));
      Assert.That(meta, Does.Contain("ovf_member=appliance.ovf"));
      Assert.That(meta, Does.Contain("vm_name=TestAppliance"));
      Assert.That(meta, Does.Contain("os_description=Linux 64-bit"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow_FullAndPartialMetadata() {
    var garbage = new byte[600];
    Array.Fill(garbage, (byte)0x5A);
    var d = new OvaFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "ova_bad_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(garbage);
      List<Compression.Registry.ArchiveEntryInfo>? entries = null;
      Assert.DoesNotThrow(() => entries = d.List(ms, null));
      Assert.That(entries![0].Name, Is.EqualTo("FULL.ova"));

      ms.Position = 0;
      Assert.DoesNotThrow(() => d.Extract(ms, dir, null, null));
      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.ova"));
      Assert.That(full, Is.EqualTo(garbage));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  // ── Writer / reader round-trip ──────────────────────────────────────

  private static readonly byte[] Disk1 = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray();
  private static readonly byte[] Disk2 = Enumerable.Range(0, 1500).Select(i => (byte)(i ^ 0x5A)).ToArray();

  [Test, Category("HappyPath")]
  public void Writer_OvfFirst_DisksThenManifest_Order() {
    var ova = new OvaWriter()
      .Add("disk1.vmdk", Disk1)
      .Add("vm.ovf", Encoding.UTF8.GetBytes(OvfXml))
      .Add("disk2.vmdk", Disk2)
      .ToArray();

    var reader = OvaReader.Read(new MemoryStream(ova));
    var names = reader.Members.Select(m => m.Name).ToList();

    Assert.That(names[0], Does.EndWith(".ovf"), "OVF must be the first member.");
    Assert.That(names[^1], Does.EndWith(".mf"), "Manifest must be the last member.");
    Assert.That(names, Does.Contain("disk1.vmdk"));
    Assert.That(names, Does.Contain("disk2.vmdk"));
  }

  [Test, Category("HappyPath")]
  public void Writer_GeneratedManifest_Sha256Matches() {
    var ova = new OvaWriter()
      .Add("vm.ovf", Encoding.UTF8.GetBytes(OvfXml))
      .Add("disk1.vmdk", Disk1)
      .ToArray();

    var reader = OvaReader.Read(new MemoryStream(ova));
    var checks = reader.VerifyManifest();

    Assert.That(checks, Is.Not.Empty);
    Assert.That(checks.All(c => c.Algorithm == "SHA256"), Is.True);
    Assert.That(checks.All(c => c.Matches), Is.True, "Every manifest digest must match its member bytes.");
    Assert.That(reader.ManifestVerifies(), Is.True);

    // Independently confirm one of the SHA-256 digests.
    var diskCheck = checks.Single(c => c.FileName == "disk1.vmdk");
    var expected = Convert.ToHexStringLower(SHA256.HashData(Disk1));
    Assert.That(diskCheck.Expected, Is.EqualTo(expected));
  }

  [Test, Category("HappyPath")]
  public void Writer_NoOvf_SynthesizesValidEnvelope() {
    var ova = new OvaWriter()
      .Add("disk1.vmdk", Disk1)
      .ToArray();

    var reader = OvaReader.Read(new MemoryStream(ova));
    var ovf = reader.Ovf;
    Assert.That(ovf, Is.Not.Null, "A synthesised OVF must be present.");

    var xml = Encoding.UTF8.GetString(ovf!.Data);
    // Well-formed XML.
    Assert.DoesNotThrow(() => System.Xml.Linq.XDocument.Parse(xml));
    // References the disk via ovf:href.
    Assert.That(xml, Does.Contain("ovf:href=\"disk1.vmdk\""));
    Assert.That(reader.ManifestVerifies(), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Writer_DroppedSuppliedManifest_RegeneratesCorrectOne() {
    // A stale/wrong manifest input must be discarded and regenerated.
    var ova = new OvaWriter()
      .Add("vm.ovf", Encoding.UTF8.GetBytes(OvfXml))
      .Add("disk1.vmdk", Disk1)
      .Add("vm.mf", Encoding.UTF8.GetBytes("SHA256(disk1.vmdk)= deadbeef\n"))
      .ToArray();

    var reader = OvaReader.Read(new MemoryStream(ova));
    Assert.That(reader.Members.Count(m => m.Name.EndsWith(".mf")), Is.EqualTo(1));
    Assert.That(reader.ManifestVerifies(), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_RoundTripsByteIdentical() {
    var d = new OvaFormatDescriptor();
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("vm.ovf", Encoding.UTF8.GetBytes(OvfXml)),
      ArchiveInputInfo.InMemory("disk1.vmdk", Disk1),
    };

    using var first = new MemoryStream();
    d.Create(first, inputs, new FormatCreateOptions());
    var firstBytes = first.ToArray();

    // Reader sees the OVF + disk + a verifying manifest.
    var reader = OvaReader.Read(new MemoryStream(firstBytes));
    Assert.That(reader.Ovf, Is.Not.Null);
    Assert.That(reader.Disks.Single().Data, Is.EqualTo(Disk1));
    Assert.That(reader.ManifestVerifies(), Is.True);

    // Determinism: re-create from the same inputs yields identical bytes.
    using var second = new MemoryStream();
    d.Create(second, inputs, new FormatCreateOptions());
    Assert.That(second.ToArray(), Is.EqualTo(firstBytes));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AddAndRemove_RebuildManifestStaysConsistent() {
    var d = new OvaFormatDescriptor();
    using var archive = new MemoryStream();
    d.Create(archive, [
      ArchiveInputInfo.InMemory("vm.ovf", Encoding.UTF8.GetBytes(OvfXml)),
      ArchiveInputInfo.InMemory("disk1.vmdk", Disk1),
    ], new FormatCreateOptions());

    // Add a second disk.
    d.Add(archive, [ArchiveInputInfo.InMemory("disk2.vmdk", Disk2)]);
    archive.Position = 0;
    var afterAdd = OvaReader.Read(archive);
    Assert.That(afterAdd.Disks.Count(), Is.EqualTo(2));
    Assert.That(afterAdd.ManifestVerifies(), Is.True);

    // Remove the first disk.
    d.Remove(archive, ["disk1.vmdk"]);
    archive.Position = 0;
    var afterRemove = OvaReader.Read(archive);
    Assert.That(afterRemove.Members.Any(m => m.Name == "disk1.vmdk"), Is.False);
    Assert.That(afterRemove.Disks.Single().Data, Is.EqualTo(Disk2));
    Assert.That(afterRemove.ManifestVerifies(), Is.True);
  }

  [Test, Category("BoundaryValue")]
  public void Writer_RejectsEmptyInput() {
    Assert.Throws<InvalidOperationException>(() => new OvaWriter().ToArray());
  }

  [Test, Category("HappyPath")]
  public void ParseManifest_HandlesSha1AndWhitespace() {
    const string mf = "SHA1(a.vmdk)=ABCDEF\nSHA256(b.vmdk)=  cafe \n\n# not a line\n";
    var parsed = OvaReader.ParseManifest(mf).ToList();
    Assert.That(parsed.Count, Is.EqualTo(2));
    Assert.That(parsed[0], Is.EqualTo(("SHA1", "a.vmdk", "ABCDEF")));
    Assert.That(parsed[1], Is.EqualTo(("SHA256", "b.vmdk", "cafe")));
  }

  // ── Interop: a third-party tar must read our OVA ────────────────────

  private static string? FindTar() => ToolDiscovery.GetToolPath("tar") ?? ToolDiscovery.GetToolPath("bsdtar");

  private static string ToMsysPath(string windowsPath) {
    if (windowsPath.Length >= 2 && windowsPath[1] == ':') {
      var drive = char.ToLowerInvariant(windowsPath[0]);
      return "/" + drive + windowsPath[2..].Replace('\\', '/');
    }
    return windowsPath.Replace('\\', '/');
  }

  private static bool IsMsysTool(string toolPath)
    => toolPath.Contains("Git", StringComparison.OrdinalIgnoreCase) &&
       toolPath.Contains("usr", StringComparison.OrdinalIgnoreCase);

  private static (string StdOut, string StdErr, int ExitCode) RunTool(string toolPath, string args, int timeoutMs = 30_000) {
    var psi = new ProcessStartInfo {
      FileName = toolPath, Arguments = args,
      RedirectStandardOutput = true, RedirectStandardError = true,
      UseShellExecute = false, CreateNoWindow = true,
    };
    using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {toolPath}");
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    if (!proc.WaitForExit(timeoutMs)) {
      try { proc.Kill(); } catch { /* best effort */ }
      Assert.Fail($"{Path.GetFileName(toolPath)} timed out after {timeoutMs}ms.");
    }
    return (stdout, stderr, proc.ExitCode);
  }

  [Test, Category("Interop")]
  public void ExternalTar_ListsOurOvaMembersInOrder() {
    var tar = FindTar() ?? throw new IgnoreException("tar/bsdtar not found on PATH or in common locations.");

    var ova = new OvaWriter()
      .Add("vm.ovf", Encoding.UTF8.GetBytes(OvfXml))
      .Add("disk1.vmdk", Disk1)
      .Add("disk2.vmdk", Disk2)
      .ToArray();

    var dir = Path.Combine(Path.GetTempPath(), "ova_interop_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var ovaPath = Path.Combine(dir, "appliance.ova");
      File.WriteAllBytes(ovaPath, ova);

      var msys = IsMsysTool(tar);
      var arg = msys ? ToMsysPath(ovaPath) : ovaPath;
      var (stdout, stderr, exit) = RunTool(tar, $"tf \"{arg}\"");
      Assert.That(exit, Is.EqualTo(0), $"tar tf failed: {stderr}");

      var listed = stdout.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
      Assert.That(listed[0], Does.EndWith(".ovf"), "External tar must see the OVF first.");
      Assert.That(listed, Does.Contain("disk1.vmdk"));
      Assert.That(listed, Does.Contain("disk2.vmdk"));
      Assert.That(listed[^1], Does.EndWith(".mf"), "External tar must see the manifest last.");
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Interop")]
  public void ExternalTar_ExtractsManifestThatMatchesDiskBytes() {
    var tar = FindTar() ?? throw new IgnoreException("tar/bsdtar not found on PATH or in common locations.");

    var ova = new OvaWriter()
      .Add("disk1.vmdk", Disk1)
      .ToArray();

    var dir = Path.Combine(Path.GetTempPath(), "ova_interop2_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var ovaPath = Path.Combine(dir, "appliance.ova");
      File.WriteAllBytes(ovaPath, ova);

      var msys = IsMsysTool(tar);
      var arg = msys ? ToMsysPath(ovaPath) : ovaPath;
      var outDir = msys ? ToMsysPath(dir) : dir;
      var (_, stderr, exit) = RunTool(tar, $"xf \"{arg}\" -C \"{outDir}\"");
      Assert.That(exit, Is.EqualTo(0), $"tar xf failed: {stderr}");

      var disk = File.ReadAllBytes(Path.Combine(dir, "disk1.vmdk"));
      Assert.That(disk, Is.EqualTo(Disk1), "Third-party tar must extract the disk byte-identically.");

      var mfPath = Directory.GetFiles(dir, "*.mf").Single();
      var mfText = File.ReadAllText(mfPath);
      var line = OvaReader.ParseManifest(mfText).Single(l => l.FileName == "disk1.vmdk");
      var actual = Convert.ToHexStringLower(SHA256.HashData(disk));
      Assert.That(line.Expected, Is.EqualTo(actual), "Generated .mf SHA-256 must match the extracted disk bytes.");
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
