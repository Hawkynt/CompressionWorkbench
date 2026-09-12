#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.SquashFs;

namespace FileFormat.AppImage;

/// <summary>
/// Writer for Linux AppImage type 2 files.
/// </summary>
/// <remarks>
/// <para>
/// A type-2 AppImage is the concatenation of an ELF runtime stub
/// (executable that mounts and runs the embedded filesystem) and a SquashFS
/// v4 image holding the application payload. The runtime carries the
/// magic bytes <c>'A' 'I' 0x02</c> at ELF offset 8 (inside <c>e_ident[EI_PAD]</c>)
/// per the AppImage specification at <c>appimage.org</c>.
/// </para>
/// <para>
/// We don't bundle a real <c>appimagetool</c> runtime — those are 1+ MiB
/// architecture-specific binaries — so this writer emits a minimal
/// 64-bit ELF stub that:
/// </para>
/// <list type="bullet">
///   <item>Starts with the canonical <c>\x7FELF</c> identifier.</item>
///   <item>Carries the <c>'A' 'I' 0x02</c> AppImage type-2 marker at offset 8.</item>
///   <item>Declares the file as ELF64, x86_64, little-endian, executable
///         (matching what the SDK's runtime header looks like at the byte level).</item>
///   <item>Has empty program-header and section-header tables, so the
///         appended SquashFS image starts immediately after the ELF header.</item>
/// </list>
/// <para>
/// The stub does not actually mount the SquashFS payload — running the
/// emitted file under Linux will simply exit (no entry point code).
/// The file is structurally a valid AppImage container that
/// <see cref="AppImageLocator"/> and the system <c>file(1)</c> recognise,
/// and the embedded SquashFS can be extracted by any AppImage extractor.
/// Callers that need an executable AppImage should rebuild the file with
/// <c>appimagetool</c> using the same SquashFS payload.
/// </para>
/// </remarks>
public sealed class AppImageWriter : IDisposable {

  /// <summary>Size of the embedded minimal ELF64 stub in bytes.</summary>
  public const int StubSize = 64;

  /// <summary>AppImage type marker (1 or 2). Type 2 is the only modern format.</summary>
  public const byte AppImageType = 2;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(string Path, byte[] Data, bool IsDirectory)> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="AppImageWriter"/> targeting
  /// <paramref name="stream"/>.
  /// </summary>
  public AppImageWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("AppImage writer requires a seekable stream.", nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>Adds a file entry to the AppImage's SquashFS payload.</summary>
  public void AddFile(string path, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    this._entries.Add((path.Replace('\\', '/').TrimStart('/'), data, false));
  }

  /// <summary>Adds an explicit directory entry to the AppImage's SquashFS payload.</summary>
  public void AddDirectory(string path) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(path);
    this._entries.Add((path.Replace('\\', '/').TrimEnd('/'), [], true));
  }

  /// <summary>
  /// Emits the ELF stub followed by the SquashFS image holding the queued entries.
  /// </summary>
  public void Finish() {
    if (this._finished) return;
    this._finished = true;

    // 1. Emit the 64-byte ELF stub with the AI\x02 marker.
    this._stream.Write(BuildElfStub());

    // 2. Build a SquashFS image into a temporary buffer.
    //    SquashFsWriter rewinds to offset 0 to write its superblock, so it must
    //    own its underlying stream — we copy the finished image after the stub.
    byte[] fsImage;
    using (var fsMs = new MemoryStream()) {
      using (var w = new SquashFsWriter(fsMs, leaveOpen: true)) {
        foreach (var (path, data, isDir) in this._entries) {
          if (isDir)
            w.AddDirectory(path);
          else
            w.AddFile(path, data);
        }
      }
      fsImage = fsMs.ToArray();
    }
    this._stream.Write(fsImage);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (!this._finished) Finish();
    if (!this._leaveOpen) this._stream.Dispose();
  }

  /// <summary>
  /// Builds a minimal 64-byte ELF64 header carrying the
  /// <c>'A' 'I' 0x02</c> AppImage type-2 marker at <c>e_ident[EI_PAD]</c>.
  /// </summary>
  /// <remarks>
  /// The header declares no program or section headers (<c>e_phoff = e_shoff
  /// = 0</c>), so <see cref="AppImageLocator"/> places <c>elfEnd</c> at the
  /// canonical ELF64 header size (64 bytes) and finds the appended SquashFS
  /// magic immediately afterwards.
  /// </remarks>
  internal static byte[] BuildElfStub() {
    var stub = new byte[StubSize];

    // e_ident
    stub[0] = 0x7F;
    stub[1] = (byte)'E';
    stub[2] = (byte)'L';
    stub[3] = (byte)'F';
    stub[4] = 2;      // EI_CLASS    = ELFCLASS64
    stub[5] = 1;      // EI_DATA     = ELFDATA2LSB (little-endian)
    stub[6] = 1;      // EI_VERSION  = EV_CURRENT
    stub[7] = 0;      // EI_OSABI    = ELFOSABI_NONE (Linux)
    stub[8] = (byte)'A';   // AppImage marker — 'A'
    stub[9] = (byte)'I';   // AppImage marker — 'I'
    stub[10] = AppImageType;
    // remaining e_ident bytes (11..15) are EI_PAD, left as zero.

    BinaryPrimitives.WriteUInt16LittleEndian(stub.AsSpan(0x10), 2);   // e_type   = ET_EXEC
    BinaryPrimitives.WriteUInt16LittleEndian(stub.AsSpan(0x12), 62);  // e_machine= EM_X86_64
    BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(0x14), 1);   // e_version

    // e_entry, e_phoff, e_shoff (8 bytes each) all zero → no entry, no program or section table.

    BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(0x30), 0);   // e_flags
    BinaryPrimitives.WriteUInt16LittleEndian(stub.AsSpan(0x34), 64);  // e_ehsize  = 64
    BinaryPrimitives.WriteUInt16LittleEndian(stub.AsSpan(0x36), 0);   // e_phentsize
    BinaryPrimitives.WriteUInt16LittleEndian(stub.AsSpan(0x38), 0);   // e_phnum
    BinaryPrimitives.WriteUInt16LittleEndian(stub.AsSpan(0x3A), 0);   // e_shentsize
    BinaryPrimitives.WriteUInt16LittleEndian(stub.AsSpan(0x3C), 0);   // e_shnum
    BinaryPrimitives.WriteUInt16LittleEndian(stub.AsSpan(0x3E), 0);   // e_shstrndx

    return stub;
  }
}
