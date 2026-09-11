using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Compression.Registry;
using FileSystem.JuiceFs;

namespace Compression.Tests.JuiceFs;

[TestFixture]
public class JuiceFsDetectionTests {
  private static byte[] BuildJsonDump() => Encoding.UTF8.GetBytes("""
    {
      "Setting": {
        "Name": "unit-test",
        "UUID": "01234567-89ab-cdef-0123-456789abcdef",
        "Storage": "file",
        "Bucket": "/var/jfs",
        "MetaVersion": 1,
        "SecretKey": "spaces stay here"
      },
      "Counters": {
        "usedSpace": 4096,
        "usedInodes": 2,
        "nextInodes": 3,
        "nextChunk": 2,
        "nextSession": 1,
        "nextTrash": 0
      },
      "Sustained": [],
      "DelFiles": [],
      "FSTree": {
        "attr": {
          "inode": 1,
          "type": "directory",
          "mode": 493,
          "uid": 0,
          "gid": 0,
          "atime": 0,
          "mtime": 0,
          "ctime": 0,
          "nlink": 2,
          "length": 0
        },
        "entries": {
          "hello.txt": {
            "attr": {
              "inode": 2,
              "type": "regular",
              "mode": 420,
              "uid": 1000,
              "gid": 1000,
              "atime": 0,
              "mtime": 0,
              "ctime": 0,
              "nlink": 1,
              "length": 5
            },
            "chunks": [
              {
                "index": 0,
                "slices": [
                  { "id": 1, "size": 5, "len": 5 }
                ]
              }
            ]
          }
        }
      },
      "Trash": {
        "attr": {
          "inode": 9223372032828243968,
          "type": "directory",
          "mode": 511,
          "uid": 0,
          "gid": 0,
          "atime": 0,
          "mtime": 0,
          "ctime": 0,
          "nlink": 2,
          "length": 0
        },
        "entries": {}
      }
    }
    """);

