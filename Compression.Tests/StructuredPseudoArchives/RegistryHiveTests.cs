using System.Buffers.Binary;
using System.Text;
using FileFormat.Creg;
using FileFormat.Regf;

namespace Compression.Tests.StructuredPseudoArchives;

[TestFixture]
public sealed class RegistryHiveTests {
  [Test]
  public void Creg_ParsesWindows9xKeyAndTypedValue() {
    var descriptor = new CregFormatDescriptor();
    using var stream = new MemoryStream(BuildCregVector());

    var entries = descriptor.List(stream, null);

    Assert.That(entries.Any(e => e.Name == "Name" && e.Kind == "REG_SZ" && e.OriginalSize == 6), Is.True);
  }

  [Test]
  public void Regf_ParsesNtFamilySubkeysAndInlineValues() {
    var descriptor = new RegfFormatDescriptor();
    using var stream = new MemoryStream(BuildRegfVector());

    var entries = descriptor.List(stream, null);

    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "Name" && e.Kind == "REG_SZ"), Is.True);
      Assert.That(entries.Any(e => e.Name == "Flags" && e.Kind == "REG_DWORD"), Is.True);
      Assert.That(entries.Any(e => e.IsDirectory && e.Name == "Child"), Is.True);
    });
  }

  [Test]
  public void Regf_ParsesVersion11LegacyCellsAndUtf16Names() {
    var descriptor = new RegfFormatDescriptor();
    using var stream = new MemoryStream(BuildRegf11Vector());

    var entries = descriptor.List(stream, null);

    Assert.That(entries.Any(e => e.Name == "Number" && e.Kind == "REG_DWORD"), Is.True);
  }

  [Test]
  public void Regf_RejectsUnallocatedRootCell() {
    var bytes = BuildRegfVector();
    var rootOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(36, 4));
    var rootCell = checked(4096 + (int)rootOffset);
    var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(rootCell, 4));
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(rootCell, 4), -size);

    var descriptor = new RegfFormatDescriptor();
    using var stream = new MemoryStream(bytes);
    Assert.That(() => descriptor.List(stream, null), Throws.TypeOf<InvalidDataException>());
  }

  private static byte[] BuildCregVector() {
    var data = new byte[256];
    "CREG"u8.CopyTo(data);
    WriteU16(data, 4, 0);
    WriteU16(data, 6, 1);
    WriteU32(data, 8, 128);
    WriteU16(data, 16, 1);

    "RGKN"u8.CopyTo(data.AsSpan(32));
    WriteU32(data, 36, 96);
    WriteU32(data, 40, 32);

    var hierarchy = 64;
    WriteU32(data, hierarchy + 8, 0xffffffff);
    WriteU32(data, hierarchy + 12, 0xffffffff);
    WriteU32(data, hierarchy + 16, 0xffffffff);
    WriteU32(data, hierarchy + 20, 0xffffffff);
    WriteU16(data, hierarchy + 24, 0);
    WriteU16(data, hierarchy + 26, 0);

    "RGDB"u8.CopyTo(data.AsSpan(128));
    WriteU32(data, 132, 128);
    WriteU16(data, 142, 0);

    var key = 160;
    const int keyEntrySize = 43;
    WriteU32(data, key, keyEntrySize);
    WriteU16(data, key + 4, 0);
    WriteU32(data, key + 8, keyEntrySize);
    WriteU16(data, key + 12, 0);
    WriteU16(data, key + 14, 1);

    var value = key + 20;
    WriteU32(data, value, 1);
    WriteU16(data, value + 8, 4);
    WriteU16(data, value + 10, 7);
    Encoding.Latin1.GetBytes("Name").CopyTo(data, value + 12);
    Encoding.Latin1.GetBytes("Widget\0").CopyTo(data, value + 16);
    return data;
  }

  private static byte[] BuildRegfVector() {
    var data = new byte[8192];
    "regf"u8.CopyTo(data);
    WriteU32(data, 4, 1);
    WriteU32(data, 8, 1);
    WriteU32(data, 20, 1);
    WriteU32(data, 24, 5);
    WriteU32(data, 28, 0);
    WriteU32(data, 32, 1);
    WriteU32(data, 40, 4096);
    WriteU32(data, 44, 1);

    "hbin"u8.CopyTo(data.AsSpan(4096));
    WriteU32(data, 4096 + 4, 0);
    WriteU32(data, 4096 + 8, 4096);

    var nextRelative = 0x20;
    uint Alloc(ReadOnlySpan<byte> payload) {
      var totalSize = (payload.Length + 4 + 7) & ~7;
      var relative = checked((uint)nextRelative);
      var absolute = 4096 + nextRelative;
      BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(absolute, 4), -totalSize);
      payload.CopyTo(data.AsSpan(absolute + 4));
      nextRelative += totalSize;
      return relative;
    }

    var stringData = Alloc(Encoding.Unicode.GetBytes("hello\0"));

    var stringValue = new byte[24];
    "vk"u8.CopyTo(stringValue);
    WriteU16(stringValue, 2, 4);
    WriteU32(stringValue, 4, 12);
    WriteU32(stringValue, 8, stringData);
    WriteU32(stringValue, 12, 1);
    WriteU16(stringValue, 16, 1);
    Encoding.Latin1.GetBytes("Name").CopyTo(stringValue, 20);
    var stringValueOffset = Alloc(stringValue);

    var dwordValue = new byte[25];
    "vk"u8.CopyTo(dwordValue);
    WriteU16(dwordValue, 2, 5);
    WriteU32(dwordValue, 4, 0x80000004);
    WriteU32(dwordValue, 8, 42);
    WriteU32(dwordValue, 12, 4);
    WriteU16(dwordValue, 16, 1);
    Encoding.Latin1.GetBytes("Flags").CopyTo(dwordValue, 20);
    var dwordValueOffset = Alloc(dwordValue);

    var valueList = new byte[8];
    WriteU32(valueList, 0, stringValueOffset);
    WriteU32(valueList, 4, dwordValueOffset);
    var valueListOffset = Alloc(valueList);

    var childKey = new byte[81];
    "nk"u8.CopyTo(childKey);
    WriteU16(childKey, 2, 0x20);
    WriteU32(childKey, 16, 0xffffffff);
    WriteU32(childKey, 28, 0xffffffff);
    WriteU32(childKey, 32, 0xffffffff);
    WriteU32(childKey, 40, 0xffffffff);
    WriteU32(childKey, 44, 0xffffffff);
    WriteU32(childKey, 48, 0xffffffff);
    WriteU16(childKey, 72, 5);
    Encoding.Latin1.GetBytes("Child").CopyTo(childKey, 76);
    var childKeyOffset = Alloc(childKey);

    var subkeyList = new byte[12];
    "lh"u8.CopyTo(subkeyList);
    WriteU16(subkeyList, 2, 1);
    WriteU32(subkeyList, 4, childKeyOffset);
    var subkeyListOffset = Alloc(subkeyList);

    var rootKey = new byte[80];
    "nk"u8.CopyTo(rootKey);
    WriteU16(rootKey, 2, 0x20);
    WriteU32(rootKey, 16, 0xffffffff);
    WriteU32(rootKey, 20, 1);
    WriteU32(rootKey, 28, subkeyListOffset);
    WriteU32(rootKey, 32, 0xffffffff);
    WriteU32(rootKey, 36, 2);
    WriteU32(rootKey, 40, valueListOffset);
    WriteU32(rootKey, 44, 0xffffffff);
    WriteU32(rootKey, 48, 0xffffffff);
    WriteU16(rootKey, 72, 4);
    Encoding.Latin1.GetBytes("ROOT").CopyTo(rootKey, 76);
    var rootKeyOffset = Alloc(rootKey);
    WriteU32(data, 36, rootKeyOffset);

    return data;
  }

  private static byte[] BuildRegf11Vector() {
    var data = new byte[8192];
    "regf"u8.CopyTo(data);
    WriteU32(data, 4, 1);
    WriteU32(data, 8, 1);
    WriteU32(data, 20, 1);
    WriteU32(data, 24, 1);
    WriteU32(data, 28, 0);
    WriteU32(data, 32, 1);
    WriteU32(data, 40, 4096);
    WriteU32(data, 44, 1);

    "hbin"u8.CopyTo(data.AsSpan(4096));
    WriteU32(data, 4096 + 4, 0);
    WriteU32(data, 4096 + 8, 4096);

    var nextRelative = 0x20;
    uint previousRelative = 0xffffffff;
    uint Alloc(ReadOnlySpan<byte> payload) {
      var totalSize = (payload.Length + 8 + 15) & ~15;
      var relative = checked((uint)nextRelative);
      var absolute = 4096 + nextRelative;
      BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(absolute, 4), -totalSize);
      WriteU32(data, absolute + 4, previousRelative);
      payload.CopyTo(data.AsSpan(absolute + 8));
      previousRelative = relative;
      nextRelative += totalSize;
      return relative;
    }

    var value = new byte[32];
    "vk"u8.CopyTo(value);
    WriteU16(value, 2, 12);
    WriteU32(value, 4, 0x80000004);
    WriteU32(value, 8, 42);
    WriteU32(value, 12, 4);
    Encoding.Unicode.GetBytes("Number").CopyTo(value, 20);
    var valueOffset = Alloc(value);

    var valueList = new byte[4];
    WriteU32(valueList, 0, valueOffset);
    var valueListOffset = Alloc(valueList);

    var rootKey = new byte[84];
    "nk"u8.CopyTo(rootKey);
    WriteU32(rootKey, 16, 0xffffffff);
    WriteU32(rootKey, 28, 0xffffffff);
    WriteU32(rootKey, 32, 0xffffffff);
    WriteU32(rootKey, 36, 1);
    WriteU32(rootKey, 40, valueListOffset);
    WriteU32(rootKey, 44, 0xffffffff);
    WriteU32(rootKey, 48, 0xffffffff);
    WriteU16(rootKey, 72, 8);
    Encoding.Unicode.GetBytes("ROOT").CopyTo(rootKey, 76);
    var rootKeyOffset = Alloc(rootKey);
    WriteU32(data, 36, rootKeyOffset);

    return data;
  }

  /// <summary>
  /// The root key of a real hive carries the 0xffff "no key-name entry" sentinel in both halves of
  /// its RGDB reference, because it has no name. Reading that as a block index aborts the hive on
  /// its very first key, which is what made <c>CanList</c> false for every Windows 9x
  /// <c>USER.DAT</c> in existence while a synthetic vector that never emits the sentinel passed.
  /// </summary>
  [Test]
  public void Creg_TreatsTheAllOnesKeyNameReferenceAsAnUnnamedKey() {
    var descriptor = new CregFormatDescriptor();
    using var stream = new MemoryStream(BuildCregSentinelVector());

    var entries = descriptor.List(stream, null);

    Assert.That(entries.Any(e => e.IsDirectory && e.Name == "Named"), Is.True);
  }

  /// <summary>
  /// RGKN addresses a key name by the identifier the RGDB record stores in its own header, not by
  /// the record's position in the block. Hives reuse freed slots, so the two disagree constantly --
  /// in the checked-in <c>USER.DAT</c> for 776 of 801 records. Positional lookup does not fail
  /// loudly on that; it hands back a neighbouring key's name.
  /// </summary>
  [Test]
  public void Creg_ResolvesKeyNamesByRecordIdentifierNotBlockPosition() {
    var descriptor = new CregFormatDescriptor();
    // Two records whose identifiers run opposite to their order in the block: position 0 is id 1.
    using var stream = new MemoryStream(BuildCregLookupVector(
      records: [(Id: (ushort)1, Name: "Second"), (Id: (ushort)0, Name: "First")],
      childEntryIndex: 1));

    var entries = descriptor.List(stream, null);

    Assert.That(entries.Any(e => e.IsDirectory && e.Name == "Second"), Is.True,
      "the key referencing identifier 1 resolved to the record at position 1 instead of the record whose identifier is 1");
  }

  /// <summary>
  /// The sentinel on a key that is not the root. The root's own name is discarded by the
  /// projection, so handling it there could be right by accident; a child has to come through as a
  /// nameless key, which the projection escapes to <c>%00</c>, rather than aborting the hive.
  /// </summary>
  [Test]
  public void Creg_ProjectsASentinelChildAsANamelessKey() {
    var descriptor = new CregFormatDescriptor();
    using var stream = new MemoryStream(BuildCregLookupVector(
      records: [(Id: (ushort)0, Name: "Named")],
      childEntryIndex: ushort.MaxValue));

    var entries = descriptor.List(stream, null);

    Assert.That(entries.Any(e => e.IsDirectory && e.Name == "%00"), Is.True);
  }

  /// <summary>A hive whose root carries the sentinel and whose single child is a named key.</summary>
  private static byte[] BuildCregSentinelVector()
    => BuildCregLookupVector([(Id: (ushort)0, Name: "Named")], childEntryIndex: 0, sentinelRoot: true);

  /// <summary>
  /// A minimal CREG hive: one RGKN record holding a root entry plus one child entry, and one RGDB
  /// block holding <paramref name="records"/> in the order given. The child points at
  /// <paramref name="childEntryIndex"/> as an RGDB key-name identifier.
  /// </summary>
  private static byte[] BuildCregLookupVector(
    (ushort Id, string Name)[] records,
    ushort childEntryIndex,
    bool sentinelRoot = true
  ) {
    const int navigationOffset = 32;
    const int navigationSize = 96;
    const int dataBlockOffset = navigationOffset + navigationSize;
    const int dataBlockSize = 256;

    var data = new byte[dataBlockOffset + dataBlockSize];
    "CREG"u8.CopyTo(data);
    WriteU16(data, 4, 0);
    WriteU16(data, 6, 1);
    WriteU32(data, 8, dataBlockOffset);
    WriteU16(data, 16, 1);

    "RGKN"u8.CopyTo(data.AsSpan(navigationOffset));
    WriteU32(data, navigationOffset + 4, navigationSize);
    WriteU32(data, navigationOffset + 8, 32); // root entry, relative to the RGKN record

    const int root = navigationOffset + 32;
    const int child = root + 28;
    WriteU32(data, root + 12, 0xffffffff); // no parent
    WriteU32(data, root + 16, (uint)(child - navigationOffset));
    WriteU32(data, root + 20, 0xffffffff); // no sibling
    WriteU16(data, root + 24, sentinelRoot ? ushort.MaxValue : (ushort)0);
    WriteU16(data, root + 26, sentinelRoot ? ushort.MaxValue : (ushort)0);

    WriteU32(data, child + 12, (uint)(root - navigationOffset));
    WriteU32(data, child + 16, 0xffffffff); // no children
    WriteU32(data, child + 20, 0xffffffff); // no siblings
    WriteU16(data, child + 24, childEntryIndex);
    WriteU16(data, child + 26, 0);

    "RGDB"u8.CopyTo(data.AsSpan(dataBlockOffset));
    WriteU32(data, dataBlockOffset + 4, dataBlockSize);

    var cursor = dataBlockOffset + 32;
    foreach (var (id, name) in records) {
      var nameBytes = Encoding.Latin1.GetBytes(name);
      var recordSize = 20 + nameBytes.Length;
      WriteU32(data, cursor, (uint)recordSize);
      WriteU16(data, cursor + 4, id);
      WriteU16(data, cursor + 6, 0); // the block this record belongs to
      WriteU32(data, cursor + 8, (uint)recordSize);
      WriteU16(data, cursor + 12, (ushort)nameBytes.Length);
      WriteU16(data, cursor + 14, 0); // no values
      nameBytes.CopyTo(data, cursor + 20);
      cursor += recordSize;
    }

    return data;
  }

  private static void WriteU16(byte[] data, int offset, ushort value)
    => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);

  private static void WriteU32(byte[] data, int offset, uint value)
    => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
}
