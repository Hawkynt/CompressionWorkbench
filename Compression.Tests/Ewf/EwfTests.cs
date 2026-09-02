using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Ewf;
using FileFormat.Zlib;

namespace Compression.Tests.Ewf;

[TestFixture]
public class EwfTests {

  private static byte[] BuildEwf(byte[] headerPayload, bool logical = false) {
    const int headerSize = EwfReader.FileHeaderSize;
    const int descSize = EwfReader.SectionDescriptorSize;
    var headerSectionSize = descSize + headerPayload.Length;
    var doneSectionOffset = headerSize + headerSectionSize;
    var total = doneSectionOffset + descSize;
    var buf = new byte[total];

    (logical ? EwfReader.LvfSignature : EwfReader.EvfSignature).CopyTo(buf, 0);
    buf[8] = 0x01;
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(9), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(11), 0);

    WriteSectionType(buf, headerSize, "header");
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(headerSize + 16), (ulong)doneSectionOffset);
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(headerSize + 24), (ulong)headerSectionSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(headerSize + 72), 0xCAFEBABE);
    headerPayload.CopyTo(buf.AsSpan(headerSize + descSize));

    WriteSectionType(buf, doneSectionOffset, "done");
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(doneSectionOffset + 16), (ulong)doneSectionOffset);
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(doneSectionOffset + 24), descSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(doneSectionOffset + 72), 0xDEADBEEF);
    return buf;
  }

  private static void WriteSectionType(byte[] buf, int offset, string type) {
    var ascii = Encoding.ASCII.GetBytes(type);
    Buffer.BlockCopy(ascii, 0, buf, offset, Math.Min(ascii.Length, 16));
  }

  private static byte[] SampleHeader() {
    var text = "1\r\n" +
               "a\tc\tn\te\tt\tav\tov\tm\tu\tp\tr\r\n" +
               "Description\tCASE123\tEVIDENCE7\tExaminer\tNotes\t20060101\t20060102\tMD5\tUnknown\tp\tr\r\n";
    return ZlibStream.Compress(Encoding.UTF8.GetBytes(text));
  }

  private static byte[] Media(int length = 3 * EwfWriter.ChunkSize) {
    var data = new byte[length];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)((i * 17 + i / 101) & 0xFF);
    return data;
  }

  [Test, Category("HappyPath")]
  public void Read_ParsesEvfSignatureAndSections() {
    var data = BuildEwf(SampleHeader());
    var img = EwfReader.Read(data);
    Assert.That(img.IsLogical, Is.False);
    Assert.That(img.SegmentNumber, Is.EqualTo((ushort)1));
    Assert.That(img.Sections, Has.Count.EqualTo(2));
    Assert.That(img.Sections[0].Type, Is.EqualTo("header"));
    Assert.That(img.Sections[1].Type, Is.EqualTo("done"));
  }

  [Test, Category("HappyPath")]
  public void Read_RecognisesLvfSignature() {
    var img = EwfReader.Read(BuildEwf(SampleHeader(), logical: true));
    Assert.That(img.IsLogical, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_EmitsMetadataAndSectionEntries() {
    using var ms = new MemoryStream(BuildEwf(SampleHeader()));
    var names = new EwfFormatDescriptor().List(ms, null).Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names.Any(n => n.StartsWith("section_00_header", StringComparison.Ordinal)), Is.True);
    Assert.That(names.Any(n => n.StartsWith("section_01_done", StringComparison.Ordinal)), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_ExtractsMetadataIniWithAcquisitionBlock() {
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(BuildEwf(SampleHeader()));
      new EwfFormatDescriptor().Extract(ms, tmp, null, null);
      var text = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(text, Does.Contain("[ewf]"));
      Assert.That(text, Does.Contain("section_count = 2"));
      Assert.That(text, Does.Contain("[acquisition]"));
      Assert.That(text, Does.Contain("CASE123"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [TestCase(false), TestCase(true)]
  [Category("HappyPath"), Category("RoundTrip")]
  public void Writer_Reader_ReconstructsLogicalMedia(bool compress) {
    var media = Media();
    var encoded = new EwfWriter { CompressChunks = compress }.Build(media);
    var decoded = EwfReader.ExtractMedia(EwfReader.Read(encoded));
    Assert.That(decoded.AsSpan(0, media.Length).ToArray(), Is.EqualTo(media));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_List_ExposesMutableMediaRaw() {
    var media = Media();
    using var image = new MemoryStream(new EwfWriter().Build(media), writable: true);
    var descriptor = new EwfFormatDescriptor();
    var mediaEntry = descriptor.List(image, null).Single(e => e.Name == "media.raw");
    Assert.That(mediaEntry.OriginalSize, Is.EqualTo(media.Length));
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveLayoutMap>());
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Add_ReplacesMediaRaw() {
    var original = Media(EwfWriter.ChunkSize);
    var replacement = Media(EwfWriter.ChunkSize * 2);
    replacement.AsSpan().Reverse();
    using var image = new MemoryStream();
    image.Write(new EwfWriter { CompressChunks = true }.Build(original));
    image.Position = 0;

    var descriptor = new EwfFormatDescriptor();
    descriptor.Add(image, [ArchiveInputInfo.InMemory("media.raw", replacement)]);
    image.Position = 0;
    var decoded = EwfReader.ExtractMedia(EwfReader.Read(image.ToArray()));
    Assert.That(decoded.AsSpan(0, replacement.Length).ToArray(), Is.EqualTo(replacement));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_RemoveMedia_LeavesValidEmptyEvf() {
    using var image = new MemoryStream();
    image.Write(new EwfWriter().Build(Media(EwfWriter.ChunkSize)));
    image.Position = 0;
    var descriptor = new EwfFormatDescriptor();
    descriptor.Remove(image, ["media.raw"]);
    var parsed = EwfReader.Read(image.ToArray());
    Assert.That(EwfReader.ExtractMedia(parsed), Is.Empty);
    Assert.That(parsed.Sections.Any(s => s.Type == "done"), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Defragment_PreservesMediaAndReportsLayout() {
    var media = Media();
    using var image = new MemoryStream();
    image.Write(new EwfWriter { CompressChunks = true }.Build(media));
    image.Position = 0;
    var phases = new List<string>();
    var descriptor = new EwfFormatDescriptor();
    descriptor.Defragment(image, new DefragOptions {
      OnProgress = e => phases.Add(e.Phase),
    });
    var decoded = EwfReader.ExtractMedia(EwfReader.Read(image.ToArray()));
    Assert.That(decoded.AsSpan(0, media.Length).ToArray(), Is.EqualTo(media));
    Assert.That(phases, Does.Contain("scanning"));
    Assert.That(phases, Does.Contain("writing"));
    Assert.That(phases, Does.Contain("committing"));
    Assert.That(phases, Does.Contain("complete"));
    Assert.That(descriptor.EnumerateLayout(image).Any(), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Shrink_ChoosesSmallerValidRepresentation() {
    var media = new byte[EwfWriter.ChunkSize * 4];
    Array.Fill(media, (byte)0x41);
    var original = new EwfWriter { CompressChunks = false }.Build(media);
    using var input = new MemoryStream(original);
    using var output = new MemoryStream();
    new EwfFormatDescriptor().Shrink(input, output);
    Assert.That(output.Length, Is.LessThan(original.Length));
    var decoded = EwfReader.ExtractMedia(EwfReader.Read(output.ToArray()));
    Assert.That(decoded.AsSpan(0, media.Length).ToArray(), Is.EqualTo(media));
  }

  [Test, Category("EdgeCase")]
  public void Read_BadSignature_Throws() {
    var data = new byte[64];
    Encoding.ASCII.GetBytes("NOTEWF!!").CopyTo(data.AsMemory());
    Assert.That(() => EwfReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_TruncatedHeader_Throws() {
    var data = new byte[8];
    EwfReader.EvfSignature.CopyTo(data, 0);
    Assert.That(() => EwfReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_BadFieldsStart_Throws() {
    var data = BuildEwf(SampleHeader());
    data[8] = 0x02;
    Assert.That(() => EwfReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }
}
