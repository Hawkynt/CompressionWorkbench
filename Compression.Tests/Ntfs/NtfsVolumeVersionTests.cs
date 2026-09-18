#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Ntfs;

namespace Compression.Tests.Ntfs;

/// <summary>
/// A volume's version field is a claim about its content, and these cases hold the
/// content to it.
/// </summary>
/// <remarks>
/// <para>
/// Three versions are writable: 1.2 (NT 3.51/4.0), 3.0 (Windows 2000) and 3.1
/// (Windows XP and later). They differ in more than the two bytes of
/// <c>$VOLUME_INFORMATION</c>. NTFS 3.0 introduced the centralised security store —
/// <c>$Secure</c> at MFT record 9, where 1.2 has the never-used <c>$Quota</c> — and the
/// <c>$Extend</c> directory at record 11, which 1.2 does not have at all. It also
/// appended OwnerId, SecurityId, QuotaCharged and the USN to
/// <c>$STANDARD_INFORMATION</c>, taking it from 48 bytes to 72, and reused three
/// attribute type codes: 0x40 from <c>$VOLUME_VERSION</c> to <c>$OBJECT_ID</c>, 0xC0
/// from <c>$SYMBOLIC_LINK</c> to <c>$REPARSE_POINT</c>, with 0xF0 <c>$PROPERTY_SET</c>
/// dropped for 0x100 <c>$LOGGED_UTILITY_STREAM</c>.
/// </para>
/// <para>
/// The per-version facts are the reference formatter's: mkntfs creates 16 system files
/// below NTFS 3.0 and 27 from it, names record 9 by the version, and picks between two
/// compiled-in <c>$AttrDef</c> tables on <c>major_ver &lt; 3</c>. The 3.x table asserted
/// here is byte-identical to the one a real <c>mkfs.ntfs</c> volume carries — see
/// <c>NtfsRecordHeaderVersionExternalTests</c>, which reads it back out of one.
/// </para>
/// </remarks>
[TestFixture]
public class NtfsVolumeVersionTests {

  private const int AttrDefEntrySize = 160;

  private static byte[] Build(NtfsVersion version, params (string Name, byte[] Data)[] files) {
    var w = new NtfsWriter("VERSIONED");
    w.SetNtfsVersion(version);
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build(16 * 1024 * 1024);
  }

  private static byte[] Payload(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  private static NtfsReader Read(byte[] image) => new(new MemoryStream(image, writable: false));

  // The (name, type, flags, min, max) of every entry in an $AttrDef table, stopping at
  // the zeroed terminator the table ends with.
  private static List<(string Name, uint Type, uint Flags, long Min, long Max)> AttrDefEntries(byte[] table) {
    var entries = new List<(string, uint, uint, long, long)>();
    for (var o = 0; o + AttrDefEntrySize <= table.Length; o += AttrDefEntrySize) {
      var type = BitConverter.ToUInt32(table, o + 128);
      if (type == 0) break;
      entries.Add((
        Encoding.Unicode.GetString(table, o, 128).TrimEnd('\0'),
        type,
        BitConverter.ToUInt32(table, o + 140),
        BitConverter.ToInt64(table, o + 144),
        BitConverter.ToInt64(table, o + 152)));
    }
    return entries;
  }

  // ── The metadata file set follows the version ───────────────────────────

  [Test, Category("HappyPath")]
  [TestCase(NtfsVersion.V31)]
  [TestCase(NtfsVersion.V30)]
  public void Create_Ntfs3x_CarriesSecureAndExtend(NtfsVersion version) {
    var image = Build(version, ("a.txt", "a"u8.ToArray()));

    Assert.Multiple(() => {
      Assert.That(MftInspector.FileNameOf(image, 9), Is.EqualTo("$Secure"),
        "NTFS 3.0 moved security descriptors into $Secure at record 9");
      Assert.That(MftInspector.FileNameOf(image, 11), Is.EqualTo("$Extend"),
        "the $Extend directory arrived with NTFS 3.0");
      Assert.That(MftInspector.AttributeTypes(MftInspector.ReadRecord(image, 9)).Count(t => t == 0x90),
        Is.EqualTo(2), "$Secure carries both of its indexes, $SDH and $SII");
    });
  }

  /// <summary>
  /// The case the version stamp existed to make honest: nothing NTFS 3.0 introduced may
  /// appear on a volume that says 1.2.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Create_Ntfs12_CarriesNeitherSecureNorExtend() {
    var image = Build(NtfsVersion.V12, ("a.txt", "a"u8.ToArray()));

    Assert.Multiple(() => {
      Assert.That(MftInspector.FileNameOf(image, 9), Is.EqualTo("$Quota"),
        "record 9 is $Quota before NTFS 3.0, not $Secure");
      Assert.That(MftInspector.AttributeTypes(MftInspector.ReadRecord(image, 9)), Does.Not.Contain(0x90u),
        "$Quota has neither the $SDH nor the $SII index $Secure needs");
      Assert.That(MftInspector.RecordIsInUse(image, 11), Is.False,
        "record 11 holds nothing before NTFS 3.0 — $Extend is a 3.0 file");

      using var reader = Read(image);
      Assert.That(reader.Entries.Select(e => e.Name), Does.Not.Contain("$Extend"));
    });
  }

