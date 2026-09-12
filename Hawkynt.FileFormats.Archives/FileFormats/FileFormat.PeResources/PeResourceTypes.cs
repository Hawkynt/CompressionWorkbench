#pragma warning disable CS1591
namespace FileFormat.PeResources;

/// <summary>
/// Win32 <c>RT_*</c> resource type ids and their conventional file extensions.
/// Matches <c>winuser.h</c>.
/// </summary>
internal static class PeResourceTypes {
  public const ushort RtCursor = 1;
  public const ushort RtBitmap = 2;
  public const ushort RtIcon = 3;
  public const ushort RtMenu = 4;
  public const ushort RtDialog = 5;
  public const ushort RtString = 6;
  public const ushort RtFontDir = 7;
  public const ushort RtFont = 8;
  public const ushort RtAccelerator = 9;
  public const ushort RtRcData = 10;
  public const ushort RtMessageTable = 11;
  public const ushort RtGroupCursor = 12; // RT_CURSOR + 11
  public const ushort RtGroupIcon = 14;   // RT_ICON + 11
  public const ushort RtVersion = 16;
  public const ushort RtDlgInclude = 17;
  public const ushort RtPlugPlay = 19;
  public const ushort RtVxd = 20;
  public const ushort RtAniCursor = 21;
  public const ushort RtAniIcon = 22;
  public const ushort RtHtml = 23;
  public const ushort RtManifest = 24;

  /// <summary>
  /// Maps a numeric resource type to a short directory name and default extension.
  /// Unknown types fall back to <c>TYPE_&lt;id&gt;</c> + <c>.bin</c>.
  /// </summary>
  public static (string Dir, string Extension) Classify(ushort typeId) => typeId switch {
    RtCursor        => ("CURSOR",        ".cur"),
    RtBitmap        => ("BITMAP",        ".bmp"),
    RtIcon          => ("ICON",          ".ico"),     // raw RT_ICON member; assembled form comes from RT_GROUP_ICON
    RtMenu          => ("MENU",          ".bin"),
    RtDialog        => ("DIALOG",        ".bin"),
    RtString        => ("STRING",        ".txt"),
    RtFontDir       => ("FONTDIR",       ".bin"),
    RtFont          => ("FONT",          ".ttf"),
    RtAccelerator   => ("ACCELERATOR",   ".bin"),
    RtRcData        => ("RCDATA",        ".bin"),
    RtMessageTable  => ("MESSAGETABLE",  ".bin"),
    RtGroupCursor   => ("GROUP_CURSOR",  ".cur"),
    RtGroupIcon     => ("GROUP_ICON",    ".ico"),
    RtVersion       => ("VERSION",       ".rcv"),
    RtDlgInclude    => ("DLGINCLUDE",    ".txt"),
    RtPlugPlay      => ("PLUGPLAY",      ".bin"),
    RtVxd           => ("VXD",           ".bin"),
    RtAniCursor     => ("ANICURSOR",     ".ani"),
    RtAniIcon       => ("ANIICON",       ".ani"),
    RtHtml          => ("HTML",          ".html"),
    RtManifest      => ("MANIFEST",      ".manifest"),
    _               => ($"TYPE_{typeId}", ".bin"),
  };
}
