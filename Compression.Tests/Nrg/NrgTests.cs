using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Nrg;

namespace Compression.Tests.Nrg;

[TestFixture]
public class NrgTests {
  private static byte[] CreateNrg(params (string Name, byte[] Data)[] files) {
    var descriptor = new NrgFormatDescriptor();
    using var image = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(
      image,
      files.Select(static file => ArchiveInputInfo.InMemory(file.Name, file.Data)).ToArray(),
      new FormatCreateOptions());
    return image.ToArray();
  }

  private static byte[] ExtractCanonicalIso(string fileName, byte[] data) {
    var canonical = CreateNrg((fileName, data));
    var trailerOffset = checked((int)BinaryPrimitives.ReadUInt64BigEndian(canonical.AsSpan(canonical.Length - 8)));
    return canonical.AsSpan(0, trailerOffset).ToArray();
  }

  private static byte[] BuildOffsetTrackNrg(string fileName, byte[] data, int prefixLength = 4096) {
    var iso = ExtractCanonicalIso(fileName, data);

    using var output = new MemoryStream();
    output.Write(new byte[prefixLength]);
    output.Write(iso);
    var trailerOffset = checked((ulong)output.Position);

    Span<byte> etn2 = stackalloc byte[32];
    BinaryPrimitives.WriteUInt64BigEndian(etn2, checked((ulong)prefixLength));
    BinaryPrimitives.WriteUInt64BigEndian(etn2[8..], checked((ulong)iso.LongLength));
    etn2[19] = 0x00;
    WriteChunk(output, "ETN2"u8, etn2);

    WriteCommonTrailerChunks(output);

    Span<byte> footer = stackalloc byte[12];
    "NER5"u8.CopyTo(footer);
    BinaryPrimitives.WriteUInt64BigEndian(footer[4..], trailerOffset);
    output.Write(footer);
    return output.ToArray();
  }

  private static byte[] BuildLegacyOffsetTrackNrg(string fileName, byte[] data, int prefixLength = 4096) {
    var iso = ExtractCanonicalIso(fileName, data);

    using var output = new MemoryStream();
    output.Write(new byte[prefixLength]);
    output.Write(iso);
    var trailerOffset = checked((uint)output.Position);

    Span<byte> etnf = stackalloc byte[20];
    BinaryPrimitives.WriteUInt32BigEndian(etnf, checked((uint)prefixLength));
    BinaryPrimitives.WriteUInt32BigEndian(etnf[4..], checked((uint)iso.Length));
    etnf[11] = 0x00;
    WriteChunk(output, "ETNF"u8, etnf);

    WriteCommonTrailerChunks(output);

    Span<byte> footer = stackalloc byte[8];
    "NERO"u8.CopyTo(footer);
    BinaryPrimitives.WriteUInt32BigEndian(footer[4..], trailerOffset);
    output.Write(footer);
    return output.ToArray();
  }

  private static void WriteCommonTrailerChunks(Stream output) {
    Span<byte> sinf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(sinf, 1);
    WriteChunk(output, "SINF"u8, sinf);

    Span<byte> mtyp = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(mtyp, 0x00000400);
    WriteChunk(output, "MTYP"u8, mtyp);
    WriteChunk(output, "END!"u8, ReadOnlySpan<byte>.Empty);
  }

