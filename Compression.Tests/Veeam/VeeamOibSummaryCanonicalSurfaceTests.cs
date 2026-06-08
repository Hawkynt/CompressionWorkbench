using System.Text;
using FileFormat.Veeam;

namespace Compression.Tests.Veeam;

/// <summary>
/// Stage-1 canonical-attribute-surface gates for the embedded
/// <c>&lt;OibSummary&gt;</c> XML metadata island. These tests pin the full
/// attribute-set documented by Synacktiv's
/// <c>Windows.Veeam.RestorePoints.BackupFiles</c> Velociraptor artifact
/// (Artifact Exchange) — every <c>AttrXxx</c> mapping in that VQL has a
/// matching field on <see cref="OibSummary"/>, and missing attributes
/// degrade to <c>null</c> rather than throwing.
///
/// <para>
/// These tests are intentionally NEW (in addition to
/// <c>VeeamOibSummaryTests</c>) so the canonical-surface promotion is
/// pinned separately from the original baseline shape. A future refactor
/// that drops one of the canonical attributes will trip these gates
/// before downstream forensics consumers notice the regression.
/// </para>
/// </summary>
[TestFixture]
public class VeeamOibSummaryCanonicalSurfaceTests {

  // Canonical Velociraptor-artifact OibSummary shape: every attribute name
  // that Windows.Veeam.RestorePoints.BackupFiles maps via AttrXxx in its
  // VQL is represented here. Synacktiv's per-OIB columns: VmName, State,
  // Type, Algorithm, HealthStatus, CreationTimeUtc, CompletionTimeUtc,
  // ApproxSize, AuxData, EffectiveMemoryMb, HasIndex/HasExchange/HasSharePoint/
  // HasSql/HasAd/HasOracle/HasPostgreSql/HasVeeamArchiver, IsCorrupted/
  // IsRecheckCorrupted/IsConsistent/IsPartialActiveFull, ProductVersion/
  // ProductVersionFlags/ProductIsRentalLicense. Backup: PolicyName,
  // EncryptionState. Point: Type. SourceHost: HostInstanceId. Object: ViType,
  // ObjectId.
  private const string CanonicalOibSummary = """
    <OibSummary>
      <Backup JobName="Backup Job vSphere VMs"
              PolicyName="Tier1 SOBR"
              Type="0"
              EncryptionState="0" />
      <Point Num="42" Type="0" CreationTime="2026-06-08 10:00:00" CreationTimeUtc="2026-06-08 08:00:00" />
      <Storage PartialPath="vm-prod01.3D2026-06-08T100000_1234.vbk" />
      <OIB DisplayName="vm-prod01"
           VmName="vm-prod01.lab.internal"
           State="0"
           Type="1"
           Algorithm="ForeverForwardIncremental"
           HealthStatus="Good"
           CreationTimeUtc="2026-06-08 08:00:00"
           CompletionTimeUtc="2026-06-08 08:42:17"
           ApproxSize="68719476736"
           EffectiveMemoryMb="16384"
           AuxData="&lt;COibAuxData&gt;&lt;HvAuxData/&gt;&lt;/COibAuxData&gt;"
           HasIndex="true"
           HasExchange="false"
           HasSharePoint="false"
           HasSql="true"
           HasAd="false"
           HasOracle="false"
           HasPostgreSql="false"
           HasVeeamArchiver="false"
           IsCorrupted="false"
           IsRecheckCorrupted="false"
           IsConsistent="true"
           IsPartialActiveFull="false"
           ProductVersion="12.1.0.2131"
           ProductVersionFlags="0"
           ProductIsRentalLicense="false" />
      <Object Name="vm-prod01"
              Id="legacy-id-1234"
              ObjectId="canonical-id-5678"
              ViType="VMware" />
      <SourceHost Name="esxi01.lab.internal" HostInstanceId="host-uuid-abcd1234" />
      <TargetHost Name="backup-repo01.lab.internal" />
      <PrevFileName>C:\Backup\vm-prod01.3D2026-06-07T100000_1234.vbk</PrevFileName>
      <BackupVersion>12</BackupVersion>
      <OibFiles>
        <File Name="vm-prod01-flat.vmdk" Size="68719476736">
          <PlatformDetails Platform="vSphere" Adapter="lsilogic" />
        </File>
      </OibFiles>
    </OibSummary>
    """;

