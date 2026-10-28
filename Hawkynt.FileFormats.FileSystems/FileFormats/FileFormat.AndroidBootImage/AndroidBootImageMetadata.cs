#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using FileFormat.FirmwareCommon;

namespace FileFormat.AndroidBootImage;

/// <summary>
/// The <c>metadata.ini</c> rendering of an Android boot / vendor_boot header, and the reverse:
/// applying an edited rendering back onto an image. A key is applied only when its value differs
/// from what the image itself renders, so handing back an untouched rendering changes nothing —
/// not even the bytes past the NUL of a string field.
/// </summary>
public static class AndroidBootImageMetadata {

  public const string BootSection = "android_boot";
  public const string VendorSection = "android_vendor_boot";

  /// <summary>Rendered for information only; editing them has no effect.</summary>
  private static readonly HashSet<string> Informational = new(StringComparer.OrdinalIgnoreCase) {
    "magic", "image_id", "image_id_valid", "canonical", "canonical_issue", "trailer_size", "avb_footer",
    "boot_signature_size", "parse_status", "error",
  };

  public static byte[] Render(AndroidBootImage img) {
    var sb = new StringBuilder();
    void Line(string key, string value) => sb.Append(key).Append(" = ").Append(value).Append('\n');

    sb.Append('[').Append(img.IsVendor ? VendorSection : BootSection).Append("]\n");
    Line("magic", img.IsVendor ? AndroidBootImage.VendorBootMagic : AndroidBootImage.BootMagic);
    Line("header_version", Dec(img.HeaderVersion));
    if (img.IsVendor || img.HeaderVersion < 3) {
      Line("page_size", Dec(img.PageSize));
      Line("kernel_load_address", FirmwareArchive.Hex(img.KernelAddress));
      Line("ramdisk_load_address", FirmwareArchive.Hex(img.RamdiskAddress));
      if (!img.IsVendor) Line("second_load_address", FirmwareArchive.Hex(img.SecondAddress));
      Line("tags_load_address", FirmwareArchive.Hex(img.TagsAddress));
    }
    if (img.IsVendor || img.HeaderVersion == 2) Line("dtb_load_address", "0x" + img.DtbAddress.ToString("X16", CultureInfo.InvariantCulture));
    if (!img.IsVendor) {
      Line("os_version", AndroidBootImage.FormatOsVersion(img.OsVersionPatchLevel));
      Line("os_patch_level", AndroidBootImage.FormatOsPatchLevel(img.OsVersionPatchLevel));
    }
    if (img.IsVendor || img.HeaderVersion < 3) Line("board", FirmwareArchive.CString(img.Board));
    Line("cmdline", FirmwareArchive.CString(img.Cmdline));
    if (!img.IsVendor && img.HeaderVersion < 3) Line("extra_cmdline", FirmwareArchive.CString(img.ExtraCmdline));
    if (img.IsVendor || img.HeaderVersion >= 1) Line("header_size", Dec(img.HeaderSize));
    if (!img.IsVendor && img.HeaderVersion < 3) {
      Line("image_id", Convert.ToHexStringLower(img.ImageId));
      Line("image_id_valid", Bool(img.ImageIdValid));
    }
    if (!img.IsVendor && img.HeaderVersion >= 4) Line("boot_signature_size", Dec((uint)img.BootSignature.Length));
    Line("trailer_size", Dec((uint)img.Trailer.Length));
    Line("avb_footer", Bool(img.HasAvbFooter));
    Line("canonical", Bool(img.IsCanonical));
    if (img.NonCanonicalReason is { } reason) Line("canonical_issue", reason);
    Line("parse_status", "ok");

    if (img.IsVendor && img.HeaderVersion >= 4)
      for (var i = 0; i < img.VendorRamdisks.Count; ++i) {
        var r = img.VendorRamdisks[i];
        sb.Append('[').Append(RamdiskEntryName(i)).Append("]\n");
        Line("type", Dec(r.Type));
        Line("name", FirmwareArchive.CString(r.Name));
        Line("board_id", string.Join(',', r.BoardId.Select(w => FirmwareArchive.Hex(w))));
      }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  public static byte[] RenderPartial(string error) => Encoding.UTF8.GetBytes(
    $"[{BootSection}]\nparse_status = partial\nerror = {error.ReplaceLineEndings(" ")}\n");

  public static string RamdiskEntryName(int index) => "vendor_ramdisk" + index.ToString("D2", CultureInfo.InvariantCulture);

  /// <summary>
  /// A fresh image of the kind and header version <paramref name="ini"/> names (boot v0 when it
  /// names none), carrying <c>mkbootimg</c>'s defaults: base 0x10000000 with its standard offsets,
  /// 2048-byte pages, and the header size of the version.
  /// </summary>
  public static AndroidBootImage NewImage(IReadOnlyDictionary<string, string>? ini, bool vendorHint) {
    var vendor = vendorHint;
    if (ini is not null) {
      if (ini.Keys.Any(k => k.StartsWith(VendorSection + ".", StringComparison.OrdinalIgnoreCase))) vendor = true;
      else if (ini.Keys.Any(k => k.StartsWith(BootSection + ".", StringComparison.OrdinalIgnoreCase))) vendor = false;
    }
    var section = vendor ? VendorSection : BootSection;
    uint version = vendor ? 3u : 0u;
    if (ini is not null && ini.TryGetValue(section + ".header_version", out var v)) {
      if (!FirmwareArchive.TryParseNumber(v, out var parsed) || parsed > 4 || vendor && parsed < 3)
        throw new ArgumentException($"metadata.ini: header_version '{v}' is not valid for a {(vendor ? "vendor_boot" : "boot")} image.");
      version = (uint)parsed;
    }

    const uint Base = 0x10000000;
    var img = new AndroidBootImage {
      IsVendor = vendor,
      HeaderVersion = version,
      PageSize = !vendor && version >= 3 ? AndroidBootImage.V3PageSize : 2048u,
      KernelAddress = Base + 0x00008000,
      RamdiskAddress = Base + 0x01000000,
      SecondAddress = Base + 0x00F00000,
      TagsAddress = Base + 0x00000100,
      DtbAddress = Base + 0x01F00000UL,
      Cmdline = new byte[vendor ? AndroidBootImage.VendorArgsSize : version >= 3 ? AndroidBootImage.BootV3ArgsSize : AndroidBootImage.BootArgsSize],
      HeaderSize = vendor
        ? version >= 4 ? AndroidBootImage.VendorV4HeaderSize : AndroidBootImage.VendorV3HeaderSize
        : version switch {
          1 => AndroidBootImage.BootV1HeaderSize, 2 => AndroidBootImage.BootV2HeaderSize,
          3 => AndroidBootImage.BootV3HeaderSize, 4 => AndroidBootImage.BootV4HeaderSize, _ => 0,
        },
    };
    return img;
  }

  /// <summary>Applies every key of <paramref name="ini"/> whose value differs from the image's own rendering.</summary>
  public static void Apply(AndroidBootImage img, IReadOnlyDictionary<string, string> ini) {
    var current = FirmwareArchive.ParseIni(Render(img));
    var section = img.IsVendor ? VendorSection : BootSection;
    var other = img.IsVendor ? BootSection : VendorSection;
    if (ini.Keys.Any(k => k.StartsWith(other + ".", StringComparison.OrdinalIgnoreCase)))
      throw new NotSupportedException($"metadata.ini describes a {(img.IsVendor ? "boot" : "vendor_boot")} image; this is a {(img.IsVendor ? "vendor_boot" : "boot")} image.");

    foreach (var (qualified, value) in ini) {
      if (current.TryGetValue(qualified, out var existing) && existing == value) continue;
      var dot = qualified.IndexOf('.');
      if (dot < 0) continue;
      var sec = qualified[..dot];
      var key = qualified[(dot + 1)..];
      if (Informational.Contains(key)) continue;
      if (sec.Equals(section, StringComparison.OrdinalIgnoreCase)) ApplyHeaderKey(img, key, value);
      else if (img.IsVendor && sec.StartsWith("vendor_ramdisk", StringComparison.OrdinalIgnoreCase)
               && int.TryParse(sec.AsSpan("vendor_ramdisk".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
               && index >= 0 && index < img.VendorRamdisks.Count)
        ApplyRamdiskKey(img.VendorRamdisks[index], key, value);
    }
  }

  private static void ApplyHeaderKey(AndroidBootImage img, string key, string value) {
    switch (key.ToLowerInvariant()) {
      case "header_version":
        throw new NotSupportedException("metadata.ini: the header version of an existing image cannot be changed; create a new image instead.");
      case "page_size":
        if (!img.IsVendor && img.HeaderVersion >= 3) return;
        var page = (uint)Number(key, value, uint.MaxValue);
        if (!AndroidBootImage.IsPowerOfTwoPage(page)) throw new ArgumentException($"metadata.ini: page_size {page} is not a power of two in 2048..262144.");
        img.PageSize = page;
        return;
      case "kernel_load_address": img.KernelAddress = (uint)Number(key, value, uint.MaxValue); return;
      case "ramdisk_load_address": img.RamdiskAddress = (uint)Number(key, value, uint.MaxValue); return;
      case "second_load_address": img.SecondAddress = (uint)Number(key, value, uint.MaxValue); return;
      case "tags_load_address": img.TagsAddress = (uint)Number(key, value, uint.MaxValue); return;
      case "dtb_load_address": img.DtbAddress = Number(key, value, ulong.MaxValue); return;
      case "header_size": img.HeaderSize = (uint)Number(key, value, uint.MaxValue); return;
      case "os_version":
        if (!AndroidBootImage.TryParseOsVersion(value, out var os)) throw new ArgumentException($"metadata.ini: os_version '{value}' is not a.b.c with each part below 128.");
        img.OsVersionPatchLevel = (os << 11) | (img.OsVersionPatchLevel & 0x7FF);
        return;
      case "os_patch_level":
        if (!AndroidBootImage.TryParseOsPatchLevel(value, out var level)) throw new ArgumentException($"metadata.ini: os_patch_level '{value}' is not YYYY-MM.");
        img.OsVersionPatchLevel = (img.OsVersionPatchLevel & ~0x7FFu) | level;
        return;
      case "board": img.Board = Field(key, value, AndroidBootImage.BootNameSize); return;
      case "cmdline":
        if (!img.IsVendor && img.HeaderVersion < 3) {
          // mkbootimg spills what does not fit the 512-byte field into extra_cmdline.
          var bytes = Encoding.Latin1.GetBytes(value);
          if (bytes.Length > AndroidBootImage.BootArgsSize - 1) {
            img.Cmdline = Field(key, Encoding.Latin1.GetString(bytes, 0, AndroidBootImage.BootArgsSize - 1), AndroidBootImage.BootArgsSize);
            img.ExtraCmdline = Field(key, Encoding.Latin1.GetString(bytes, AndroidBootImage.BootArgsSize - 1, bytes.Length - (AndroidBootImage.BootArgsSize - 1)), AndroidBootImage.BootExtraArgsSize);
          } else
            img.Cmdline = Field(key, value, AndroidBootImage.BootArgsSize);
        } else
          img.Cmdline = Field(key, value, img.Cmdline.Length, terminated: img.IsVendor);
        return;
      case "extra_cmdline": img.ExtraCmdline = Field(key, value, AndroidBootImage.BootExtraArgsSize); return;
      default:
        throw new ArgumentException($"metadata.ini: '{key}' is not a field of this image's header.");
    }
  }

  private static void ApplyRamdiskKey(VendorRamdisk r, string key, string value) {
    switch (key.ToLowerInvariant()) {
      case "type": r.Type = (uint)Number(key, value, uint.MaxValue); return;
      case "name": r.Name = Field(key, value, AndroidBootImage.VendorRamdiskNameSize); return;
      case "board_id":
        var words = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > AndroidBootImage.BoardIdWords) throw new ArgumentException($"metadata.ini: board_id has more than {AndroidBootImage.BoardIdWords} words.");
        var id = new uint[AndroidBootImage.BoardIdWords];
        for (var i = 0; i < words.Length; ++i) id[i] = (uint)Number(key, words[i], uint.MaxValue);
        r.BoardId = id;
        return;
      default:
        throw new ArgumentException($"metadata.ini: '{key}' is not a field of a vendor ramdisk table entry.");
    }
  }

  private static ulong Number(string key, string value, ulong max) {
    if (!FirmwareArchive.TryParseNumber(value, out var n) || n > max)
      throw new ArgumentException($"metadata.ini: {key} '{value}' is not a number in range.");
    return n;
  }

  /// <summary>A NUL-padded fixed-size field. mkbootimg requires room for the terminator, except in the v3+ 1536-byte boot command line.</summary>
  private static byte[] Field(string key, string value, int size, bool terminated = true) {
    var bytes = Encoding.Latin1.GetBytes(value);
    if (bytes.Length > size - (terminated ? 1 : 0))
      throw new ArgumentException($"metadata.ini: {key} is {bytes.Length} bytes; the field holds {size - (terminated ? 1 : 0)}.");
    var field = new byte[size];
    bytes.CopyTo(field, 0);
    return field;
  }

  private static string Dec(uint value) => value.ToString(CultureInfo.InvariantCulture);
  private static string Bool(bool value) => value ? "true" : "false";
}
