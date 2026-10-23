#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FileFormat.FirmwareCommon;

namespace FileFormat.LinuxKernelImage;

/// <summary>
/// A bootable Linux kernel image, recognised by the header its architecture's boot protocol
/// defines, and split into the parts that protocol names: the x86 real-mode setup and the
/// compressed payload of a <c>bzImage</c>, the payload of an EFI <c>zboot</c> image, the
/// decompressor-wrapped payload of an ARM <c>zImage</c>; the uncompressed arm64 / RISC-V
/// <c>Image</c> is described but has no inner payload.
/// </summary>
/// <remarks>
/// Header layouts are from the kernel's own documentation, which is normative for them:
/// <c>Documentation/arch/x86/boot.rst</c> (setup header at 0x1F1, <c>HdrS</c> at 0x202,
/// <c>payload_offset</c>/<c>payload_length</c> from protocol 2.08), <c>Documentation/arch/arm64/booting.rst</c>
/// and <c>Documentation/arch/riscv/boot-image-header.rst</c> (64-byte image header), the ARM zImage
/// table at 0x24 (<c>arch/arm/boot/compressed/head.S</c>) and the EFI zboot header
/// (<c>drivers/firmware/efi/libstub/zboot-header.S</c>). A payload is decompressed with this
/// repository's own codecs and only reported when the decoder accepts it and the result carries
/// a <c>Linux version</c> banner.
/// </remarks>
public sealed class LinuxKernelImage {

  public string Kind { get; private set; } = "";
  public List<(string Key, string Value)> Fields { get; } = [];
  public List<FirmwareEntry> Parts { get; } = [];

  public const uint ArmZImageMagic = 0x016F2818;

  public static bool LooksLikeX86(ReadOnlySpan<byte> d) => d.Length >= 0x264 && d[0x1FE] == 0x55 && d[0x1FF] == 0xAA && d.Slice(0x202, 4).SequenceEqual("HdrS"u8);
  public static bool LooksLikeArmZImage(ReadOnlySpan<byte> d) => d.Length >= 0x30 && BinaryPrimitives.ReadUInt32LittleEndian(d[0x24..]) == ArmZImageMagic;
  public static bool LooksLikeArm64(ReadOnlySpan<byte> d) => d.Length >= 64 && d.Slice(0x38, 4).SequenceEqual("ARM\x64"u8);
  public static bool LooksLikeRiscV(ReadOnlySpan<byte> d) => d.Length >= 64 && d.Slice(0x38, 4).SequenceEqual("RSC\x05"u8);
  public static bool LooksLikeZboot(ReadOnlySpan<byte> d) => d.Length >= 0x40 && d[0] == (byte)'M' && d[1] == (byte)'Z' && d.Slice(4, 4).SequenceEqual("zimg"u8);

  public static LinuxKernelImage Parse(ReadOnlyMemory<byte> data) {
    var d = data.Span;
    var img = new LinuxKernelImage();
    // zboot first: its MZ stub would otherwise be mistaken for nothing in particular.
    if (LooksLikeZboot(d)) img.ParseZboot(data);
    else if (LooksLikeX86(d)) img.ParseX86(data);
    else if (LooksLikeArm64(d)) img.ParseImageHeader(data, "arm64 Image");
    else if (LooksLikeRiscV(d)) img.ParseImageHeader(data, "RISC-V Image");
    else if (LooksLikeArmZImage(d)) img.ParseArmZImage(data);
    else throw new InvalidDataException("Linux kernel image: no x86 setup header, ARM zImage table, arm64/RISC-V image header or EFI zboot header.");
    return img;
  }

  private void Field(string key, string value) => this.Fields.Add((key, value));
  private static string Dec(ulong v) => v.ToString(CultureInfo.InvariantCulture);

  private static bool HasBanner(byte[] decoded) => decoded.AsSpan().IndexOf("Linux version "u8) >= 0;

  private static string? Banner(ReadOnlySpan<byte> d) {
    var at = d.IndexOf("Linux version "u8);
    if (at < 0) return null;
    var end = d[at..].IndexOfAny((byte)0, (byte)'\n');
    return Encoding.ASCII.GetString(end < 0 ? d.Slice(at, Math.Min(200, d.Length - at)) : d.Slice(at, Math.Min(end, 200)));
  }

  private void AddPayload(ReadOnlyMemory<byte> payload, string decodedName) {
    if (EmbeddedPayload.Identify(payload.Span) is not { } kind) {
      this.Field("payload_compression", "unknown");
      this.Parts.Add(new("payload.bin", payload));
      return;
    }
    this.Field("payload_compression", kind.Id.ToLowerInvariant());
    this.Parts.Add(new("payload." + kind.Extension, payload));
    if (EmbeddedPayload.TryDecompress(kind.Id, payload) is { } decoded && HasBanner(decoded)) {
      this.Field("payload_decoded_size", Dec((ulong)decoded.Length));
      if (Banner(decoded) is { } banner) this.Field("linux_banner", banner);
      this.Parts.Add(new(decodedName, decoded));
    } else
      this.Field("payload_decoded", "false");
  }