  [Test, Category("HappyPath")]
  public void Parser_ExtractsCanonicalBackupAttributes() {
    var oib = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(CanonicalOibSummary));
    Assert.That(oib, Is.Not.Null);
    Assert.That(oib!.JobName, Is.EqualTo("Backup Job vSphere VMs"));
    Assert.That(oib.PolicyName, Is.EqualTo("Tier1 SOBR"),
      "Backup/@PolicyName must surface — used by Synacktiv VQL as a per-restore-point column.");
    Assert.That(oib.BackupTypeCode, Is.EqualTo(0));
    Assert.That(oib.EncryptionStateCode, Is.EqualTo(0),
      "Backup/@EncryptionState is the canonical attribute name; the legacy @Encryption " +
      "fallback is for pre-V12 writers only.");
  }

  [Test, Category("HappyPath")]
  public void Parser_ExtractsCanonicalPointAttributes() {
    var oib = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(CanonicalOibSummary));
    Assert.That(oib, Is.Not.Null);
    Assert.That(oib!.RestorePointNumber, Is.EqualTo(42));
    Assert.That(oib.RestorePointTypeCode, Is.EqualTo(0),
      "Point/@Type is distinct from Backup/@Type — Synacktiv VQL surfaces both " +
      "because real writers occasionally set one but not the other.");
  }

  [Test, Category("HappyPath")]
  public void Parser_ExtractsCanonicalOibAttributes() {
    var oib = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(CanonicalOibSummary));
    Assert.That(oib, Is.Not.Null);
    Assert.That(oib!.OibVmName, Is.EqualTo("vm-prod01.lab.internal"));
    Assert.That(oib.OibState, Is.EqualTo("0"));
    Assert.That(oib.OibType, Is.EqualTo("1"));
    Assert.That(oib.OibAlgorithm, Is.EqualTo("ForeverForwardIncremental"));
    Assert.That(oib.OibHealthStatus, Is.EqualTo("Good"));
    Assert.That(oib.OibCreationTimeUtc, Is.EqualTo("2026-06-08 08:00:00"));
    Assert.That(oib.OibCompletionTimeUtc, Is.EqualTo("2026-06-08 08:42:17"));
    Assert.That(oib.OibApproxSize, Is.EqualTo(68719476736L),
      "OIB/@ApproxSize must surface as a parsed long for downstream humanization.");
    Assert.That(oib.OibEffectiveMemoryMb, Is.EqualTo(16384L));
  }

  [Test, Category("HappyPath")]
  public void Parser_ExtractsAuxDataRawBlob() {
    var oib = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(CanonicalOibSummary));
    Assert.That(oib, Is.Not.Null);
    // Synacktiv runs a nested parse_xml on AttrAuxData to extract platform-
    // specific guest details (HvAuxData / DesktopOibAuxData / COibAuxDataVmware).
    // We surface the raw blob — callers can run their own XML extraction.
    Assert.That(oib!.OibAuxDataRaw, Is.Not.Null);
    Assert.That(oib.OibAuxDataRaw, Does.Contain("COibAuxData"),
      "OIB/@AuxData must surface verbatim — it is an XML-in-attribute blob " +
      "carrying platform-specific guest details and a flat dictionary would lose structure.");
  }

  [Test, Category("HappyPath")]
  public void Parser_ExtractsCanonicalApplicationFlags() {
    var oib = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(CanonicalOibSummary));
    Assert.That(oib, Is.Not.Null);
    // All eight Has* flags from Synacktiv VQL.
    Assert.That(oib!.OibHasIndex, Is.EqualTo("true"));
    Assert.That(oib.OibHasExchange, Is.EqualTo("false"));
    Assert.That(oib.OibHasSharePoint, Is.EqualTo("false"));
    Assert.That(oib.OibHasSql, Is.EqualTo("true"));
    Assert.That(oib.OibHasAd, Is.EqualTo("false"));
    Assert.That(oib.OibHasOracle, Is.EqualTo("false"));
    Assert.That(oib.OibHasPostgreSql, Is.EqualTo("false"));
    Assert.That(oib.OibHasVeeamArchiver, Is.EqualTo("false"));
  }

  [Test, Category("HappyPath")]
  public void Parser_ExtractsCanonicalHealthFlags() {
    var oib = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(CanonicalOibSummary));
    Assert.That(oib, Is.Not.Null);
    Assert.That(oib!.OibIsCorrupted, Is.EqualTo("false"));
    Assert.That(oib.OibIsRecheckCorrupted, Is.EqualTo("false"));
    Assert.That(oib.OibIsConsistent, Is.EqualTo("true"));
    Assert.That(oib.OibIsPartialActiveFull, Is.EqualTo("false"));
  }

  [Test, Category("HappyPath")]
  public void Parser_ExtractsCanonicalProductVersionFamily() {
    var oib = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(CanonicalOibSummary));
    Assert.That(oib, Is.Not.Null);
    Assert.That(oib!.OibProductVersion, Is.EqualTo("12.1.0.2131"));
    Assert.That(oib.OibProductVersionFlags, Is.EqualTo("0"));
    Assert.That(oib.OibProductIsRentalLicense, Is.EqualTo("false"));
  }

  [Test, Category("HappyPath")]
  public void Parser_ExtractsCanonicalSourceHostHostInstanceId() {
    var oib = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(CanonicalOibSummary));
    Assert.That(oib, Is.Not.Null);
    Assert.That(oib!.SourceHostName, Is.EqualTo("esxi01.lab.internal"));
    Assert.That(oib.SourceHostInstanceId, Is.EqualTo("host-uuid-abcd1234"),
      "SourceHost/@HostInstanceId must surface — Synacktiv VQL's AttrHostInstanceId " +
      "is the globally unique source-host tag.");
  }

  [Test, Category("HappyPath")]
  public void Parser_ExtractsBothLegacyAndCanonicalObjectId() {
    var oib = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(CanonicalOibSummary));
    Assert.That(oib, Is.Not.Null);
    // Real-world OibSummary blocks vary by VBR version. The Synacktiv VQL
    // uses AttrObjectId (canonical); older writer versions emit @Id. We
    // surface both independently so the consumer can pick the one their
    // schema expects.
    Assert.That(oib!.ObjectId, Is.EqualTo("legacy-id-1234"));
    Assert.That(oib.ObjectIdNew, Is.EqualTo("canonical-id-5678"),
      "Object/@ObjectId is the canonical Synacktiv attribute and must surface " +
      "separately from the legacy @Id fallback.");
  }

  [Test, Category("HappyPath")]
  public void Parser_ExtractsObjectViType() {
    var oib = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(CanonicalOibSummary));
    Assert.That(oib, Is.Not.Null);
    Assert.That(oib!.ObjectViType, Is.EqualTo("VMware"),
      "Object/@ViType — Synacktiv VQL falls back to 'Physical machine' string " +
      "when this attribute is absent.");
  }

  [Test, Category("BoundaryCase")]
  public void Parser_TreatsLegacyEncryptionAttrAndCanonicalEncryptionStateIndependently() {
    // Pre-V12 writers emit Backup/@Encryption; V12+ writers emit Backup/@EncryptionState.
    // Both legacy and canonical attribute names must round-trip through their
    // own typed fields without collision.
    const string legacy = "<OibSummary><Backup JobName=\"legacy\" Encryption=\"2\" /></OibSummary>";
    var oib1 = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(legacy));
    Assert.That(oib1, Is.Not.Null);
    Assert.That(oib1!.EncryptionCode, Is.EqualTo(2));
    Assert.That(oib1.EncryptionStateCode, Is.Null,
      "Legacy @Encryption does NOT populate canonical @EncryptionState.");

    const string canonical = "<OibSummary><Backup JobName=\"v12\" EncryptionState=\"0\" /></OibSummary>";
    var oib2 = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(canonical));
    Assert.That(oib2, Is.Not.Null);
    Assert.That(oib2!.EncryptionStateCode, Is.EqualTo(0));
    Assert.That(oib2.EncryptionCode, Is.Null,
      "Canonical @EncryptionState does NOT populate legacy @Encryption.");
  }

  [Test, Category("BoundaryCase")]
  public void Parser_HandlesMissingCanonicalAttributesAsNull() {
    // Older writer versions emit a subset of the canonical attributes —
    // missing ones must surface as null fields, not throw.
    const string minimal = "<OibSummary><Backup JobName=\"minimal\" /></OibSummary>";
    var oib = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(minimal));
    Assert.That(oib, Is.Not.Null);
    Assert.That(oib!.PolicyName, Is.Null);
    Assert.That(oib.EncryptionStateCode, Is.Null);
    Assert.That(oib.RestorePointTypeCode, Is.Null);
    Assert.That(oib.OibVmName, Is.Null);
    Assert.That(oib.OibState, Is.Null);
    Assert.That(oib.OibAlgorithm, Is.Null);
    Assert.That(oib.OibHealthStatus, Is.Null);
    Assert.That(oib.OibCreationTimeUtc, Is.Null);
    Assert.That(oib.OibCompletionTimeUtc, Is.Null);
    Assert.That(oib.OibApproxSize, Is.Null);
    Assert.That(oib.OibEffectiveMemoryMb, Is.Null);
    Assert.That(oib.OibAuxDataRaw, Is.Null);
    Assert.That(oib.OibHasIndex, Is.Null);
    Assert.That(oib.OibHasSql, Is.Null);
    Assert.That(oib.OibIsCorrupted, Is.Null);
    Assert.That(oib.OibProductVersion, Is.Null);
    Assert.That(oib.SourceHostInstanceId, Is.Null);
    Assert.That(oib.ObjectIdNew, Is.Null);
    Assert.That(oib.ObjectViType, Is.Null);
  }

  [Test, Category("BoundaryCase")]
  public void Parser_ToleratesNonIntegerApproxSizeByLeavingFieldNull() {
    // OIB/@ApproxSize is declared long but defensive: if a writer ever
    // emits something other than a base-10 integer, we leave the typed
    // field null (the raw XML is still in RawXml for diagnostics).
    const string xml = "<OibSummary><OIB DisplayName=\"odd\" ApproxSize=\"BIG\" EffectiveMemoryMb=\"LOTS\" /></OibSummary>";
    var oib = OibSummaryParser.TryParse(Encoding.UTF8.GetBytes(xml));
    Assert.That(oib, Is.Not.Null);
    Assert.That(oib!.OibApproxSize, Is.Null);
    Assert.That(oib.OibEffectiveMemoryMb, Is.Null);
    Assert.That(oib.OibDisplayName, Is.EqualTo("odd"),
      "Non-integer numeric attrs must not abort parsing — siblings must still surface.");
  }

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

  [Test, Category("HappyPath")]
  public void MetadataIni_SurfacesCanonicalFields_WhenCanonicalOibSummaryFound() {
    var image = BuildWithTrailer(CanonicalOibSummary);
    using var ms = new MemoryStream(image);
    using var r = new VeeamReader(ms, VeeamFileType.Full);
    var ini = Encoding.UTF8.GetString(r.Entries.First(e => e.Name == "metadata.ini").Data);
    // Each new canonical field gets a metadata.ini key.
    Assert.That(ini, Does.Contain("policy_name=Tier1 SOBR"));
    Assert.That(ini, Does.Contain("encryption_state_code=0 (Unencrypted)"));
    Assert.That(ini, Does.Contain("restore_point_type_code=0 (Full)"));
    Assert.That(ini, Does.Contain("oib_vm_name=vm-prod01.lab.internal"));
    Assert.That(ini, Does.Contain("oib_algorithm=ForeverForwardIncremental"));
    Assert.That(ini, Does.Contain("oib_health_status=Good"));
    Assert.That(ini, Does.Contain("oib_creation_time_utc=2026-06-08 08:00:00"));
    Assert.That(ini, Does.Contain("oib_completion_time_utc=2026-06-08 08:42:17"));
    Assert.That(ini, Does.Contain("oib_approx_size=68719476736"));
    Assert.That(ini, Does.Contain("oib_effective_memory_mb=16384"));
    Assert.That(ini, Does.Contain("oib_aux_data_length="));
    Assert.That(ini, Does.Contain("oib_has_index=true"));
    Assert.That(ini, Does.Contain("oib_has_sql=true"));
    Assert.That(ini, Does.Contain("oib_is_consistent=true"));
    Assert.That(ini, Does.Contain("oib_product_version=12.1.0.2131"));
    Assert.That(ini, Does.Contain("source_host_instance_id=host-uuid-abcd1234"));
    Assert.That(ini, Does.Contain("object_id_new=canonical-id-5678"));
    Assert.That(ini, Does.Contain("object_vi_type=VMware"));
  }

  [Test, Category("Stub")]
  public void MetadataIni_CitesCanonicalVelociraptorArtifactSchemaSource() {
    var image = BuildWithTrailer(CanonicalOibSummary);
    using var ms = new MemoryStream(image);
    using var r = new VeeamReader(ms, VeeamFileType.Full);
    var ini = Encoding.UTF8.GetString(r.Entries.First(e => e.Name == "metadata.ini").Data);
    // Provenance audit trail: a downstream consumer reading metadata.ini
    // must see WHERE the canonical attribute set came from.
    Assert.That(ini, Does.Contain("oib_summary_schema_source=Synacktiv"),
      "metadata.ini must cite the Synacktiv Velociraptor artifact as the canonical " +
      "AttrXxx schema source so the field surface is auditable.");
    Assert.That(ini, Does.Contain("Windows.Veeam.RestorePoints.BackupFiles"),
      "metadata.ini must name the specific Velociraptor artifact whose AttrXxx " +
      "mapping we mirror.");
  }

  [Test, Category("Stub")]
  public void Description_AnnouncesCanonicalSurfaceMirror() {
    var d = new VeeamFormatDescriptor();
    var desc = d.Description;
    Assert.That(desc, Does.Contain("canonical").IgnoreCase,
      "Description must announce that the OibSummary field surface mirrors the " +
      "canonical Synacktiv Velociraptor artifact AttrXxx mapping.");
    Assert.That(desc, Does.Contain("Windows.Veeam.RestorePoints.BackupFiles"),
      "Description must name the specific Velociraptor artifact whose schema we mirror.");
    Assert.That(desc, Does.Contain("hashcat issue #3623"),
      "Description must cite the hashcat encryption-RE provenance.");
    Assert.That(desc, Does.Contain("t93873"),
      "Description must cite the Veeam R&D forum thread documenting the block-diagram-level structure.");
  }

  [Test, Category("Stub")]
  public void Description_HonestlyStatesEncryptionPasswordVerificationOnly() {
    var d = new VeeamFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    // Honest deep-RE summary: the hashcat algorithm verifies the PASSWORD
    // against a stored check blob; it does NOT give us per-block decryption.
    // The Description must say so explicitly so consumers do not believe a
    // password-known VBK is decryptable through this reader.
    Assert.That(desc, Does.Contain("verification").Or.Contain("verify"),
      "Description must clarify that the documented hashcat algorithm is a PASSWORD-VERIFICATION " +
      "blob check, not a per-block decryption path.");
  }

  [Test, Category("Stub")]
  public void Description_CitesAllResearchFollowUpDeadEnds() {
    // The deep-RE 2026 follow-up confirmed: (1) Synacktiv's pipeline uses
    // Veeam's own Extract.exe; (2) SosRansomware's tool is closed-source;
    // (3) no public binary-level reverse engineering of VeeamAgent.exe exists; (4) the Veeam
    // forum disclosure stays at block-diagram level. Description must cite
    // these so a future researcher does not redo the same searches.
    var d = new VeeamFormatDescriptor();
    var desc = d.Description;
    Assert.That(desc, Does.Contain("Extract.exe"),
      "Description must note that Synacktiv's pipeline calls Veeam's own Extract.exe — " +
      "the canonical 'use the vendor tool' fallback.");
    Assert.That(desc, Does.Contain("SosRansomware"),
      "Description must note SosRansomware's proprietary tool as a closed-source dead-end.");
    Assert.That(desc, Does.Contain("VeeamAgent.exe").Or.Contain("VeeamDataMover"),
      "Description must note that no public binary-RE of VeeamAgent.exe / VeeamDataMover exists.");
  }
}
