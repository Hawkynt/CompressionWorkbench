using System.Text;
using Compression.Registry;
using FileFormat.Veeam;

namespace Compression.Tests.Veeam;

[TestFixture]
public class VeeamTests {

  // ── Fixture helpers ────────────────────────────────────────────────

  private const string SampleOibSummaryXml =
    "<OibSummary>" +
      "<OIB>" +
        "<DisplayName>FILE-SERVER</DisplayName>" +
        "<VmName>FILE-SERVER</VmName>" +
        "<OibType>Vm</OibType>" +
        "<Type>Full</Type>" +
        "<Algorithm>Incremental</Algorithm>" +
        "<CreationTimeUtc>2024-08-12T03:00:00Z</CreationTimeUtc>" +
        "<CompletionTimeUtc>2024-08-12T03:14:22Z</CompletionTimeUtc>" +
        "<IsCorrupted>false</IsCorrupted>" +
      "</OIB>" +
      "<SourceHost>" +
        "<Name>vcenter-01.lab.local</Name>" +
        "<InstanceId>00000000-1111-2222-3333-444444444444</InstanceId>" +
      "</SourceHost>" +
      "<Backup>" +
        "<JobName>Daily-FileServers</JobName>" +
        "<PolicyName>Gold</PolicyName>" +
        "<Encrypted>false</Encrypted>" +
      "</Backup>" +
      "<Object>" +
        "<Name>FILE-SERVER</Name>" +
        "<Id>vm-1234</Id>" +
      "</Object>" +
      "<Point>" +
        "<Number>17</Number>" +
        "<Type>Full</Type>" +
      "</Point>" +
      "<Storage>" +
        "<PartialPath>Daily-FileServers/FILE-SERVER.vbk</PartialPath>" +
      "</Storage>" +
      "<PrevFileName>FILE-SERVER2024-08-11.vib</PrevFileName>" +
      "<OibFiles>" +
        "<File Name=\"C:\\Windows\\System32\\config\\SAM\" Size=\"262144\" />" +
        "<File Name=\"D:\\Shares\\Finance\\Q3-Report.xlsx\" Size=\"1048576\" />" +
        "<File Name=\"E:\\Data\\db.mdf\" Size=\"10737418240\" />" +
      "</OibFiles>" +
    "</OibSummary>";

  private static byte[] BuildFixture(string xml = SampleOibSummaryXml, int leadingNoiseBytes = 4096) {
    using var ms = new MemoryStream();
    // Simulate a header + opaque chunk data — fill with non-XML noise so the
    // reverse-scan must actually locate the trailing OibSummary island.
    var noise = new byte[leadingNoiseBytes];
    for (var i = 0; i < noise.Length; i++) noise[i] = (byte)(i ^ 0x5A);
    ms.Write(noise);
    ms.Write(Encoding.UTF8.GetBytes(xml));
    // Trailing pad — Veeam keeps the XML near (but not necessarily at) EOF.
    var tail = new byte[64];
    for (var i = 0; i < tail.Length; i++) tail[i] = 0xCC;
    ms.Write(tail);
    return ms.ToArray();
  }