  private static byte[] BuildBinaryBackup() {
    byte[] formatPayload = [0x0A, 0x02, (byte)'{', (byte)'}'];
    using var stream = new MemoryStream();
    WriteUInt32BigEndian(stream, 1);
    WriteUInt64BigEndian(stream, (ulong)formatPayload.Length);
    stream.Write(formatPayload);
    WriteUInt32BigEndian(stream, JuiceFsReader.BakMagic);

    using var footer = new MemoryStream();
    footer.WriteByte(0x08);
    WriteVarint(footer, JuiceFsReader.BakMagic);
    footer.WriteByte(0x10);
    WriteVarint(footer, JuiceFsReader.BakVersion);
    var footerBytes = footer.ToArray();
    stream.Write(footerBytes);
    WriteUInt64BigEndian(stream, (ulong)footerBytes.Length);
    return stream.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_UsesRealJsonDumpPrefixesInsteadOfSyntheticWrapper() {
    var descriptor = new JuiceFsFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor.Id, Is.EqualTo("JuiceFs"));
      Assert.That(descriptor.MagicSignatures, Has.Count.EqualTo(2));
      Assert.That(descriptor.MagicSignatures.Any(signature => signature.Bytes.SequenceEqual("JuiceFS"u8.ToArray())), Is.False);
      Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveCreatable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.Not.InstanceOf<ILayoutOptimizable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchivePurgeable>());
    });
  }

  [Test, Category("HappyPath")]
  public void JsonDump_ExposesManifestAndRawBackup() {
    using var stream = new MemoryStream(BuildJsonDump());
    using var reader = new JuiceFsReader(stream);
    Assert.That(reader.Kind, Is.EqualTo(JuiceFsBackupKind.Json));
    Assert.That(reader.Entries.Select(entry => entry.Name), Is.EquivalentTo(new[] {
      "metadata.ini", "manifest.tsv", "juicefs-dump.json",
    }));

    var manifest = Encoding.UTF8.GetString(reader.Extract(reader.Entries.Single(entry => entry.Name == "manifest.tsv")));
    Assert.Multiple(() => {
      Assert.That(manifest, Does.Contain("/\tdirectory\t1\t0\t0\t0"));
      Assert.That(manifest, Does.Contain("/hello.txt\tregular\t2\t5\t1\t1"));
    });

    var metadata = Encoding.UTF8.GetString(reader.Extract(reader.Entries.Single(entry => entry.Name == "metadata.ini")));
    Assert.Multiple(() => {
      Assert.That(metadata, Does.Contain("backup_kind=json"));
      Assert.That(metadata, Does.Contain("volume_name=unit-test"));
      Assert.That(metadata, Does.Contain("regular_files=1"));
      Assert.That(metadata, Does.Contain("contains_file_payloads=false"));
    });
  }

  [Test, Category("HappyPath")]
  public void BinaryDump_ParsesCanonicalFramingAndExposesSegments() {
    var image = BuildBinaryBackup();
    using var stream = new MemoryStream(image);
    using var reader = new JuiceFsReader(stream);
    Assert.Multiple(() => {
      Assert.That(reader.Kind, Is.EqualTo(JuiceFsBackupKind.Binary));
      Assert.That(reader.BinaryVersion, Is.EqualTo(JuiceFsReader.BakVersion));
      Assert.That(reader.SegmentCount, Is.EqualTo(1));
      Assert.That(reader.Entries.Select(entry => entry.Name), Is.EquivalentTo(new[] {
        "metadata.ini", "segments/0000-format.pb", "footer.pb", "juicefs-backup.bin",
      }));
    });

    var raw = reader.Extract(reader.Entries.Single(entry => entry.Name == "juicefs-backup.bin"));
    Assert.That(raw, Is.EqualTo(image));
    var segment = reader.Extract(reader.Entries.Single(entry => entry.Name == "segments/0000-format.pb"));
    Assert.That(segment, Is.EqualTo(new byte[] { 0x0A, 0x02, (byte)'{', (byte)'}' }));
  }

  [Test, Category("HappyPath")]
  public void Shrink_JsonDump_RemovesOnlyInsignificantWhitespace() {
    var source = BuildJsonDump();
    using var input = new MemoryStream(source);
    using var output = new MemoryStream();
    ((IArchiveShrinkable)new JuiceFsFormatDescriptor()).Shrink(input, output);
    var shrunk = output.ToArray();

    Assert.That(shrunk.Length, Is.LessThan(source.Length));
    Assert.That(Encoding.UTF8.GetString(shrunk), Does.Contain("spaces stay here"));
    using var before = JsonDocument.Parse(source);
    using var after = JsonDocument.Parse(shrunk);
    Assert.That(JsonElement.DeepEquals(before.RootElement, after.RootElement), Is.True);
    using var validation = new JuiceFsReader(new MemoryStream(shrunk));
    Assert.That(validation.Kind, Is.EqualTo(JuiceFsBackupKind.Json));
  }

  [Test, Category("HappyPath")]
  public void Shrink_BinaryDump_IsByteIdentical() {
    var source = BuildBinaryBackup();
    using var input = new MemoryStream(source);
    using var output = new MemoryStream();
    ((IArchiveShrinkable)new JuiceFsFormatDescriptor()).Shrink(input, output);
    Assert.That(output.ToArray(), Is.EqualTo(source));
  }

  [Test, Category("ExceptionalCase")]
  public void Reader_RejectsOldSyntheticWrapper() {
    var synthetic = new byte[64];
    "JuiceFS"u8.CopyTo(synthetic);
    using var stream = new MemoryStream(synthetic);
    Assert.That(() => _ = new JuiceFsReader(stream), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExceptionalCase")]
  public void Reader_RejectsBinaryBackupWithWrongFooterMagic() {
    var image = BuildBinaryBackup();
    image[^11] ^= 0x01;
    using var stream = new MemoryStream(image);
    Assert.That(() => _ = new JuiceFsReader(stream), Throws.InstanceOf<InvalidDataException>());
  }

  private static void WriteUInt32BigEndian(Stream stream, uint value) {
    Span<byte> bytes = stackalloc byte[sizeof(uint)];
    BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void WriteUInt64BigEndian(Stream stream, ulong value) {
    Span<byte> bytes = stackalloc byte[sizeof(ulong)];
    BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void WriteVarint(Stream stream, ulong value) {
    while (value >= 0x80) {
      stream.WriteByte((byte)(value | 0x80));
      value >>= 7;
    }
    stream.WriteByte((byte)value);
  }
}
