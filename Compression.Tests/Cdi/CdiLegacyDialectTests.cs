using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Cdi;

namespace Compression.Tests.Cdi;

[TestFixture]
public sealed class CdiLegacyDialectTests {
  private const uint V2 = 0x80000004;
  private const uint V3 = 0x80000005;
  private const uint V35 = 0x80000006;
  private const int Pregap = 150;

  private static ReadOnlySpan<byte> TrackMarker => [
    0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
  ];

  private sealed record LegacyTrack(
    CdiTrackMode Mode,
    uint SectorSizeSelector,
    int PregapSectors,
    int DataSectors,
    int StartLba,
    byte[] Body
  );

  [TestCase(V2)]
  [TestCase(V3)]
  [Category("HappyPath"), Category("RoundTrip")]
  public void Read_OldDescriptorDialect_ParsesAudioAndCookedMode1(uint version) {
    var image = BuildLegacyMultiTrack(version, "legacy-reader"u8.ToArray());
    using var stream = new MemoryStream(image);
    using var reader = new CdiReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.CdiVersion, Is.EqualTo(version));
      Assert.That(reader.SessionCount, Is.EqualTo(1));
      Assert.That(reader.Tracks, Has.Count.EqualTo(2));
      Assert.That(reader.Tracks[0].Mode, Is.EqualTo(CdiTrackMode.Audio));
      Assert.That(reader.Tracks[0].ReadMode, Is.EqualTo(CdiReadMode.Raw2352));
      Assert.That(reader.Tracks[1].Mode, Is.EqualTo(CdiTrackMode.Mode1));
      Assert.That(reader.Tracks[1].ReadMode, Is.EqualTo(CdiReadMode.Mode1_2048));
      Assert.That(reader.ActiveDataTrack, Is.EqualTo(reader.Tracks[1]));
    });

    var file = reader.Entries.Single(entry =>
      !entry.IsDirectory && entry.Name.Equals("README.TXT", StringComparison.OrdinalIgnoreCase));
    Assert.That(reader.Extract(file), Is.EqualTo("legacy-reader"u8.ToArray()));
  }

  [TestCase(V2)]
  [TestCase(V3)]
  [Category("HappyPath"), Category("RoundTrip")]
  public void Add_OldMultiTrackDescriptor_RebuildsIsoButPreservesOpticalBytes(uint version) {
    using var archive = new MemoryStream();
    archive.Write(BuildLegacyMultiTrack(version, "before"u8.ToArray()));
    archive.Position = 0;

    long dataStart;
    long dataEnd;
    using (var reader = new CdiReader(archive, leaveOpen: true)) {
      var active = reader.ActiveDataTrack!;
      dataStart = active.DataOffset;
      dataEnd = active.DataOffset + (long)active.DataSectorCount * 2048;
    }

    var before = archive.ToArray();
    ((IArchiveModifiable)new CdiFormatDescriptor()).Add(archive, [
      ArchiveInputInfo.InMemory("README.TXT", "replacement"u8),
      ArchiveInputInfo.InMemory("NEWFILE.BIN", "new-file"u8),
    ]);
    var after = archive.ToArray();

    Assert.Multiple(() => {
      Assert.That(after.Length, Is.EqualTo(before.Length), "fixed optical layout changed size");
      Assert.That(after.AsSpan(0, checked((int)dataStart)).ToArray(),
        Is.EqualTo(before.AsSpan(0, checked((int)dataStart)).ToArray()),
        "pregaps/audio/earlier tracks changed");
      Assert.That(after.AsSpan(checked((int)dataEnd)).ToArray(),
        Is.EqualTo(before.AsSpan(checked((int)dataEnd)).ToArray()),
        "bytes after active ISO track, including old descriptor, changed");
      Assert.That(ReadFile(archive, "README.TXT"), Is.EqualTo("replacement"u8.ToArray()));
      Assert.That(ReadFile(archive, "NEWFILE.BIN"), Is.EqualTo("new-file"u8.ToArray()));
      Assert.That(ReadFooterVersion(after), Is.EqualTo(version));
    });
  }

  [TestCase(V2)]
  [TestCase(V3)]
  [Category("HappyPath"), Category("RoundTrip")]
  public void RemovePurgeAndDefrag_OldMultiTrackDescriptor_KeepDescriptorDialect(uint version) {
    using var archive = new MemoryStream();
    archive.Write(BuildLegacyMultiTrack(version, "remove-me"u8.ToArray()));
    archive.Position = 0;
    var descriptor = new CdiFormatDescriptor();

    ((IArchiveModifiable)descriptor).Add(archive, [ArchiveInputInfo.InMemory("KEEP.BIN", "keep"u8)]);
    ((IArchiveModifiable)descriptor).Remove(archive, ["README.TXT"]);
    Assert.Multiple(() => {
      Assert.That(ReadFile(archive, "README.TXT"), Is.Null);
      Assert.That(ReadFile(archive, "KEEP.BIN"), Is.EqualTo("keep"u8.ToArray()));
      Assert.That(ReadFooterVersion(archive.ToArray()), Is.EqualTo(version));
    });

    ((IArchiveDefragmentable)descriptor).Defragment(archive);
    Assert.Multiple(() => {
      Assert.That(ReadFile(archive, "KEEP.BIN"), Is.EqualTo("keep"u8.ToArray()));
      Assert.That(ReadFooterVersion(archive.ToArray()), Is.EqualTo(version));
    });

    ((IArchivePurgeable)descriptor).Purge(archive);
    archive.Position = 0;
    using var reader = new CdiReader(archive, leaveOpen: true);
    Assert.Multiple(() => {
      Assert.That(reader.Entries.Where(static entry => !entry.IsDirectory), Is.Empty);
      Assert.That(reader.Tracks, Has.Count.EqualTo(2));
      Assert.That(reader.Tracks[0].Mode, Is.EqualTo(CdiTrackMode.Audio));
      Assert.That(reader.CdiVersion, Is.EqualTo(version));
    });
  }

  [TestCase("2.0", V2)]
  [TestCase("3.0", V3)]
  [TestCase("3.5", V35)]
  [Category("HappyPath"), Category("RoundTrip")]
  public void Create_TargetCompatibility_WritesRequestedTrailerDialect(string target, uint expectedVersion) {
    var descriptor = new CdiFormatDescriptor();
    var options = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        [FormatOptionKeys.TargetCompatibility] = target,
      },
    };
    using var image = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(
      image,
      [ArchiveInputInfo.InMemory("VERSION.TXT", targetu8(target))],
      options);

    var bytes = image.ToArray();
    var version = ReadFooterVersion(bytes);
    var locator = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4));
    var descriptorStart = expectedVersion == V35
      ? bytes.Length - locator
      : locator;

    Assert.Multiple(() => {
      Assert.That(version, Is.EqualTo(expectedVersion));
      Assert.That(descriptorStart, Is.GreaterThan(0));
      Assert.That(descriptorStart, Is.LessThan(bytes.Length - 8));
      Assert.That(ReadFile(image, "VERSION.TXT"), Is.EqualTo(targetu8(target)));
    });
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ExposesVersionAsCompatibilityConstraint() {
    var descriptor = (IFormatOptionsSchema)new CdiFormatDescriptor();
    var option = descriptor.OptionsSchema.Single(item => item.Key == FormatOptionKeys.TargetCompatibility);
    Assert.Multiple(() => {
      Assert.That(option.Kind, Is.EqualTo(FormatOptionKind.Enum));
      Assert.That(option.Default, Is.EqualTo("3.5"));
      Assert.That(option.AllowedValues, Is.EqualTo(new[] { "3.5", "3.0", "2.0" }));
      Assert.That(option.IsOptimizationAxis, Is.False);
    });
  }

  [Test, Category("ErrorPath")]
  public void OldMode2MultiTrackMutation_FailsBeforeChangingImage() {
    var image = BuildLegacyMode2(V3);
    using var archive = new MemoryStream();
    archive.Write(image);
    archive.Position = 0;
    var before = archive.ToArray();

    Assert.Throws<NotSupportedException>(() =>
      ((IArchiveModifiable)new CdiFormatDescriptor()).Add(
        archive,
        [ArchiveInputInfo.InMemory("NEW.BIN", "new"u8)]));
    Assert.That(archive.ToArray(), Is.EqualTo(before));
  }

  private static byte[] BuildLegacyMultiTrack(uint version, byte[] payload) {
    var isoWriter = new FileSystem.Iso.IsoWriter();
    isoWriter.AddFile("README.TXT", payload);
    var iso = isoWriter.Build();

    var audio = AudioTrack(dataSectors: 4, fill: 0x5A, startLba: 0);
    var data = CookedMode1Track(iso, startLba: 304);
    return BuildLegacy(version, [[audio, data]]);
  }

  private static byte[] BuildLegacyMode2(uint version) {
    const int dataSectors = 32;
    var body = new byte[(Pregap + dataSectors) * 2336];
    var pvdAt = (Pregap + 16) * 2336 + 8;
    body[pvdAt] = 1;
    "CD001"u8.CopyTo(body.AsSpan(pvdAt + 1));
    body[pvdAt + 6] = 1;
    var mode2 = new LegacyTrack(CdiTrackMode.Mode2, 1, Pregap, dataSectors, 11700, body);
    return BuildLegacy(version, [[AudioTrack(3, 0x33, 0)], [mode2]]);
  }

  private static LegacyTrack AudioTrack(int dataSectors, byte fill, int startLba) {
    var body = new byte[(Pregap + dataSectors) * 2352];
    body.AsSpan(Pregap * 2352).Fill(fill);
    return new(CdiTrackMode.Audio, 2, Pregap, dataSectors, startLba, body);
  }

  private static LegacyTrack CookedMode1Track(byte[] iso, int startLba) {
    var dataSectors = checked((iso.Length + 2047) / 2048);
    var body = new byte[(Pregap + dataSectors) * 2048];
    iso.CopyTo(body.AsSpan(Pregap * 2048));
    return new(CdiTrackMode.Mode1, 0, Pregap, dataSectors, startLba, body);
  }

  private static byte[] BuildLegacy(uint version, IReadOnlyList<IReadOnlyList<LegacyTrack>> sessions) {
    if (version is not (V2 or V3))
      throw new ArgumentOutOfRangeException(nameof(version));

    using var image = new MemoryStream();
    foreach (var session in sessions)
      foreach (var track in session)
        image.Write(track.Body);

    var descriptorOffset = checked((uint)image.Position);
    WriteUInt16(image, checked((ushort)sessions.Count));
    foreach (var session in sessions) {
      WriteUInt16(image, checked((ushort)session.Count));
      foreach (var track in session)
        WriteLegacyTrack(image, version, track);
      WriteZeros(image, version == V2 ? 12 : 13);
    }

    WriteUInt32(image, version);
    WriteUInt32(image, descriptorOffset);
    return image.ToArray();
  }

  private static void WriteLegacyTrack(Stream output, uint version, LegacyTrack track) {
    WriteUInt32(output, 0);
    output.Write(TrackMarker);
    output.Write(TrackMarker);
    WriteZeros(output, 4);
    output.WriteByte(0);
    WriteZeros(output, 11 + 4 + 4);
    WriteUInt32(output, 0);
    WriteZeros(output, 2);
    WriteUInt32(output, checked((uint)track.PregapSectors));
    WriteUInt32(output, checked((uint)track.DataSectors));
    WriteZeros(output, 6);
    WriteUInt32(output, (uint)track.Mode);
    WriteZeros(output, 12);
    WriteUInt32(output, checked((uint)track.StartLba));
    WriteUInt32(output, checked((uint)(track.PregapSectors + track.DataSectors)));
    WriteZeros(output, 16);
    WriteUInt32(output, track.SectorSizeSelector);
    WriteZeros(output, 29);
    if (version == V3) {
      WriteZeros(output, 5);
      WriteUInt32(output, 0);
    }
  }

  private static byte[]? ReadFile(Stream image, string name) {
    image.Position = 0;
    using var reader = new CdiReader(image, leaveOpen: true);
    var entry = reader.Entries.FirstOrDefault(candidate =>
      !candidate.IsDirectory && candidate.FullPath.Equals(name, StringComparison.OrdinalIgnoreCase));
    return entry == null ? null : reader.Extract(entry);
  }

  private static uint ReadFooterVersion(byte[] image)
    => BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(image.Length - 8));

  private static byte[] targetu8(string text) => System.Text.Encoding.ASCII.GetBytes(text);

  private static void WriteUInt16(Stream output, ushort value) {
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
    output.Write(bytes);
  }

  private static void WriteUInt32(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    output.Write(bytes);
  }

  private static void WriteZeros(Stream output, int count) {
    Span<byte> zeros = stackalloc byte[64];
    zeros.Clear();
    while (count > 0) {
      var chunk = Math.Min(count, zeros.Length);
      output.Write(zeros[..chunk]);
      count -= chunk;
    }
  }
}
