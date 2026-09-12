#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileFormat.ResourceDll.ResourceDllConstants;

namespace FileFormat.ResourceDll;

/// <summary>
/// Writes a minimal PE32+ DLL whose only purpose is to host opaque files as Win32
/// resources. Each input becomes one <c>RT_RCDATA</c> resource keyed by its archive
/// name (string ID). Readable from native code via <c>LoadLibraryEx + FindResource +
/// LoadResource + LockResource</c>, from .NET via the same API through P/Invoke, or
/// cross-platform via any PE resource parser (LIEF, pefile, llvm-readobj).
/// </summary>
/// <remarks>
/// The PE has no exports, no imports, no <c>.text</c> section — only headers and a
/// single <c>.rsrc</c> section. Because there is no entry point, callers must use
/// <c>LoadLibraryEx</c> with <c>LOAD_LIBRARY_AS_DATAFILE</c> /
/// <c>LOAD_LIBRARY_AS_IMAGE_RESOURCE</c>: a plain <c>LoadLibrary</c> succeeds (no
/// imports to resolve) but yields a module with no executable code and no
/// <c>DllMain</c>.
/// </remarks>
public sealed class ResourceDllWriter {
  private const ushort PeMagicPe32Plus = 0x020B;
  private const ushort PeMachineAmd64 = 0x8664;
  private const int FileAlignment = 0x200;     // 512
  private const int SectionAlignment = 0x1000; // 4096
  private const ulong ImageBase = 0x1_8000_0000UL;
  private const uint RsrcRva = SectionAlignment; // first section after headers
  private const int CoffHeaderSize = 20;
  private const int OptHeader64Size = 240;
  private const int SectionHeaderSize = 40;
  private const int DosHeaderSize = 0x40;
  private const int PeHeaderOffset = 0x80;
  private const int PeSignatureSize = 4;

  private readonly List<(string Name, byte[] Data)> _entries = [];

