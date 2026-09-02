#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Detector and payload locator for MidgetPack (github.com/arisada/midgetpack)
/// ELF crypters. MidgetPack appends the AES-encrypted original ELF to a
/// precompiled stub and describes it with an extra <c>PT_LOAD</c> program
/// header: the added segment is mapped read/write/execute, starts past the end
/// of the stub, and runs to end-of-file. Because the payload is a whole number
/// of cipher blocks, its length is always a multiple of 16.
/// </summary>
/// <remarks>
/// <para>
/// Detection keys on that segment plus a cross-check that is independent of any
/// particular stub build: the stub keeps its own copy of the payload's load
/// address and length in writable data, so the segment's <c>p_vaddr</c> followed
/// immediately by its <c>p_filesz</c> appears verbatim somewhere ahead of the
/// payload. Requiring the echo is what keeps ordinary executables — which never
/// carry an RWX segment reaching exactly end-of-file whose address and size are
/// repeated in their own data — from matching.
/// </para>
/// <para>
/// The AES key is derived at run time, either by PBKDF2 over a password the user
/// types or from a Curve25519 key-exchange keyfile, and is <em>never</em> stored
/// in the binary. Static recovery of the plaintext is therefore impossible; this
/// handler honestly reports <see cref="ExecutableUnpackLevel.PayloadLocated"/>,
/// carving the encrypted payload while making the missing-key limitation
/// explicit in diagnostics.
/// </para>
/// </remarks>
public sealed class MidgetPackExecutablePackerHandler : IExecutablePackerHandler {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "midgetpack";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "MidgetPack ELF crypter (AES-CBC, runtime key)";

  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.SupportsElf |
    ExecutableUnpackCapabilities.SupportsX86 |
    ExecutableUnpackCapabilities.SupportsX64 |
    ExecutableUnpackCapabilities.SupportsArm32;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (TryParseDescriptor(image.ToArray(), out _))
      return new(true, this.Id, 1.0, []);
    return new(false, this.Id, 0,
      [new(ExecutableDiagnosticCode.NotPackedExecutable,
        "No MidgetPack payload segment (RWX PT_LOAD reaching end-of-file, with its address and length echoed in stub data) was found.", true)]);
  }

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var bytes = image.ToArray();
    var info = ExecutableContainerParsers.ParseBestEffort(image);
    return new(this.Id, bytes, detection, info, this.Capabilities, new Dictionary<string, string> {
      ["packer"] = this.Id,
      ["container"] = info.Container.ToString(),
      ["architecture"] = info.Architecture.ToString(),
      ["cipher"] = "aes-cbc",
    });
  }

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var image = packed.OriginalImage;
    if (image.LongLength > options.MaximumInputSize)
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [],
        [new(ExecutableDiagnosticCode.PayloadNotFound, "Input exceeds configured executable unpacking size limit.", true)]);

    if (!TryParseDescriptor(image, out var desc))
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [],
        [new(ExecutableDiagnosticCode.PayloadNotFound, "MidgetPack descriptor could not be parsed.", true)]);

    // Layout: stub | encrypted payload, the latter mapped by the appended PT_LOAD.
    var payloadOffset = desc.PayloadOffset;
    if (payloadOffset < 0 || (long)payloadOffset + desc.DataLen > image.Length)
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [],
        [new(ExecutableDiagnosticCode.PayloadNotFound, "MidgetPack payload region falls outside the file.", true)]);

    var payload = image.AsSpan(payloadOffset, desc.DataLen).ToArray();
    var keySource = desc.Type switch {
      1 => "a password typed at run time (PBKDF2)",
      2 => "a Curve25519 keyfile supplied at run time",
      _ => "a secret supplied at run time",
    };
    var diagnostics = new List<ExecutableDiagnostic> {
      new(ExecutableDiagnosticCode.TransformNotReversible,
        $"MidgetPack encrypts the payload with AES under a session key derived from {keySource}; the key is not stored in the binary, so static decryption is impossible. Encrypted payload is located only."),
    };

    var artifacts = new List<UnpackArtifact> {
      new("metadata.ini", BuildMetadata(image.Length, desc, payloadOffset), "stored"),
      new("original_packed.bin", image, "stored"),
      new("encrypted_payload.bin", payload, "aes-cbc"),
    };

    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.SupportsElf | ExecutableUnpackCapabilities.SupportsX86 |
      ExecutableUnpackCapabilities.SupportsX64 | ExecutableUnpackCapabilities.SupportsArm32;
    var result = new UnpackResult(ExecutableUnpackLevel.PayloadLocated, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private readonly record struct Descriptor(int Offset, bool Is64, int PayloadOffset, int DataLen, ulong PayloadAddress, uint Type);

  /// <summary>
  /// Locates the appended MidgetPack payload from the program header table.
  /// </summary>
  /// <remarks>
  /// The added segment is the only <c>PT_LOAD</c> that is simultaneously mapped
  /// RWX, ends exactly at end-of-file, and has a cipher-block-aligned length. The
  /// stub's copy of the payload address and length — the same two values the
  /// segment header carries, stored adjacently in writable data — is then
  /// required to appear ahead of the payload, which is what makes the match
  /// specific rather than merely structural.
  /// </remarks>
  private static bool TryParseDescriptor(byte[] image, out Descriptor descriptor) {
    descriptor = default;
    if (image.Length < 0x40 || image[0] != 0x7F || image[1] != 'E' || image[2] != 'L' || image[3] != 'F')
      return false;
    var is64 = image[4] == 2;
    if (image[5] != 1) // little-endian only; MidgetPack targets x86/x86-64/ARM LE.
      return false;

    ulong phoff;
    ushort phentsize;
    ushort phnum;
    if (is64) {
      phoff = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x20));
      phentsize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x36));
      phnum = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x38));
    } else {
      phoff = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x1C));
      phentsize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x2A));
      phnum = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x2C));
    }
    if (phoff == 0 || phentsize < (is64 ? 56 : 32) || phnum == 0 || phnum > 4096)
      return false;
    if (phoff + (ulong)phentsize * phnum > (ulong)image.Length)
      return false;

    for (var i = 0; i < phnum; ++i) {
      var o = (int)phoff + i * phentsize;
      if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(o)) != 1) // PT_LOAD
        continue;

      ulong pOffset;
      ulong pVaddr;
      ulong pFilesz;
      uint flags;
      if (is64) {
        flags = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(o + 4));
        pOffset = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(o + 8));
        pVaddr = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(o + 16));
        pFilesz = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(o + 32));
      } else {
        pOffset = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(o + 4));
        pVaddr = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(o + 8));
        pFilesz = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(o + 16));
        flags = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(o + 24));
      }

      // PF_R|PF_W|PF_X: the payload is decrypted in place, so it must be writable
      // and executable as well as readable.
      if (flags != 7 || pOffset == 0 || pFilesz == 0 || pFilesz % 16 != 0)
        continue;
      if (pOffset + pFilesz != (ulong)image.Length)
        continue;

      // The stub's own copy of (payload address, payload length), stored as
      // adjacent fields, must appear somewhere in the stub ahead of the payload.
      Span<byte> echo = stackalloc byte[12];
      if (is64) {
        BinaryPrimitives.WriteUInt64LittleEndian(echo, pVaddr);
        BinaryPrimitives.WriteUInt32LittleEndian(echo[8..], (uint)pFilesz);
      } else {
        BinaryPrimitives.WriteUInt32LittleEndian(echo, (uint)pVaddr);
        BinaryPrimitives.WriteUInt32LittleEndian(echo[4..], (uint)pFilesz);
        echo = echo[..8];
      }

      var stub = image.AsSpan(0, (int)pOffset);
      var at = stub.IndexOf(echo);
      if (at < 0)
        continue;

      // The pack type sits a fixed distance behind the address/length pair in the
      // same descriptor; report it only when it names a mode MidgetPack defines.
      var typeOffset = at + echo.Length + 0x10;
      var type = typeOffset + 4 <= stub.Length
        ? BinaryPrimitives.ReadUInt32LittleEndian(stub[typeOffset..])
        : 0u;
      if (type is not (1 or 2))
        type = 0;

      descriptor = new(at, is64, (int)pOffset, (int)pFilesz, pVaddr, type);
      return true;
    }
    return false;
  }

  private static byte[] BuildMetadata(int imageSize, Descriptor desc, int payloadOffset) {
    var sb = new StringBuilder();
    sb.AppendLine("[midgetpack]");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"image_size = {imageSize}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"container = {(desc.Is64 ? "ELF64" : "ELF32")}\n");
    sb.AppendLine("cipher = aes-cbc");
    if (desc.Type != 0)
      sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"pack_type = {(desc.Type == 2 ? "curve25519" : "password")}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"descriptor_offset = 0x{desc.Offset:X}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"payload_offset = 0x{payloadOffset:X}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"payload_address = 0x{desc.PayloadAddress:X}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"payload_size = {desc.DataLen}\n");
    sb.AppendLine("capability_level = PayloadLocated");
    sb.AppendLine("note = AES key derived at runtime (PBKDF2 password or Curve25519 keyfile); not embedded, so plaintext cannot be recovered statically.");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