  [Test, Category("HappyPath")]
  [TestCase(NtfsVersion.V31)]
  [TestCase(NtfsVersion.V30)]
  [TestCase(NtfsVersion.V12)]
  public void Create_EveryVersion_StampsItsOwnMajorAndMinor(NtfsVersion version) {
    var image = Build(version, ("a.txt", "a"u8.ToArray()));

    Assert.That(MftInspector.VolumeVersion(MftInspector.ReadRecord(image, 3)),
      Is.EqualTo((version.Major(), version.Minor())));
  }

  // ── $STANDARD_INFORMATION follows the version ───────────────────────────

  /// <summary>
  /// Every record, system and user alike, has to carry the shape its own volume's
  /// <c>$AttrDef</c> permits: 48 bytes and no more on 1.2, the 72-byte 3.0 shape on a
  /// 3.x volume. The writer used to emit the 48-byte shape unconditionally, so a volume
  /// declaring 3.1 carried a 1.2-era attribute throughout.
  /// </summary>
  [Test, Category("HappyPath")]
  [TestCase(NtfsVersion.V31, 72)]
  [TestCase(NtfsVersion.V30, 72)]
  [TestCase(NtfsVersion.V12, 48)]
  public void Create_EveryVersion_SizesStandardInformationForIt(NtfsVersion version, int expectedLength) {
    var image = Build(version, ("resident.txt", "small"u8.ToArray()), ("large.bin", Payload(9000, 3)));
    var last = MftInspector.LastInUseRecord(image);

    Assert.Multiple(() => {
      for (uint record = 0; record <= last; ++record) {
        if (!MftInspector.RecordIsInUse(image, record)) continue;
        Assert.That(MftInspector.StandardInformationLength(MftInspector.ReadRecord(image, record)),
          Is.EqualTo(expectedLength), $"MFT record {record} carries the wrong $STANDARD_INFORMATION shape");
      }
    });
  }