  /// <summary>
  /// Adds <paramref name="data"/> as an embedded resource keyed by <paramref name="name"/>.
  /// Names become UTF-16 string IDs in the resource directory.
  /// </summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (string.IsNullOrEmpty(name))
      throw new ArgumentException("Name must be non-empty.", nameof(name));
    if (name.Length > 65535)
      throw new ArgumentException("Resource name length must fit in 16 bits.", nameof(name));
    _entries.Add((name, data));
  }

  /// <summary>Serializes the resource DLL to <paramref name="output"/>.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    var rsrc = BuildResourceSection();
    var sectionRawSize = AlignUp(rsrc.Length, FileAlignment);
    var sectionVirtualSize = rsrc.Length;
    var headersRawSize = AlignUp(GetHeaderSize(), FileAlignment);

    var headers = new byte[headersRawSize];
    WriteDosStub(headers);
    WritePeHeaders(headers, headersRawSize, sectionRawSize, sectionVirtualSize);
    output.Write(headers);

    output.Write(rsrc);
    var sectionPadding = sectionRawSize - rsrc.Length;
    if (sectionPadding > 0)
      output.Write(new byte[sectionPadding]);
  }

  private byte[] BuildResourceSection() {
    var n = _entries.Count;

    // Layout (top-down):
    //   ResourceDirectory level 1 (root): DirHeaderSize + 1 type entry × DirEntrySize
    //   ResourceDirectory level 2 (RT_RCDATA): DirHeaderSize + n × DirEntrySize
    //   ResourceDirectory level 3 (per-name): n × (DirHeaderSize + 1 × DirEntrySize)
    //   ResourceDataEntry × n × DataEntrySize
    //   String pool (UTF-16 lengths + chars, 4-byte aligned)
    //   Resource data blobs (4-byte aligned)

    const int rootHdrOff = 0;
    var typeDirHdrOff = DirHeaderSize + DirEntrySize;
    var nameDirsBaseOff = typeDirHdrOff + DirHeaderSize + n * DirEntrySize;
    var perNameDirStride = DirHeaderSize + DirEntrySize;
    var dataEntriesBaseOff = nameDirsBaseOff + n * perNameDirStride;
    var stringPoolOff = dataEntriesBaseOff + n * DataEntrySize;

    var stringOffsets = new int[n];
    var pos = stringPoolOff;
    for (var i = 0; i < n; i++) {
      stringOffsets[i] = pos;
      pos += 2 + 2 * _entries[i].Name.Length;
      pos = AlignUp(pos, 4);
    }
    var dataBlobsOff = pos;

    var blobOffsets = new int[n];
    pos = dataBlobsOff;
    for (var i = 0; i < n; i++) {
      blobOffsets[i] = pos;
      pos += _entries[i].Data.Length;
      pos = AlignUp(pos, 4);
    }
    var section = new byte[pos];

    WriteDirectoryHeader(section, rootHdrOff, 0, 1);
    BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(rootHdrOff + DirHeaderSize), RtRcData);
    BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(rootHdrOff + DirHeaderSize + 4), HighBitFlag | (uint)typeDirHdrOff);

    WriteDirectoryHeader(section, typeDirHdrOff, n, 0);
    for (var i = 0; i < n; i++) {
      var entryOff = typeDirHdrOff + DirHeaderSize + i * DirEntrySize;
      BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(entryOff + 0), HighBitFlag | (uint)stringOffsets[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(entryOff + 4), HighBitFlag | (uint)(nameDirsBaseOff + i * perNameDirStride));
    }

    for (var i = 0; i < n; i++) {
      var dirOff = nameDirsBaseOff + i * perNameDirStride;
      WriteDirectoryHeader(section, dirOff, 0, 1);
      // Language ID 0 = LANG_NEUTRAL; child offset points at the data entry (high bit clear → leaf).
      BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(dirOff + DirHeaderSize + 4), (uint)(dataEntriesBaseOff + i * DataEntrySize));
    }

    for (var i = 0; i < n; i++) {
      var entryOff = dataEntriesBaseOff + i * DataEntrySize;
      // DataRva is an *image* RVA, so we add the section's RVA to the in-section offset.
      BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(entryOff + 0), RsrcRva + (uint)blobOffsets[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(entryOff + 4), (uint)_entries[i].Data.Length);
    }

    for (var i = 0; i < n; i++) {
      var name = _entries[i].Name;
      var off = stringOffsets[i];
      BinaryPrimitives.WriteUInt16LittleEndian(section.AsSpan(off), (ushort)name.Length);
      Encoding.Unicode.GetBytes(name, section.AsSpan(off + 2));
    }

    for (var i = 0; i < n; i++)
      _entries[i].Data.AsSpan().CopyTo(section.AsSpan(blobOffsets[i]));

    return section;
  }

  private static void WriteDirectoryHeader(byte[] section, int off, int numNamed, int numId) {
    BinaryPrimitives.WriteUInt16LittleEndian(section.AsSpan(off + 12), (ushort)numNamed);
    BinaryPrimitives.WriteUInt16LittleEndian(section.AsSpan(off + 14), (ushort)numId);
  }

  private static int GetHeaderSize() =>
    PeHeaderOffset + PeSignatureSize + CoffHeaderSize + OptHeader64Size + SectionHeaderSize;

  private static void WriteDosStub(byte[] image) {
    image[0] = (byte)'M';
    image[1] = (byte)'Z';
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C), PeHeaderOffset);
    "This program cannot be run in DOS mode.\r\n$"u8.CopyTo(image.AsSpan(DosHeaderSize));
  }

  private void WritePeHeaders(byte[] image, int headersSize, int sectionRawSize, int sectionVirtualSize) {
    image[PeHeaderOffset + 0] = (byte)'P';
    image[PeHeaderOffset + 1] = (byte)'E';

    var coffOff = PeHeaderOffset + PeSignatureSize;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coffOff + 0), PeMachineAmd64);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coffOff + 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coffOff + 16), OptHeader64Size);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coffOff + 18), 0x2022); // EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE | DLL

    var optOff = coffOff + CoffHeaderSize;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optOff + 0), PeMagicPe32Plus);
    image[optOff + 2] = 14; // MajorLinkerVersion
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optOff + 8), (uint)sectionRawSize);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(optOff + 24), ImageBase);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optOff + 32), SectionAlignment);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optOff + 36), FileAlignment);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optOff + 40), 6); // MajorOSVersion
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optOff + 48), 6); // MajorSubsystemVersion
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optOff + 56),
      (uint)(RsrcRva + AlignUp(sectionVirtualSize, SectionAlignment)));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optOff + 60), (uint)headersSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optOff + 68), 3); // Subsystem WINDOWS_CUI
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optOff + 70), 0x8160);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(optOff + 72), 0x100000); // SizeOfStackReserve
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(optOff + 80), 0x1000);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(optOff + 88), 0x100000); // SizeOfHeapReserve
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(optOff + 96), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optOff + 108), 16); // NumberOfRvaAndSizes

    // Resource Table is data directory index 2.
    var ddOff = optOff + 112;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ddOff + 2 * 8 + 0), RsrcRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ddOff + 2 * 8 + 4), (uint)sectionVirtualSize);

    var secOff = optOff + OptHeader64Size;
    ".rsrc"u8.CopyTo(image.AsSpan(secOff));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(secOff + 8), (uint)sectionVirtualSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(secOff + 12), RsrcRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(secOff + 16), (uint)sectionRawSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(secOff + 20), (uint)headersSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(secOff + 36), 0x40000040); // CNT_INITIALIZED_DATA | MEM_READ
  }

  private static int AlignUp(int v, int alignment) {
    var rem = v % alignment;
    return rem == 0 ? v : v + (alignment - rem);
  }
}
