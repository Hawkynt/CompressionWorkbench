using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Rarc;

namespace Compression.Tests.Rarc;

[TestFixture]
public sealed class RarcTests {
  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var descriptor = new RarcFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor.Id, Is.EqualTo("Rarc"));
      Assert.That(descriptor.Extensions, Does.Contain(".arc"));
      Assert.That(descriptor.MagicSignatures.Single().Bytes, Is.EqualTo("RARC"u8.ToArray()).AsCollection);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
        "rebuild-backed add/remove is WORM, not genuine in-place R/W");
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_RoundTripsNestedPayloads() {
    var hello = "hello from rarc\n"u8.ToArray();
    byte[] nested = [0, 1, 2, 3, 4, 5, 0xFE, 0xFF];
    ArchiveInputInfo[] inputs = [
      ArchiveInputInfo.InMemory("HELLO.TXT", hello),
      ArchiveInputInfo.InMemory("sub/inner.bin", nested),
    ];

    using var output = new MemoryStream();
    var descriptor = new RarcFormatDescriptor();
    descriptor.Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    var listed = descriptor.List(output, null);
    Assert.Multiple(() => {
      Assert.That(listed.Select(entry => entry.Name), Does.Contain("HELLO.TXT"));
      Assert.That(listed.Select(entry => entry.Name), Does.Contain("sub"));
      Assert.That(listed.Select(entry => entry.Name), Does.Contain("sub/inner.bin"));
    });

    output.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(output, "HELLO.TXT", null), Is.EqualTo(hello).AsCollection);
    output.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(output, "sub/inner.bin", null), Is.EqualTo(nested).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void Writer_EmitsCanonicalAlignedBigEndianSectionsAndLoadBlocks() {
    using var output = new MemoryStream();
    using (var writer = new RarcWriter(output, leaveOpen: true)) {
      writer.AddEntry("mram.bin", [0x11]);
      writer.AddEntry("aram.bin", new byte[33], RarcEntryAttributes.File | RarcEntryAttributes.PreloadToAram);
      writer.AddEntry("dvd.bin", [0x22, 0x33], RarcEntryAttributes.File | RarcEntryAttributes.LoadFromDvd);
      writer.Finish();
    }

    var data = output.ToArray();
    Assert.That(data.AsSpan(0, 4).SequenceEqual("RARC"u8), Is.True);
    var declaredSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
    var dataHeaderOffset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
    var fileDataOffset = checked(dataHeaderOffset + BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12, 4)));
    var totalData = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4));
    var mramData = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4));
    var aramData = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(24, 4));
    var dvdData = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(28, 4));

    Assert.Multiple(() => {
      Assert.That(declaredSize, Is.EqualTo((uint)data.Length));
      Assert.That(dataHeaderOffset, Is.EqualTo(0x20u));
      Assert.That(fileDataOffset % 0x20u, Is.Zero);
      Assert.That(mramData, Is.EqualTo(0x20u));
      Assert.That(aramData, Is.EqualTo(0x40u));
      Assert.That(dvdData, Is.EqualTo(0x20u));
      Assert.That(totalData, Is.EqualTo(mramData + aramData + dvdData));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x20, 4)), Is.EqualTo(1u));
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x28, 4)), Is.EqualTo(5u));
      Assert.That(data.AsSpan(0x40, 4).SequenceEqual("ROOT"u8), Is.True);
    });
  }

  [Test, Category("EdgeCase")]
  public void NameHash_KnownVectors() {
    Assert.Multiple(() => {
      Assert.That(RarcReader.CalculateNameHash("abc"), Is.EqualTo(0x04F2));
      Assert.That(RarcReader.CalculateNameHash("root"), Is.EqualTo(0x11AE));
      Assert.That(RarcReader.CalculateNameHash("."), Is.EqualTo(0x002E));
      Assert.That(RarcReader.CalculateNameHash(".."), Is.EqualTo(0x00B8));
    });
  }

  [Test, Category("EdgeCase")]
  public void Writer_RejectsUnsafeDuplicateAndFileDirectoryConflicts() {
    using var output = new MemoryStream();
    using var writer = new RarcWriter(output, leaveOpen: true);
    writer.AddEntry("foo", [1]);

    Assert.Multiple(() => {
      Assert.That(() => writer.AddEntry("foo", [2]), Throws.TypeOf<ArgumentException>());
      Assert.That(() => writer.AddEntry("foo/bar.bin", [3]), Throws.TypeOf<ArgumentException>());
      Assert.That(() => writer.AddEntry("../escape.bin", [4]), Throws.TypeOf<ArgumentException>());
      Assert.That(() => writer.AddEntry("bad/./name.bin", [5]), Throws.TypeOf<ArgumentException>());
      Assert.That(() => writer.AddEntry("ümlaut.bin", [6]), Throws.TypeOf<ArgumentException>());
    });
  }

  [Test, Category("EdgeCase")]
  public void Reader_RejectsTruncatedOrOutOfRangeMetadata() {
    Assert.That(() => new RarcReader(new MemoryStream("RARC"u8.ToArray())),
      Throws.TypeOf<InvalidDataException>());

    using var output = new MemoryStream();
    using (var writer = new RarcWriter(output, leaveOpen: true)) {
      writer.AddEntry("file.bin", [1, 2, 3]);
      writer.Finish();
    }
    var malformed = output.ToArray();
    BinaryPrimitives.WriteUInt32BigEndian(malformed.AsSpan(0x2C, 4), 0xFFFF_FFF0u);
    Assert.That(() => new RarcReader(new MemoryStream(malformed)),
      Throws.TypeOf<InvalidDataException>());
  }
}
