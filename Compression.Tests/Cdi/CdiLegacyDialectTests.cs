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

    var (dataStart, dataEnd) = ActiveDataBounds(archive);
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

  [TestCase(CdiTrackMode.Mode1)]
  [TestCase(CdiTrackMode.Mode2)]
  [Category("HappyPath"), Category("RoundTrip"), Category("KnownAnswer")]
  public void Add_OldRawDataTrack_RegeneratesIntegrityWithoutChangingOpticalLayout(CdiTrackMode mode) {
    var image = mode == CdiTrackMode.Mode1
      ? BuildLegacyRawMode1(V3, "raw-before"u8.ToArray())
      : BuildLegacyRawMode2Form1(V3, "raw-before"u8.ToArray());
    using var archive = new MemoryStream(image);
    var (dataStart, dataEnd) = ActiveDataBounds(archive);
    var before = archive.ToArray();

    archive.Position = 37;
    ((IArchiveModifiable)new CdiFormatDescriptor()).Add(archive, [
      ArchiveInputInfo.InMemory("README.TXT", "raw-after"u8),
      ArchiveInputInfo.InMemory("RAWNEW.BIN", "new"u8),
    ]);
    var callerPosition = archive.Position;
    var after = archive.ToArray();

    Assert.Multiple(() => {
      Assert.That(callerPosition, Is.EqualTo(37), "successful mutation did not restore caller stream position");
      Assert.That(after.Length, Is.EqualTo(before.Length));
      Assert.That(after.AsSpan(0, checked((int)dataStart)).ToArray(),
        Is.EqualTo(before.AsSpan(0, checked((int)dataStart)).ToArray()));
      Assert.That(after.AsSpan(checked((int)dataEnd)).ToArray(),
        Is.EqualTo(before.AsSpan(checked((int)dataEnd)).ToArray()));
      Assert.That(ReadFile(archive, "README.TXT"), Is.EqualTo("raw-after"u8.ToArray()));
      Assert.That(ReadFile(archive, "RAWNEW.BIN"), Is.EqualTo("new"u8.ToArray()));
      Assert.That(ReadFooterVersion(after), Is.EqualTo(V3));
    });
  }

  [Test, Category("ErrorPath")]
  public void FailedEmbeddedRebuild_RestoresCallerStreamPosition() {
    using var archive = new MemoryStream(BuildLegacyMultiTrack(V3, "small"u8.ToArray()));
    archive.Position = 41;
    var before = archive.ToArray();
    var huge = new byte[2 * 1024 * 1024];

    Assert.Throws<IOException>(() =>
      ((IArchiveModifiable)new CdiFormatDescriptor()).Add(
        archive,
        [ArchiveInputInfo.InMemory("TOO-BIG.BIN", huge)]));

    Assert.Multiple(() => {
      Assert.That(archive.Position, Is.EqualTo(41), "failed mutation did not restore caller stream position");
      Assert.That(archive.ToArray(), Is.EqualTo(before), "failed staged rebuild changed the source image");
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
      [ArchiveInputInfo.InMemory("VERSION.TXT", TargetUtf8(target))],
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
      Assert.That(ReadFile(image, "VERSION.TXT"), Is.EqualTo(TargetUtf8(target)));
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
  public void OldMode2Form2MultiTrackMutation_FailsBeforeChangingImage() {
    var image = BuildLegacyMode2Form2(V3);
    using var archive = new MemoryStream(image);
    archive.Position = 29;
    var before = archive.ToArray();

    Assert.Throws<NotSupportedException>(() =>
      ((IArchiveModifiable)new CdiFormatDescriptor()).Add(
        archive,
        [ArchiveInputInfo.InMemory("NEW.BIN", "new"u8)]));
    Assert.Multiple(() => {
      Assert.That(archive.ToArray(), Is.EqualTo(before));
      Assert.That(archive.Position, Is.EqualTo(29));
    });
  }

  private static byte[] BuildLegacyMultiTrack(uint version, byte[] payload) {
    var isoWriter = new FileSystem.Iso.IsoWriter();
    isoWriter.AddFile("README.TXT", payload);
    var iso = isoWriter.Build();

    var audio = AudioTrack(dataSectors: 4, fill: 0x5A, startLba: 0);
    var data = CookedMode1Track(iso, startLba: 304);
    return BuildLegacy(version, [[audio, data]]);
  }

  private static byte[] BuildLegacyRawMode1(uint version, byte[] payload) {
    var isoWriter = new FileSystem.Iso.IsoWriter();
    isoWriter.AddFile("README.TXT", payload);
    return BuildLegacy(version, [[AudioTrack(3, 0x31, 0), RawDataTrack(isoWriter.Build(), CdiTrackMode.Mode1, 303)]]);
  }

  private static byte[] BuildLegacyRawMode2Form1(uint version, byte[] payload) {
    var isoWriter = new FileSystem.Iso.IsoWriter();
    isoWriter.AddFile("README.TXT", payload);
    return BuildLegacy(version, [[AudioTrack(3, 0x32, 0), RawDataTrack(isoWriter.Build(), CdiTrackMode.Mode2, 303)]]);
  }

  private static byte[] BuildLegacyMode2Form2(uint version) {
    const int dataSectors = 32;
    var body = new byte[(Pregap + dataSectors) * 2336];
    var firstData = Pregap * 2336;
    body[firstData + 2] = 0x20;
    body[firstData + 6] = 0x20;
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

  private static LegacyTrack RawDataTrack(byte[] iso, CdiTrackMode mode, int startLba) {
    const int stride = 2352;
    var dataSectors = checked((iso.Length + 2047) / 2048);
    var body = new byte[(Pregap + dataSectors) * stride];
    for (var sector = 0; sector < dataSectors; ++sector) {
      var raw = body.AsSpan((Pregap + sector) * stride, stride);
      WriteSync(raw);
      raw[12] = 0;
      raw[13] = unchecked((byte)((sector / 75) % 60));
      raw[14] = unchecked((byte)(sector % 75));
      raw[15] = (byte)mode;
      if (mode == CdiTrackMode.Mode1) {
        iso.AsSpan(sector * 2048, 2048).CopyTo(raw[16..]);
        CdiCdSectorIntegrity.RegenerateMode1(raw);
      } else {
        byte[] subheader = [0, 0, 0x08, 0, 0, 0, 0x08, 0];
        subheader.CopyTo(raw[16..]);
        iso.AsSpan(sector * 2048, 2048).CopyTo(raw[24..]);
        CdiCdSectorIntegrity.RegenerateMode2Form1(raw);
      }
    }
    return new(mode, 2, Pregap, dataSectors, startLba, body);
  }

  private static void WriteSync(Span<byte> sector) {
    sector[0] = 0;
    sector.Slice(1, 10).Fill(0xFF);
    sector[11] = 0;
  }

  private static (long Start, long End) ActiveDataBounds(Stream archive) {
    var position = archive.Position;
    try {
      archive.Position = 0;
      using var reader = new CdiReader(archive, leaveOpen: true);
      var active = reader.ActiveDataTrack!;
      return (active.DataOffset, active.DataOffset + (long)active.DataSectorCount * active.StoredSectorSize);
    } finally {
      archive.Position = position;
    }
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

  private static byte[] TargetUtf8(string text) => System.Text.Encoding.ASCII.GetBytes(text);

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