  // ── Descriptor metadata (HappyPath) ────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new VeeamFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d.Id, Is.EqualTo("Veeam"));
      Assert.That(d.DisplayName, Is.EqualTo("Veeam Backup"));
      Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
      Assert.That(d.Extensions, Is.EquivalentTo(new[] { ".vbk", ".vib", ".vrb" }));
      Assert.That(d.DefaultExtension, Is.EqualTo(".vbk"));
      Assert.That(d.MagicSignatures, Is.Empty, "No public magic bytes — detection by extension only.");
      Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList));
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract));
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest));
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False,
        "Stage-1 R/O — never advertise CanCreate without real chunk-format compliance.");
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    });
  }

  // ── OibSummary locator (HappyPath + boundary) ──────────────────────

  [Test, Category("HappyPath")]
  public void TryExtract_FindsTrailingOibSummary() {
    using var ms = new MemoryStream(BuildFixture());
    var xml = VeeamOibSummary.TryExtract(ms);
    Assert.That(xml, Is.Not.Null);
    var s = Encoding.UTF8.GetString(xml!);
    Assert.That(s, Does.StartWith("<OibSummary>"));
    Assert.That(s, Does.EndWith("</OibSummary>"));
    Assert.That(s, Does.Contain("Daily-FileServers"));
  }

  [Test, Category("Boundary")]
  public void TryExtract_PicksLastOccurrence_WhenMultiplePresent() {
    // A real backup chain may contain references to the previous file's
    // metadata; the recovery convention is to take the *last* occurrence.
    var stale = SampleOibSummaryXml.Replace("Daily-FileServers", "STALE-OLDER-COPY");
    var data = Encoding.UTF8.GetBytes(stale + new string('x', 1024) + SampleOibSummaryXml);
    using var ms = new MemoryStream(data);
    var xml = VeeamOibSummary.TryExtract(ms);
    Assert.That(xml, Is.Not.Null);
    var s = Encoding.UTF8.GetString(xml!);
    Assert.That(s, Does.Contain("Daily-FileServers"));
    Assert.That(s, Does.Not.Contain("STALE-OLDER-COPY"));
  }

  [Test, Category("Boundary")]
  public void TryExtract_LocatesTagStraddlingPageBoundary() {
    // The reverse-scanner uses 64 KiB pages. Place the open tag so its bytes
    // straddle the page boundary to exercise the overlap logic.
    const int targetOffset = 64 * 1024 - 5; // open tag (12 bytes) crosses 64 KiB
    var prefix = new byte[targetOffset];
    for (var i = 0; i < prefix.Length; i++) prefix[i] = (byte)(i & 0x7F); // pure ASCII noise, no '<'
    using var ms = new MemoryStream();
    ms.Write(prefix);
    ms.Write(Encoding.UTF8.GetBytes(SampleOibSummaryXml));
    ms.Position = 0;

    var xml = VeeamOibSummary.TryExtract(ms);
    Assert.That(xml, Is.Not.Null);
    Assert.That(Encoding.UTF8.GetString(xml!), Does.StartWith("<OibSummary>"));
  }

  // ── XML parsing (HappyPath) ────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void TryParse_ReturnsAllSummaryFields() {
    var info = VeeamOibSummary.TryParse(Encoding.UTF8.GetBytes(SampleOibSummaryXml));
    Assert.That(info, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(info!.VmName, Is.EqualTo("FILE-SERVER"));
      Assert.That(info.OibType, Is.EqualTo("Vm"));
      Assert.That(info.BackupType, Is.EqualTo("Full"));
      Assert.That(info.Algorithm, Is.EqualTo("Incremental"));
      Assert.That(info.CreationTimeUtc, Is.EqualTo("2024-08-12T03:00:00Z"));
      Assert.That(info.IsCorrupted, Is.EqualTo(false));
      Assert.That(info.SourceHostName, Is.EqualTo("vcenter-01.lab.local"));
      Assert.That(info.JobName, Is.EqualTo("Daily-FileServers"));
      Assert.That(info.PolicyName, Is.EqualTo("Gold"));
      Assert.That(info.IsEncrypted, Is.EqualTo(false));
      Assert.That(info.ObjectName, Is.EqualTo("FILE-SERVER"));
      Assert.That(info.ObjectId, Is.EqualTo("vm-1234"));
      Assert.That(info.PointNumber, Is.EqualTo(17));
      Assert.That(info.PointType, Is.EqualTo("Full"));
      Assert.That(info.PrevFileName, Is.EqualTo("FILE-SERVER2024-08-11.vib"));
      Assert.That(info.StoragePartialPath, Is.EqualTo("Daily-FileServers/FILE-SERVER.vbk"));
      Assert.That(info.Files, Has.Count.EqualTo(3));
      Assert.That(info.Files[2].Name, Is.EqualTo(@"E:\Data\db.mdf"));
      Assert.That(info.Files[2].Size, Is.EqualTo(10737418240L));
    });
  }

  // ── Descriptor List (HappyPath) ────────────────────────────────────

  [Test, Category("HappyPath")]
  public void List_EmitsFullPlusMetadataPlusOibSummaryPlusGuestFiles() {
    var d = new VeeamFormatDescriptor();
    using var ms = new MemoryStream(BuildFixture());
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.Multiple(() => {
      Assert.That(names, Has.Member("FULL.vbk"));
      Assert.That(names, Has.Member("metadata.ini"));
      Assert.That(names, Has.Member("OibSummary.xml"));
      Assert.That(names, Has.Member("guest_files/"));
      Assert.That(names.Any(n => n.StartsWith("guest_files/") && n.EndsWith("db.mdf")), Is.True);
      // Path separators normalized to forward slashes
      Assert.That(names.Any(n => n.Contains('\\')), Is.False);
    });
  }

  // ── Descriptor Extract (HappyPath) ─────────────────────────────────

  [Test, Category("HappyPath")]
  public void Extract_WritesAllExpectedFiles() {
    var d = new VeeamFormatDescriptor();
    var fixture = BuildFixture();
    var tmp = Path.Combine(Path.GetTempPath(), "veeam-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(fixture);
      d.Extract(ms, tmp, null, null);

      Assert.Multiple(() => {
        Assert.That(File.Exists(Path.Combine(tmp, "FULL.vbk")));
        Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")));
        Assert.That(File.Exists(Path.Combine(tmp, "OibSummary.xml")));
      });

      var full = File.ReadAllBytes(Path.Combine(tmp, "FULL.vbk"));
      Assert.That(full, Is.EqualTo(fixture), "FULL.* must be a byte-identical pass-through.");

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("parse_status=ok"));
      Assert.That(ini, Does.Contain("vm_name=FILE-SERVER"));
      Assert.That(ini, Does.Contain("job_name=Daily-FileServers"));
      Assert.That(ini, Does.Contain("is_encrypted=false"));
      Assert.That(ini, Does.Contain("guest_file_count=3"));

      var xmlOut = File.ReadAllText(Path.Combine(tmp, "OibSummary.xml"));
      Assert.That(xmlOut, Does.StartWith("<OibSummary>"));
      Assert.That(xmlOut, Does.EndWith("</OibSummary>"));

      // Guest-file placeholders are 0-byte stubs.
      var guestFile = Path.Combine(tmp, "guest_files", "Data", "db.mdf");
      Assert.That(File.Exists(guestFile));
      Assert.That(new FileInfo(guestFile).Length, Is.EqualTo(0));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_RespectsFileFilter() {
    var d = new VeeamFormatDescriptor();
    var tmp = Path.Combine(Path.GetTempPath(), "veeam-filter-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(BuildFixture());
      d.Extract(ms, tmp, null, new[] { "metadata.ini" });
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")));
      Assert.That(File.Exists(Path.Combine(tmp, "FULL.vbk")), Is.False);
      Assert.That(File.Exists(Path.Combine(tmp, "OibSummary.xml")), Is.False);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  // ── Exception / robustness cases ───────────────────────────────────

  [Test, Category("Exception")]
  public void TryExtract_ReturnsNull_OnGarbageWithoutMarkers() {
    var junk = new byte[8 * 1024];
    Array.Fill(junk, (byte)0xAA);
    using var ms = new MemoryStream(junk);
    Assert.That(VeeamOibSummary.TryExtract(ms), Is.Null);
  }

  [Test, Category("Exception")]
  public void TryExtract_ReturnsNull_WhenOpenTagPresentButCloseTagMissing() {
    var data = new List<byte>();
    data.AddRange(new byte[1024]);
    data.AddRange(Encoding.UTF8.GetBytes("<OibSummary><OIB><Name>x</Name></OIB>"));
    using var ms = new MemoryStream(data.ToArray());
    Assert.That(VeeamOibSummary.TryExtract(ms), Is.Null);
  }

  [Test, Category("Exception")]
  public void TryExtract_ReturnsNull_OnEmptyStream() {
    using var ms = new MemoryStream();
    Assert.That(VeeamOibSummary.TryExtract(ms), Is.Null);
  }

  [Test, Category("Exception")]
  public void TryParse_ReturnsNull_OnMalformedXml() {
    var bad = Encoding.UTF8.GetBytes("<OibSummary><not closed");
    Assert.That(VeeamOibSummary.TryParse(bad), Is.Null);
  }

  [Test, Category("Exception")]
  public void TryParse_ReturnsNull_OnNullOrEmptyInput() {
    Assert.That(VeeamOibSummary.TryParse(null), Is.Null);
    Assert.That(VeeamOibSummary.TryParse(Array.Empty<byte>()), Is.Null);
  }

  [Test, Category("Exception")]
  public void Extract_WritesPartialMetadata_OnGarbageInput() {
    var d = new VeeamFormatDescriptor();
    var junk = new byte[1024];
    Array.Fill(junk, (byte)0x77);
    var tmp = Path.Combine(Path.GetTempPath(), "veeam-junk-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(junk);
      Assert.DoesNotThrow(() => d.Extract(ms, tmp, null, null));
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")));
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("parse_status=partial"));
      Assert.That(File.Exists(Path.Combine(tmp, "OibSummary.xml")), Is.False);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("Exception")]
  public void Extract_WritesPartialMetadata_WhenXmlMalformed() {
    var d = new VeeamFormatDescriptor();
    var bad = new List<byte>();
    bad.AddRange(new byte[1024]);
    bad.AddRange(Encoding.UTF8.GetBytes("<OibSummary><not-well-formed></OibSummary>"));
    bad.AddRange(new byte[32]);
    var tmp = Path.Combine(Path.GetTempPath(), "veeam-malformed-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(bad.ToArray());
      Assert.DoesNotThrow(() => d.Extract(ms, tmp, null, null));
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")));
      // The locator found the tags so the raw XML is still emitted, but the
      // parser fails -> partial status in metadata.ini.
      Assert.That(File.Exists(Path.Combine(tmp, "OibSummary.xml")));
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("parse_status=partial"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void List_DoesNotThrow_OnGarbage() {
    var d = new VeeamFormatDescriptor();
    using var ms = new MemoryStream(new byte[64]);
    Assert.DoesNotThrow(() => d.List(ms, null));
  }
}
