using System.Text;
using FileFormat.Veeam;

namespace Compression.Tests.Veeam;

/// <summary>
/// Stage-1 acceptance gates for the embedded <c>&lt;OibSummary&gt;</c> XML
/// metadata island that Veeam Backup &amp; Replication writes near the end of
/// an unencrypted Storage file. Provenance for the field shapes is
/// Synacktiv's two-part 2024 write-up "Using Veeam metadata for efficient
/// extraction of Backup artefacts" plus their open-source
/// <c>Windows.Veeam.RestorePoints.BackupFiles</c> Velociraptor artifact —
/// the YARA-rule "last occurrence" semantics and the <c>AttrXxx</c>-mapped
/// XML attribute names come straight from that artifact pack.
/// </summary>
[TestFixture]
public class VeeamOibSummaryTests {

  // Builds a synthetic Veeam-shaped image: VEEAM tag near the start, then
  // an arbitrary payload, then the OibSummary trailer XML. Mirrors the
  // observed real-world layout (chunk skeleton up front, plaintext XML
  // island near the trailing edge).
  private static byte[] BuildWithTrailer(string oibSummaryXml, int leadingPadding = 256) {
    var prefix = new byte[16 + leadingPadding];
    Encoding.ASCII.GetBytes("VEEAM").CopyTo(prefix.AsSpan(0, 5));
    prefix[5] = 0xDE; prefix[6] = 0xAD; prefix[7] = 0xBE; prefix[8] = 0xEF;
    for (var i = 16; i < prefix.Length; i++) prefix[i] = (byte)(i & 0xFF);
    var xmlBytes = Encoding.UTF8.GetBytes(oibSummaryXml);
    var trailer = new byte[64];
    for (var i = 0; i < trailer.Length; i++) trailer[i] = (byte)(i ^ 0x5A);
    var image = new byte[prefix.Length + xmlBytes.Length + trailer.Length];
    Buffer.BlockCopy(prefix, 0, image, 0, prefix.Length);
    Buffer.BlockCopy(xmlBytes, 0, image, prefix.Length, xmlBytes.Length);
    Buffer.BlockCopy(trailer, 0, image, prefix.Length + xmlBytes.Length, trailer.Length);
    return image;
  }

  private const string SyntheticOibSummary = """
    <OibSummary>
      <Backup JobName="Backup Job Hyper-V VMs" Type="0" Encryption="0" />
      <Point Num="3" CreationTime="2024-02-29 19:06:52" CreationTimeUtc="2024-02-29 18:06:52" />
      <Storage PartialPath="vsphere-windows10-vm.3D2024-02-29T190652_AB4B.vbk" />
      <OIB DisplayName="vsphere-windows10-vm" />
      <Object Name="vsphere-windows10-vm" Id="abcd1234-5678-90ab-cdef-1234567890ab" />
      <SourceHost Name="esxi01.lab.internal" />
      <TargetHost Name="backup-repo01.lab.internal" />
      <PrevFileName>C:\Backup\Backup Job vSphere\vsphere-windows10-vm.3D2024-02-28T190652_AB4A.vbk</PrevFileName>
      <BackupVersion>2</BackupVersion>
      <OibFiles>
        <File Name="vsphere-windows10-vm-flat.vmdk" Size="42949672960">
          <PlatformDetails Platform="vSphere" Adapter="lsilogic" />
        </File>
        <File Name="vsphere-windows10-vm.nvram" Size="8684" />
      </OibFiles>
    </OibSummary>
    """;