  private static void WriteChunk(Stream output, ReadOnlySpan<byte> id, ReadOnlySpan<byte> payload) {
    Span<byte> header = stackalloc byte[8];
    id.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)payload.Length));
    output.Write(header);
    output.Write(payload);
  }

  private static byte[] ReadFile(byte[] image, string leaf) {
    using var stream = new MemoryStream(image, writable: false);
    using var reader = new NrgReader(stream);
    var entry = reader.Entries.Single(e => !e.IsDirectory && e.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase));
    return reader.Extract(entry);
  }

  [Test, Category("HappyPath")]
  public void Read_Nrg_DetectsVersion2Footer() {
    using var stream = new MemoryStream(CreateNrg(("README.TXT", "NRG content"u8.ToArray())));
    using var reader = new NrgReader(stream);
    Assert.That(reader.Version, Is.EqualTo(2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_WritesRealV2DaoChunkChain() {
    var image = CreateNrg(("DATA.BIN", "nero-payload"u8.ToArray()));
    Assert.That(image.AsSpan(image.Length - 12, 4).ToArray(), Is.EqualTo("NER5"u8.ToArray()));

    var trailerOffset = checked((int)BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(image.Length - 8)));
    Assert.That(trailerOffset, Is.LessThan(image.Length - 12));
    Assert.That(trailerOffset % 2048, Is.Zero);

    var position = trailerOffset;
    var cuex = ReadChunk(image, ref position, "CUEX", 32);
    Assert.Multiple(() => {
      Assert.That(cuex[0], Is.EqualTo(0x41));
      Assert.That(cuex[1], Is.EqualTo(0x00));
      Assert.That(cuex[2], Is.EqualTo(0x00));
      Assert.That(unchecked((int)BinaryPrimitives.ReadUInt32BigEndian(cuex[4..])), Is.EqualTo(-150));
      Assert.That(cuex[9], Is.EqualTo(0x01));
      Assert.That(cuex[10], Is.EqualTo(0x00));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(cuex[12..]), Is.Zero);
      Assert.That(cuex[17], Is.EqualTo(0x01));
      Assert.That(cuex[18], Is.EqualTo(0x01));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(cuex[20..]), Is.Zero);
      Assert.That(cuex[25], Is.EqualTo(0xAA));
      Assert.That(cuex[26], Is.EqualTo(0x01));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(cuex[28..]), Is.EqualTo((uint)(trailerOffset / 2048)));
    });

    var daox = ReadChunk(image, ref position, "DAOX", 64);
    var track = daox[22..];
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(daox), Is.EqualTo(64));
      Assert.That(daox[20], Is.EqualTo(1));
      Assert.That(daox[21], Is.EqualTo(1));
      Assert.That(BinaryPrimitives.ReadUInt16BigEndian(track[12..]), Is.EqualTo(2048));
      Assert.That(track[14], Is.Zero, "cooked Mode 1");
      Assert.That(BinaryPrimitives.ReadUInt16BigEndian(track[16..]), Is.EqualTo(1));
      Assert.That(BinaryPrimitives.ReadUInt64BigEndian(track[18..]), Is.Zero);
      Assert.That(BinaryPrimitives.ReadUInt64BigEndian(track[26..]), Is.Zero);
      Assert.That(BinaryPrimitives.ReadUInt64BigEndian(track[34..]), Is.EqualTo((ulong)trailerOffset));
    });

    var sinf = ReadChunk(image, ref position, "SINF", 4);
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(sinf), Is.EqualTo(1));

    var mtyp = ReadChunk(image, ref position, "MTYP", 4);
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(mtyp), Is.EqualTo(0x00000400));

    _ = ReadChunk(image, ref position, "END!", 0);
    Assert.That(position, Is.EqualTo(image.Length - 12));
  }

  private static ReadOnlySpan<byte> ReadChunk(byte[] image, ref int position, string id, int expectedLength) {
    Assert.That(System.Text.Encoding.ASCII.GetString(image, position, 4), Is.EqualTo(id));
    var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(position + 4, 4)));
    Assert.That(length, Is.EqualTo(expectedLength));
    var payload = image.AsSpan(position + 8, length);
    position += 8 + length;
    return payload;
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Read_UsesEtn2TrackOffset_InsteadOfAssumingOffsetZero() {
    var payload = "track-offset"u8.ToArray();
    var image = BuildOffsetTrackNrg("OFFSET.BIN", payload);
    Assert.That(ReadFile(image, "OFFSET.BIN"), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Read_LegacyNeroEtnf_Uses32BitTrackOffset() {
    var payload = "legacy-track-offset"u8.ToArray();
    var image = BuildLegacyOffsetTrackNrg("LEGACY.BIN", payload);

    using var stream = new MemoryStream(image, writable: false);
    using var reader = new NrgReader(stream);
    Assert.That(reader.Version, Is.EqualTo(1));
    var entry = reader.Entries.Single(e => !e.IsDirectory && e.Name.Equals("LEGACY.BIN", StringComparison.OrdinalIgnoreCase));
    Assert.That(reader.Extract(entry), Is.EqualTo(payload));
  }

  [Test, Category("Regression")]
  public void Read_TruncatedChunkLength_FailsClosedAndFallsBackToLegacyTrackProbe() {
    var canonical = CreateNrg(("SAFE.BIN", "safe"u8.ToArray()));
    var trailer = checked((int)BinaryPrimitives.ReadUInt64BigEndian(canonical.AsSpan(canonical.Length - 8)));
    var iso = canonical.AsSpan(0, trailer).ToArray();

    using var output = new MemoryStream();
    output.Write(iso);
    var malformedTrailer = checked((ulong)output.Position);
    output.Write("ETN2"u8);
    Span<byte> absurdLength = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(absurdLength, uint.MaxValue);
    output.Write(absurdLength);
    Span<byte> footer = stackalloc byte[12];
    "NER5"u8.CopyTo(footer);
    BinaryPrimitives.WriteUInt64BigEndian(footer[4..], malformedTrailer);
    output.Write(footer);

    Assert.That(ReadFile(output.ToArray(), "SAFE.BIN"), Is.EqualTo("safe"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Create_PreservesDirectoryPaths() {
    var image = CreateNrg(("DIR/SUB/FILE.BIN", "nested"u8.ToArray()));
    using var stream = new MemoryStream(image);
    using var reader = new NrgReader(stream);

    Assert.That(reader.Entries.Any(e => !e.IsDirectory && e.FullPath.Equals("DIR/SUB/FILE.BIN", StringComparison.OrdinalIgnoreCase)), Is.True);
    Assert.That(ReadFile(image, "FILE.BIN"), Is.EqualTo("nested"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Modify_AddReplaceRemove_RoundTripsNamedIsoEntries() {
    var descriptor = new NrgFormatDescriptor();
    using var image = new MemoryStream();
    image.Write(CreateNrg(("SEED.BIN", "seed"u8.ToArray())));
    image.Position = 0;

    ((IArchiveModifiable)descriptor).Add(image, [ArchiveInputInfo.InMemory("PROBE.BIN", "probe"u8.ToArray())]);
    Assert.That(ReadFile(image.ToArray(), "SEED.BIN"), Is.EqualTo("seed"u8.ToArray()));
    Assert.That(ReadFile(image.ToArray(), "PROBE.BIN"), Is.EqualTo("probe"u8.ToArray()));

    ((IArchiveModifiable)descriptor).Add(image, [ArchiveInputInfo.InMemory("PROBE.BIN", "replaced"u8.ToArray())]);
    Assert.That(ReadFile(image.ToArray(), "PROBE.BIN"), Is.EqualTo("replaced"u8.ToArray()));

    ((IArchiveModifiable)descriptor).Remove(image, ["PROBE.BIN"]);
    using var after = new MemoryStream(image.ToArray());
    using var reader = new NrgReader(after);
    Assert.That(reader.Entries.Any(e => !e.IsDirectory && e.Name.Equals("PROBE.BIN", StringComparison.OrdinalIgnoreCase)), Is.False);
    Assert.That(ReadFile(image.ToArray(), "SEED.BIN"), Is.EqualTo("seed"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Maintenance_RebuildsOffsetImage_WithoutChangingPayload() {
    var payload = Enumerable.Range(0, 1000).Select(static i => (byte)(i * 31)).ToArray();
    var source = BuildOffsetTrackNrg("KEEP.BIN", payload, prefixLength: 8192);
    var descriptor = new NrgFormatDescriptor();

    using var defrag = new MemoryStream();
    defrag.Write(source);
    defrag.Position = 0;
    ((IArchiveDefragmentable)descriptor).Defragment(defrag);
    Assert.That(ReadFile(defrag.ToArray(), "KEEP.BIN"), Is.EqualTo(payload));

    using var input = new MemoryStream(source);
    using var shrunk = new MemoryStream();
    ((IArchiveShrinkable)descriptor).Shrink(input, shrunk);
    Assert.That(shrunk.Length, Is.LessThan(source.Length));
    Assert.That(ReadFile(shrunk.ToArray(), "KEEP.BIN"), Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void Purge_LeavesValidEmptyNrg() {
    var descriptor = new NrgFormatDescriptor();
    using var image = new MemoryStream();
    image.Write(CreateNrg(("DELETE.ME", "gone"u8.ToArray())));
    image.Position = 0;

    ((IArchivePurgeable)descriptor).Purge(image);

    image.Position = 0;
    using var reader = new NrgReader(image, leaveOpen: true);
    Assert.That(reader.Version, Is.EqualTo(2));
    Assert.That(reader.Entries.Where(static e => !e.IsDirectory), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesOnlySupportedMaintenanceVerbs() {
    var descriptor = new NrgFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.InstanceOf<IArchivePurgeable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.Not.InstanceOf<ILayoutOptimizable>());
    });
  }

  [Test, Category("HappyPath")]
  public void Read_NoFooter_VersionIsZero() {
    using var stream = new MemoryStream(new byte[2352 * 32]);
    using var reader = new NrgReader(stream);
    Assert.That(reader.Version, Is.Zero);
  }

  [Test, Category("HappyPath")]
  public void Detect_ByExtension() {
    var format = Compression.Lib.FormatDetector.DetectByExtension("disc.nrg");
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.Nrg));
  }
}