  private void ParseX86(ReadOnlyMemory<byte> data) {
    var d = data.Span;
    this.Kind = "x86 bzImage";
    var setupSects = d[0x1F1] == 0 ? 4 : d[0x1F1];
    var protocol = BinaryPrimitives.ReadUInt16LittleEndian(d[0x206..]);
    var pmStart = (setupSects + 1) * 512;
    if (pmStart > d.Length) throw new InvalidDataException($"Linux bzImage: {setupSects} setup sectors run past the end of the image.");
    var sysSize = BinaryPrimitives.ReadUInt32LittleEndian(d[0x1F4..]) * 16L;
    var loadFlags = d[0x211];
    this.Field("boot_protocol", $"{protocol >> 8}.{protocol & 0xFF:D2}");
    this.Field("setup_sects", Dec((ulong)setupSects));
    this.Field("syssize_bytes", Dec((ulong)sysSize));
    this.Field("loadflags", FirmwareArchive.Hex(loadFlags));
    if (protocol >= 0x200) {
      var versionPointer = BinaryPrimitives.ReadUInt16LittleEndian(d[0x20E..]);
      if (versionPointer != 0 && versionPointer + 0x200 < pmStart)
        this.Field("kernel_version", FirmwareArchive.CString(d[(versionPointer + 0x200)..pmStart]));
    }
    this.Parts.Add(new("setup.bin", data[..pmStart]));
    var pmEnd = sysSize > 0 && pmStart + sysSize <= d.Length ? pmStart + (int)sysSize : d.Length;
    this.Parts.Add(new("protected_mode.bin", data[pmStart..pmEnd]));
    if (protocol >= 0x20A) this.Field("init_size", Dec(BinaryPrimitives.ReadUInt32LittleEndian(d[0x260..])));
    if (protocol >= 0x208) {
      var payloadOffset = BinaryPrimitives.ReadUInt32LittleEndian(d[0x248..]);
      var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(d[0x24C..]);
      this.Field("payload_offset", FirmwareArchive.Hex(payloadOffset));
      this.Field("payload_length", Dec(payloadLength));
      if (payloadLength > 0 && (long)pmStart + payloadOffset + payloadLength <= d.Length)
        this.AddPayload(data.Slice(pmStart + (int)payloadOffset, (int)payloadLength), "vmlinux");
    }
    if (pmEnd < d.Length) this.Parts.Add(new("trailer.bin", data[pmEnd..]));
  }

  private void ParseZboot(ReadOnlyMemory<byte> data) {
    var d = data.Span;
    this.Kind = "EFI zboot";
    var offset = BinaryPrimitives.ReadUInt32LittleEndian(d[8..]);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(d[12..]);
    var compression = FirmwareArchive.CString(d.Slice(0x18, 32));
    this.Field("payload_offset", FirmwareArchive.Hex(offset));
    this.Field("payload_length", Dec(size));
    this.Field("compression_type", compression);
    if ((long)offset + size > d.Length || size == 0) throw new InvalidDataException("Linux EFI zboot: the payload runs past the end of the image.");
    this.Parts.Add(new("efi_stub.bin", data[..(int)offset]));
    this.AddPayload(data.Slice((int)offset, (int)size), "Image");
    var end = (int)(offset + size);
    if (end < d.Length) this.Parts.Add(new("trailer.bin", data[end..]));
  }

  private void ParseImageHeader(ReadOnlyMemory<byte> data, string kind) {
    var d = data.Span;
    this.Kind = kind;
    this.Field("text_offset", FirmwareArchive.Hex(BinaryPrimitives.ReadUInt64LittleEndian(d[8..])));
    var imageSize = BinaryPrimitives.ReadUInt64LittleEndian(d[16..]);
    this.Field("image_size", Dec(imageSize));
    var flags = BinaryPrimitives.ReadUInt64LittleEndian(d[24..]);
    this.Field("flags", FirmwareArchive.Hex(flags));
    if (kind.StartsWith("arm64", StringComparison.Ordinal)) {
      this.Field("endianness", (flags & 1) != 0 ? "big" : "little");
      this.Field("page_size", (flags >> 1 & 3) switch { 1 => "4K", 2 => "16K", 3 => "64K", _ => "unspecified" });
    } else {
      var version = BinaryPrimitives.ReadUInt32LittleEndian(d[32..]);
      this.Field("header_version", $"{version >> 16}.{version & 0xFFFF}");
    }
    if (Banner(d) is { } banner) this.Field("linux_banner", banner);
  }

  private void ParseArmZImage(ReadOnlyMemory<byte> data) {
    var d = data.Span;
    this.Kind = "ARM zImage";
    var start = BinaryPrimitives.ReadUInt32LittleEndian(d[0x28..]);
    var end = BinaryPrimitives.ReadUInt32LittleEndian(d[0x2C..]);
    this.Field("load_start", FirmwareArchive.Hex(start));
    this.Field("load_end", FirmwareArchive.Hex(end));
    var length = end >= start ? end - start : 0;
    this.Field("zimage_length", Dec(length));
    var image = length > 0 && length <= d.Length ? data[..(int)length] : data;
    if (EmbeddedPayload.Find(image, 0x30, HasBanner) is { } found) {
      this.Field("payload_offset", FirmwareArchive.Hex((uint)found.Offset));
      this.Field("payload_compression", found.Id.ToLowerInvariant());
      this.Field("payload_decoded_size", Dec((ulong)found.Decoded.Length));
      if (Banner(found.Decoded) is { } banner) this.Field("linux_banner", banner);
      this.Parts.Add(new("Image", found.Decoded));
    } else
      this.Field("payload_decoded", "false");
    if (length > 0 && length < d.Length) this.Parts.Add(new("appended.bin", data[(int)length..]));
  }
}