  /// <summary>
  /// The 24 bytes NTFS 3.0 appended to <c>$STANDARD_INFORMATION</c> come out of the same
  /// MFT record that a resident <c>$DATA</c> and a <c>$FILE_NAME</c> share, and the name
  /// grows that record by two bytes a character. A file near the resident threshold with
  /// a long name therefore has to go non-resident on a 3.x volume rather than overrun the
  /// record — which is what a flat byte threshold alone would have it do.
  /// </summary>
  [Test, Category("Boundary")]
  [TestCase(NtfsVersion.V31)]
  [TestCase(NtfsVersion.V30)]
  [TestCase(NtfsVersion.V12)]
  public void Create_LongNamesAtTheResidentThreshold_StayInsideTheirRecords(NtfsVersion version) {
    var files = Enumerable.Range(1, 48)
      .Select(n => (Name: new string('n', n) + ".txt", Data: Payload(700, n)))
      .ToArray();
    var image = Build(version, files);
    var last = MftInspector.LastInUseRecord(image);

    Assert.Multiple(() => {
      for (uint record = 0; record <= last; ++record) {
        if (!MftInspector.RecordIsInUse(image, record)) continue;
        var raw = MftInspector.ReadRecord(image, record);
        Assert.That(MftInspector.UsedSize(raw), Is.LessThanOrEqualTo((uint)raw.Length),
          $"MFT record {record} claims to use more bytes than it has");
      }

      using var reader = Read(image);
      foreach (var (name, data) in files)
        Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == name)), Is.EqualTo(data), name);
    });
  }

  [Test, Category("Boundary")]
  [TestCase(NtfsVersion.V31)]
  [TestCase(NtfsVersion.V30)]
  [TestCase(NtfsVersion.V12)]
  public void InPlaceAdd_LongNameAtTheResidentThreshold_StaysInsideItsRecord(NtfsVersion version) {
    var image = Build(version, ("seed.txt", "seed"u8.ToArray()));
    var name = new string('n', 48) + ".txt";
    var data = Payload(700, 3);

    NtfsInPlaceAdder.AddFile(image, name, data);

    var record = MftInspector.FindRecordByFileName(image, name);
    Assert.Multiple(() => {
      Assert.That(MftInspector.UsedSize(record), Is.LessThanOrEqualTo((uint)record.Length));
      using var reader = Read(image);
      Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == name)), Is.EqualTo(data));
    });
  }

  // ── $AttrDef follows the version ────────────────────────────────────────

  [Test, Category("HappyPath")]
  [TestCase(NtfsVersion.V31)]
  [TestCase(NtfsVersion.V30)]
  [TestCase(NtfsVersion.V12)]
  public void Create_EveryVersion_WritesItsOwnAttrDefTable(NtfsVersion version) {
    var image = Build(version, ("a.txt", "a"u8.ToArray()));
    var onDisk = MftInspector.ReadDefaultDataStream(image, 4);

    Assert.That(onDisk, Is.EqualTo(NtfsWriter.BuildAttrDefTable(version)),
      "$AttrDef's content must be the table the declared version defines");
  }

  [Test, Category("HappyPath")]
  public void AttrDef_Ntfs3x_DefinesTheTypesNtfs30Introduced() {
    var entries = AttrDefEntries(NtfsWriter.BuildAttrDefTable(NtfsVersion.V31));
    var byType = entries.ToDictionary(e => e.Type);

    Assert.Multiple(() => {
      Assert.That(byType[0x40].Name, Is.EqualTo("$OBJECT_ID"), "0x40 is $OBJECT_ID from NTFS 3.0");
      Assert.That(byType[0xC0].Name, Is.EqualTo("$REPARSE_POINT"), "0xC0 is $REPARSE_POINT from NTFS 3.0");
      Assert.That(byType.ContainsKey(0x100), Is.True, "$LOGGED_UTILITY_STREAM arrived with NTFS 3.0");
      Assert.That(byType.ContainsKey(0xF0), Is.False, "$PROPERTY_SET is not in the 3.x table");
      Assert.That((byType[0x10].Min, byType[0x10].Max), Is.EqualTo((48L, 72L)),
        "3.x permits both $STANDARD_INFORMATION shapes");
    });
  }

  [Test, Category("HappyPath")]
  public void AttrDef_Ntfs12_DefinesTheTypesNtfs30Replaced() {
    var entries = AttrDefEntries(NtfsWriter.BuildAttrDefTable(NtfsVersion.V12));
    var byType = entries.ToDictionary(e => e.Type);

    Assert.Multiple(() => {
      Assert.That(byType[0x40].Name, Is.EqualTo("$VOLUME_VERSION"), "0x40 is $VOLUME_VERSION before NTFS 3.0");
      Assert.That(byType[0xC0].Name, Is.EqualTo("$SYMBOLIC_LINK"), "0xC0 is $SYMBOLIC_LINK before NTFS 3.0");
      Assert.That(byType.ContainsKey(0x100), Is.False, "$LOGGED_UTILITY_STREAM does not exist in NTFS 1.2");
      Assert.That((byType[0x10].Min, byType[0x10].Max), Is.EqualTo((48L, 48L)),
        "NTFS 1.2 permits exactly one $STANDARD_INFORMATION shape");
      Assert.That(entries, Has.Count.EqualTo(14));
    });
  }

  // ── Round trip and in-place edits keep the version ──────────────────────

  [Test, Category("RoundTrip")]
  [TestCase(NtfsVersion.V31)]
  [TestCase(NtfsVersion.V30)]
  [TestCase(NtfsVersion.V12)]
  public void Create_EveryVersion_ReadsBackWithItsVersionAndNoInconsistency(NtfsVersion version) {
    var payload = Payload(20_000, 5);
    var image = Build(version, ("small.txt", "small"u8.ToArray()), ("large.bin", payload));

    using var reader = Read(image);
    Assert.Multiple(() => {
      Assert.That(reader.DeclaredVersion, Is.EqualTo(version));
      Assert.That(reader.VersionInconsistencies, Is.Empty);
      Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "large.bin")), Is.EqualTo(payload));
    });
  }

  /// <summary>
  /// In-place editing must leave the volume the version it already was — down to the
  /// <c>$STANDARD_INFORMATION</c> shape of the records it grows, which is where a 1.2
  /// volume would otherwise acquire a 3.x structure one added file at a time.
  /// </summary>
  [Test, Category("RoundTrip")]
  [TestCase(NtfsVersion.V31, 72)]
  [TestCase(NtfsVersion.V30, 72)]
  [TestCase(NtfsVersion.V12, 48)]
  public void InPlaceAdd_KeepsTheStandardInformationShapeOfTheVolume(NtfsVersion version, int expectedLength) {
    var image = Build(version, ("seed.txt", "seed"u8.ToArray()));
    var added = "added in place"u8.ToArray();

    NtfsInPlaceAdder.AddFile(image, "added.txt", added);
    NtfsInPlaceAdder.AddFile(image, "sub/deep.bin", Payload(9000, 7));

    Assert.Multiple(() => {
      foreach (var name in new[] { "added.txt", "sub" })
        Assert.That(MftInspector.StandardInformationLength(MftInspector.FindRecordByFileName(image, name)),
          Is.EqualTo(expectedLength), $"the record added for '{name}' left the volume's version behind");

      using var reader = Read(image);
      Assert.That(reader.DeclaredVersion, Is.EqualTo(version));
      Assert.That(reader.VersionInconsistencies, Is.Empty);
      Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "added.txt")), Is.EqualTo(added));
    });
  }

  [Test, Category("RoundTrip")]
  [TestCase(NtfsVersion.V31)]
  [TestCase(NtfsVersion.V30)]
  [TestCase(NtfsVersion.V12)]
  public void RemoveAndShrink_KeepTheVersionOfTheImageTheyEdit(NtfsVersion version) {
    var keep = Payload(200_000, 11);
    var w = new NtfsWriter("VERSIONED");
    w.SetNtfsVersion(version);
    w.AddFile("keep.bin", keep);
    w.AddFile("drop.bin", Payload(9000, 13));
    var image = w.Build(24 * 1024 * 1024);

    NtfsRemover.Remove(image, "drop.bin");
    var shrink = NtfsInPlaceShrinker.ShrinkToFit(image);
    Assert.That(shrink.WasReduced, Is.True);

    using var reader = new NtfsReader(new MemoryStream(image, 0, (int)shrink.NewSize, writable: false));
    Assert.Multiple(() => {
      Assert.That(reader.DeclaredVersion, Is.EqualTo(version));
      Assert.That(reader.VersionInconsistencies, Is.Empty);
      Assert.That(reader.Entries.Select(e => e.Name), Does.Not.Contain("drop.bin"));
      Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "keep.bin")), Is.EqualTo(keep));
    });
  }

  [Test, Category("RoundTrip")]
  [TestCase(NtfsVersion.V31)]
  [TestCase(NtfsVersion.V30)]
  [TestCase(NtfsVersion.V12)]
  public void Defragment_KeepsTheVersionOfTheImageItEdits(NtfsVersion version) {
    var payload = Payload(60_000, 17);
    var image = Build(version, ("frag.bin", payload), ("other.txt", "other"u8.ToArray()));

    using var stream = new MemoryStream();
    stream.Write(image);
    new NtfsFormatDescriptor().Defragment(stream);

    stream.Position = 0;
    using var reader = new NtfsReader(stream, leaveOpen: true);
    Assert.Multiple(() => {
      Assert.That(reader.DeclaredVersion, Is.EqualTo(version));
      Assert.That(reader.VersionInconsistencies, Is.Empty);
      Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "frag.bin")), Is.EqualTo(payload));
    });
  }

  // ── The option surface ──────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Option_OffersAllThreeVersionsAndDefaultsTo31() {
    var option = new NtfsFormatDescriptor().OptionsSchema.Single(o => o.Key == "NtfsVersion");

    Assert.Multiple(() => {
      Assert.That(option.AllowedValues, Is.EquivalentTo(new[] { "3.1", "3.0", "1.2" }));
      Assert.That(option.Default, Is.EqualTo("3.1"));
      Assert.That(option.Description, Does.Contain("$Quota").And.Contain("$Extend").And.Contain("$STANDARD_INFORMATION"),
        "the description has to say what the version selects besides the stamp");
    });
  }

  [Test, Category("HappyPath")]
  public void Create_NoVersionOption_ProducesA31Volume() {
    var output = new MemoryStream();
    new NtfsFormatDescriptor().Create(output, [], new FormatCreateOptions());

    using var reader = Read(output.ToArray());
    Assert.That(reader.DeclaredVersion, Is.EqualTo(NtfsVersion.V31));
  }

  // ── Exceptional: a stamp that contradicts the content ───────────────────

  /// <summary>
  /// Precisely the bug being fixed: a volume whose <c>$VOLUME_INFORMATION</c> says 1.2
  /// while its metadata set is 3.x. Restamping a 3.1 image is the shortest way to build
  /// one, and it must be reported rather than read as though it were a 1.2 volume.
  /// </summary>
  [Test, Category("Exceptional")]
  public void Read_Ntfs12StampOver3xMetadata_IsDiagnosed() {
    var image = Build(NtfsVersion.V31, ("a.txt", "a"u8.ToArray()));
    RestampVolumeVersion(image, major: 1, minor: 2);

    using var reader = Read(image);
    Assert.Multiple(() => {
      Assert.That(reader.DeclaredVersion, Is.EqualTo(NtfsVersion.V12));
      Assert.That(reader.VersionInconsistencies, Has.Count.EqualTo(3));
      Assert.That(reader.VersionInconsistencies, Has.Some.Contains("$Secure"));
      Assert.That(reader.VersionInconsistencies, Has.Some.Contains("$Extend"));
      Assert.That(reader.VersionInconsistencies, Has.Some.Contains("$STANDARD_INFORMATION"));
      Assert.That(reader.Entries.Select(e => e.Name), Does.Contain("a.txt"),
        "a diagnosed volume is still read — the reader reports, it does not refuse");
    });
  }

  /// <summary>
  /// The mirror: a 1.2 volume restamped 3.1 is missing the two metadata files 3.x
  /// requires, which is the same defect seen from the other side.
  /// </summary>
  [Test, Category("Exceptional")]
  public void Read_Ntfs31StampOver12Metadata_IsDiagnosed() {
    var image = Build(NtfsVersion.V12, ("a.txt", "a"u8.ToArray()));
    RestampVolumeVersion(image, major: 3, minor: 1);

    using var reader = Read(image);
    Assert.Multiple(() => {
      Assert.That(reader.DeclaredVersion, Is.EqualTo(NtfsVersion.V31));
      Assert.That(reader.VersionInconsistencies, Has.Count.EqualTo(2));
      Assert.That(reader.VersionInconsistencies, Has.Some.Contains("$Quota"));
      Assert.That(reader.VersionInconsistencies, Has.Some.Contains("$Extend"));
    });
  }

  [Test, Category("Exceptional")]
  [TestCase((byte)1, (byte)0, TestName = "NTFS 1.0, never written here")]
  [TestCase((byte)2, (byte)0, TestName = "a major version that never shipped")]
  [TestCase((byte)3, (byte)2, TestName = "a minor version past 3.1")]
  public void Read_UnknownVersionStamp_IsDiagnosedRatherThanAccepted(byte major, byte minor) {
    var image = Build(NtfsVersion.V31, ("a.txt", "a"u8.ToArray()));
    RestampVolumeVersion(image, major, minor);

    using var reader = Read(image);
    Assert.Multiple(() => {
      Assert.That(reader.DeclaredVersion, Is.Null);
      Assert.That(reader.VersionInconsistencies, Has.Some.Contains($"NTFS {major}.{minor}"));
    });
  }

  [Test, Category("Exceptional")]
  public void SetNtfsVersion_UndefinedValue_IsRefused()
    => Assert.That(() => new NtfsWriter("V").SetNtfsVersion((NtfsVersion)0x0201),
      Throws.InstanceOf<ArgumentOutOfRangeException>());

  [Test, Category("Exceptional")]
  [TestCase(null)]
  [TestCase("")]
  [TestCase("3")]
  [TestCase("3.1.1")]
  [TestCase("two.point.oh")]
  [TestCase("4.0")]
  public void TryParse_UnusableText_Fails(string? text)
    => Assert.That(NtfsVersions.TryParse(text, out _), Is.False);

  [Test, Category("HappyPath")]
  [TestCase("1.2", NtfsVersion.V12)]
  [TestCase("3.0", NtfsVersion.V30)]
  [TestCase(" 3.1 ", NtfsVersion.V31)]
  public void TryParse_TheOptionSpellings_RoundTrip(string text, NtfsVersion expected) {
    Assert.That(NtfsVersions.TryParse(text, out var version), Is.True);
    Assert.That(version, Is.EqualTo(expected));
  }

  // Rewrites $Volume's $VOLUME_INFORMATION major/minor in place, fixup and all, so an
  // image can be made to declare a version its content does not match.
  private static void RestampVolumeVersion(byte[] image, byte major, byte minor) {
    var offset = MftInspector.VolumeVersionByteOffset(image);
    image[offset] = major;
    image[offset + 1] = minor;
  }
}
