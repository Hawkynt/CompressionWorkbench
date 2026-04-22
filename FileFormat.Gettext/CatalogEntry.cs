#pragma warning disable CS1591
namespace FileFormat.Gettext;

/// <summary>
/// One translatable entry in a gettext catalog.
/// <para>
/// <paramref name="Context"/> is null for keyless entries, the msgctxt string otherwise
/// (MO encodes context as <c>ctx '\x04' msgid</c>; PO uses a dedicated <c>msgctxt</c> line).
/// </para>
/// <para>
/// <paramref name="MsgIdPlural"/> / <paramref name="MsgStrPlural"/> populate when the
/// entry has plural forms; otherwise plural is null and the single translation is in
/// <paramref name="MsgStr"/>. The empty-msgid entry carries the catalog's metadata
/// header (Content-Type, Plural-Forms, …) in <paramref name="MsgStr"/>.
/// </para>
/// </summary>
public sealed record CatalogEntry(
  int Index,
  string? Context,
  string MsgId,
  string? MsgIdPlural,
  string MsgStr,
  IReadOnlyList<string>? MsgStrPlural
);