  [Test, Category("HappyPath")]
  public void Parser_ExtractsAllDocumentedFields() {
    var oib = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(SyntheticOibSummary));
    Assert.That(oib, Is.Not.Null, "Parser must accept the canonical OibSummary shape from Synacktiv research.");
    Assert.That(oib!.JobName, Is.EqualTo("Backup Job Hyper-V VMs"));
    Assert.That(oib.BackupTypeCode, Is.EqualTo(0), "Synacktiv: Type=0 means Full backup.");
    Assert.That(oib.EncryptionCode, Is.EqualTo(0), "Velociraptor mapping: Encryption=0 is Unencrypted (the only case where OibSummary is plaintext).");
    Assert.That(oib.RestorePointNumber, Is.EqualTo(3));
    Assert.That(oib.CreationTime, Is.EqualTo("2024-02-29 19:06:52"));
    Assert.That(oib.CreationTimeUtc, Is.EqualTo("2024-02-29 18:06:52"));
    Assert.That(oib.StoragePartialPath, Is.EqualTo("vsphere-windows10-vm.3D2024-02-29T190652_AB4B.vbk"));
    Assert.That(oib.OibDisplayName, Is.EqualTo("vsphere-windows10-vm"));
    Assert.That(oib.ObjectName, Is.EqualTo("vsphere-windows10-vm"));
    Assert.That(oib.ObjectId, Is.EqualTo("abcd1234-5678-90ab-cdef-1234567890ab"));
    Assert.That(oib.SourceHostName, Is.EqualTo("esxi01.lab.internal"));
    Assert.That(oib.TargetHostName, Is.EqualTo("backup-repo01.lab.internal"));
    Assert.That(oib.PrevFileName, Does.Contain("vsphere-windows10-vm.3D2024-02-28T190652_AB4A.vbk"));
    Assert.That(oib.BackupVersion, Is.EqualTo("2"));
    Assert.That(oib.OibFiles, Has.Count.EqualTo(2));
    Assert.That(oib.OibFiles[0].Name, Is.EqualTo("vsphere-windows10-vm-flat.vmdk"));
    Assert.That(oib.OibFiles[0].Size, Is.EqualTo(42949672960L));
    Assert.That(oib.OibFiles[0].PlatformDetails["Platform"], Is.EqualTo("vSphere"));
    Assert.That(oib.OibFiles[0].PlatformDetails["Adapter"], Is.EqualTo("lsilogic"));
    Assert.That(oib.OibFiles[1].Name, Is.EqualTo("vsphere-windows10-vm.nvram"));
    Assert.That(oib.OibFiles[1].Size, Is.EqualTo(8684));
    Assert.That(oib.OibFiles[1].PlatformDetails, Is.Empty,
      "Files without PlatformDetails must surface an empty (not null) dictionary.");
  }

  [Test, Category("HappyPath")]
  public void Parser_PicksLastOccurrence_PerVelociraptorYaraRule() {
    // Synacktiv's YARA rule (StartOffsetRule { strings: $start = "<OibSummary>" })
    // picks the LAST occurrence in the file — earlier inline copies inside
    // compressed metadata banks must be ignored. Pin that semantic.
    const string stale = "<OibSummary><Backup JobName=\"STALE — must be ignored\" /></OibSummary>";
    const string fresh = "<OibSummary><Backup JobName=\"AUTHORITATIVE TRAILER\" /></OibSummary>";
    var combined = Encoding.UTF8.GetBytes(stale + "\n\nfiller_bytes_between\n\n" + fresh);
    var oib = OibSummaryParser.TryParse(combined);
    Assert.That(oib, Is.Not.Null);
    Assert.That(oib!.JobName, Is.EqualTo("AUTHORITATIVE TRAILER"),
      "Parser must adopt the LAST OibSummary occurrence (Synacktiv/Velociraptor YARA rule).");
  }

  [Test, Category("HappyPath")]
  public void Parser_SetsOffsetAndLengthForDiagnostics() {
    var leading = new byte[1024];
    for (var i = 0; i < leading.Length; i++) leading[i] = (byte)i;
    var xml = Encoding.UTF8.GetBytes(SyntheticOibSummary);
    var combined = new byte[leading.Length + xml.Length + 16];
    Buffer.BlockCopy(leading, 0, combined, 0, leading.Length);
    Buffer.BlockCopy(xml, 0, combined, leading.Length, xml.Length);
    var oib = OibSummaryParser.TryParse(combined);
    Assert.That(oib, Is.Not.Null);
    Assert.That(oib!.XmlOffset, Is.EqualTo(leading.Length),
      "XmlOffset must point at the '<' of the open tag.");
    Assert.That(oib.XmlLength, Is.EqualTo(xml.Length),
      "XmlLength must span the full XML island including the close tag.");
    Assert.That(oib.RawXml, Is.Not.Null.And.Contains("<OibSummary>").And.Contains("</OibSummary>"));
  }

  [Test, Category("ExceptionalCase")]
  public void Parser_ReturnsNullWhenNoOpenTagPresent() {
    var bogus = new byte[2048];
    for (var i = 0; i < bogus.Length; i++) bogus[i] = (byte)(i & 0x7F);
    var oib = OibSummaryParser.TryParse(bogus);
    Assert.That(oib, Is.Null,
      "Encrypted/pre-trailer containers carry no plaintext <OibSummary> tag — parser must return null cleanly.");
  }

  [Test, Category("ExceptionalCase")]
  public void Parser_ReturnsNullWhenCloseTagMissing() {
    var truncated = Encoding.UTF8.GetBytes("<OibSummary><Backup JobName=\"truncated\" /><Point Num=\"1\" />");
    var oib = OibSummaryParser.TryParse(truncated);
    Assert.That(oib, Is.Null,
      "An opening <OibSummary> without a matching close tag must NOT throw — degrade to null.");
  }

  [Test, Category("ExceptionalCase")]
  public void Parser_ReturnsNullOnMalformedXml() {
    // Open + close tag both present but the contents are not well-formed XML.
    var bad = Encoding.UTF8.GetBytes("<OibSummary><Backup JobName=unquoted /></OibSummary>");
    var oib = OibSummaryParser.TryParse(bad);
    Assert.That(oib, Is.Null,
      "Malformed XML between the tags must degrade gracefully — Stage-1 must never throw past detection.");
  }

  [Test, Category("BoundaryCase")]
  public void Parser_HandlesMissingOptionalElements() {
    // Real-world OibSummary blocks vary by VBR version and platform —
    // missing optional children must surface as null fields, not throw.
    const string minimal = "<OibSummary><Backup JobName=\"minimal\" /></OibSummary>";
    var oib = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(minimal));
    Assert.That(oib, Is.Not.Null);
    Assert.That(oib!.JobName, Is.EqualTo("minimal"));
    Assert.That(oib.RestorePointNumber, Is.Null);
    Assert.That(oib.CreationTime, Is.Null);
    Assert.That(oib.SourceHostName, Is.Null);
    Assert.That(oib.OibFiles, Is.Empty);
    Assert.That(oib.PrevFileName, Is.Null,
      "PrevFileName is only present for incremental/reverse-incremental chains — first-full backups omit it.");
  }

  [Test, Category("BoundaryCase")]
  public void Parser_ToleratesNonIntegerAttributesByLeavingNumericFieldsNull() {
    // Defensive: if a writer ever emits Type/Num as something other than a
    // base-10 integer, we must NOT throw — leave the typed field null.
    const string xml = "<OibSummary><Backup JobName=\"odd\" Type=\"NOT_AN_INT\" /><Point Num=\"NaN\" /></OibSummary>";
    var oib = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(xml));
    Assert.That(oib, Is.Not.Null);
    Assert.That(oib!.BackupTypeCode, Is.Null);
    Assert.That(oib.RestorePointNumber, Is.Null);
    Assert.That(oib.JobName, Is.EqualTo("odd"));
  }

  [Test, Category("HappyPath")]
  public void Reader_SurfacesOibSummary_WhenTrailerPresent() {
    var image = BuildWithTrailer(SyntheticOibSummary);
    using var ms = new MemoryStream(image);
    using var r = new VeeamReader(ms, VeeamFileType.Full);
    Assert.That(r.OibSummary, Is.Not.Null);
    Assert.That(r.OibSummary!.JobName, Is.EqualTo("Backup Job Hyper-V VMs"));
    Assert.That(r.OibSummary.RestorePointNumber, Is.EqualTo(3));
    Assert.That(r.Entries.Select(e => e.Name), Does.Contain("OibSummary.xml"),
      "Reader must surface the raw XML island as its own entry when present.");
  }

  [Test, Category("HappyPath")]
  public void Reader_OmitsOibSummaryXmlEntry_WhenTrailerAbsent() {
    // No OibSummary in the synthetic data → no separate XML entry, Stage 0 fallback.
    var image = new byte[2048];
    Encoding.ASCII.GetBytes("VEEAM").CopyTo(image.AsSpan(0, 5));
    using var ms = new MemoryStream(image);
    using var r = new VeeamReader(ms, VeeamFileType.Full);
    Assert.That(r.OibSummary, Is.Null);
    Assert.That(r.Entries.Select(e => e.Name), Does.Not.Contain("OibSummary.xml"));
    Assert.That(r.Entries, Has.Count.EqualTo(2),
      "Without an OibSummary trailer the entries must be exactly metadata.ini + raw payload.");
  }

  [Test, Category("HappyPath")]
  public void MetadataIni_PromotesToStage1_WhenOibSummaryFound() {
    var image = BuildWithTrailer(SyntheticOibSummary);
    using var ms = new MemoryStream(image);
    using var r = new VeeamReader(ms, VeeamFileType.Full);
    var ini = Encoding.UTF8.GetString(r.Entries.First(e => e.Name == "metadata.ini").Data);
    Assert.That(ini, Does.Contain("stage=1"));
    Assert.That(ini, Does.Contain("parse_status=metadata-only"));
    Assert.That(ini, Does.Contain("oib_summary_found=true"));
    Assert.That(ini, Does.Contain("job_name=Backup Job Hyper-V VMs"));
    Assert.That(ini, Does.Contain("restore_point_number=3"));
    Assert.That(ini, Does.Contain("backup_type_code=0 (Full)"));
    Assert.That(ini, Does.Contain("encryption_code=0 (Unencrypted)"));
    Assert.That(ini, Does.Contain("object_name=vsphere-windows10-vm"));
    Assert.That(ini, Does.Contain("source_host_name=esxi01.lab.internal"));
    Assert.That(ini, Does.Contain("prev_file_name="));
    Assert.That(ini, Does.Contain("backup_version=2"));
    Assert.That(ini, Does.Contain("oib_file_count=2"));
    Assert.That(ini, Does.Contain("oib_file_0_name=vsphere-windows10-vm-flat.vmdk"));
    Assert.That(ini, Does.Contain("treatment=Stage 1"),
      "Stage-1 metadata.ini must announce that disk content stays Stage 0 even when XML metadata is recovered.");
    Assert.That(ini, Does.Contain("ro_promotion=blocked_for_disk_content"),
      "Promotion is for METADATA only — disk content R/O remains blocked. metadata.ini must say so explicitly.");
  }

  [Test, Category("HappyPath")]
  public void MetadataIni_KeepsStage0Disclaimer_WhenOibSummaryAbsent() {
    // Encrypted backups and pre-trailer writer versions yield no XML →
    // metadata.ini must report Stage 0 honestly, not pretend metadata is
    // available.
    var image = new byte[1024];
    Encoding.ASCII.GetBytes("VEEAM").CopyTo(image.AsSpan(0, 5));
    using var ms = new MemoryStream(image);
    using var r = new VeeamReader(ms, VeeamFileType.Full);
    var ini = Encoding.UTF8.GetString(r.Entries.First(e => e.Name == "metadata.ini").Data);
    Assert.That(ini, Does.Contain("stage=0"));
    Assert.That(ini, Does.Contain("parse_status=detection-only"));
    Assert.That(ini, Does.Contain("oib_summary_found=false"));
    Assert.That(ini, Does.Contain("oib_summary_reason="));
    Assert.That(ini, Does.Contain("treatment=Stage 0 confirmed"));
  }

  [Test, Category("HappyPath")]
  public void Description_NamesStage1AndOibSummary() {
    var d = new VeeamFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("stage 1").Or.Contain("stage-1"),
      "Description must announce the Stage-1 promotion for the OibSummary XML island.");
    Assert.That(desc, Does.Contain("oibsummary"),
      "Description must name the OibSummary XML island so consumers can audit the source of the metadata.");
    Assert.That(desc, Does.Contain("synacktiv").Or.Contain("velociraptor"),
      "Description must cite the Synacktiv/Velociraptor reverse-engineering provenance.");
    Assert.That(desc, Does.Contain("stage 0"),
      "Description must honestly state that disk content stays Stage 0 even after Stage-1 metadata extraction.");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ListSurfacesOibSummaryEntry_WhenPresent() {
    var image = BuildWithTrailer(SyntheticOibSummary);
    using var ms = new MemoryStream(image);
    var d = new VeeamFormatDescriptor();
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Has.Member("metadata.ini"));
    Assert.That(names, Has.Member("OibSummary.xml"));
    Assert.That(names.Count(n => n.EndsWith(".bin", StringComparison.Ordinal)), Is.EqualTo(1));
    Assert.That(entries.Count, Is.EqualTo(3),
      "With trailer present, descriptor lists exactly metadata.ini + OibSummary.xml + raw payload.");
  }
}
