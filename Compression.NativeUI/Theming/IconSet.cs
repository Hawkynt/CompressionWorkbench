using Hawkynt.NativeForms;
using static Compression.NativeUI.Theming.IconGeometry;
using static Compression.NativeUI.Theming.IconShapeKind;

namespace Compression.NativeUI.Theming;

/// <summary>Stable names for the shell's icons, used as <see cref="ImageList"/> keys.</summary>
internal static class IconKeys {
  public const string Folder = "Folder";
  public const string File = "File";
  public const string LockedFile = "LockedFile";
  public const string UpArrow = "UpArrow";
  public const string Open = "Open";
  public const string Create = "Create";
  public const string Add = "Add";
  public const string Extract = "Extract";
  public const string ExtractSelected = "ExtractSelected";
  public const string Test = "Test";
  public const string ViewText = "ViewText";
  public const string ViewHex = "ViewHex";
  public const string ViewImage = "ViewImage";
  public const string Properties = "Properties";
  public const string Analyze = "Analyze";
  public const string NavigateUp = "NavigateUp";
  public const string Exit = "Exit";
  public const string About = "About";
  public const string Save = "Save";
  public const string Preview = "Preview";
  public const string Browse = "Browse";
  public const string Remove = "Remove";
  public const string Defragment = "Defragment";
}

/// <summary>
/// The shell's icon artwork, transcribed from the WPF resource dictionary the previous frontend
/// shipped so both look the same. Every entry is the same layer stack, in the same 16x16 space,
/// with the same colours; only the renderer changed.
/// </summary>
internal static class IconSet {
  // Palette, named as the resource dictionary used them.
  private const uint FolderBack = 0xFFD4A017;
  private const uint FolderFront = 0xFFFFC846;
  private const uint FolderTab = 0xFFE8B830;
  private const uint PageBody = 0xFFF0F2F5;
  private const uint PageEdge = 0xFF9EAAB8;
  private const uint PageFold = 0xFFD0D8E0;
  private const uint PageText = 0xFFBCC4CC;
  private const uint Blue = 0xFF4488CC;
  private const uint LightBlue = 0xFF66AADD;
  private const uint Slate = 0xFF6688AA;
  private const uint DarkSlate = 0xFF556688;
  private const uint Green = 0xFF44AA44;
  private const uint Red = 0xFFCC4444;
  private const uint White = 0xFFFFFFFF;

  private static IconShape Fill(uint color, params (double X, double Y)[][] polygons) => new(IconShapeKind.Fill, polygons, color);
  private static IconShape Outline(uint color, double thickness, params (double X, double Y)[][] polygons) => new(StrokeClosed, polygons, color, thickness);
  private static IconShape Line(uint color, double thickness, params (double X, double Y)[][] polygons) => new(Stroke, polygons, color, thickness);

  /// <summary>The page silhouette shared by the file, locked-file and create icons.</summary>
  private static (double X, double Y)[] Page => Poly(3, 1, 3, 15, 13, 15, 13, 5, 9, 1);

  private static (double X, double Y)[] PageCorner => Poly(9, 1, 9, 5, 13, 5);

  /// <summary>The open-folder silhouette shared by the open and browse icons.</summary>
  private static IconShape[] OpenFolder => [
    Fill(FolderBack, Poly(1, 4, 1, 13, 4, 13, 6, 7, 13, 7, 13, 6, 7, 6, 5, 4)),
    Fill(FolderFront, Poly(4, 13, 6, 7, 15, 7, 13, 13)),
  ];

  /// <summary>The download tray shared by the two extract icons.</summary>
  private static (double X, double Y)[] Tray => Poly(2, 13, 2, 15, 14, 15, 14, 13, 12, 13, 12, 14, 4, 14, 4, 13);

  private static readonly Dictionary<string, IconShape[]> Definitions = new(StringComparer.Ordinal) {
    [IconKeys.Folder] = [
      Fill(FolderBack, Poly(1, 4, 1, 14, 15, 14, 15, 6, 8, 6, 6, 4)),
      Fill(FolderFront, Rect(1, 7, 15, 14)),
      Fill(FolderTab, Poly(1, 4, 6, 4, 8, 6, 1, 6)),
    ],

    [IconKeys.File] = [
      Fill(PageBody, Page),
      Outline(PageEdge, 0.5, Page),
      Fill(PageFold, PageCorner),
      Fill(PageText, Rect(5, 7, 11, 7.7), Rect(5, 9, 11, 9.7), Rect(5, 11, 9, 11.7)),
    ],

    [IconKeys.LockedFile] = [
      Fill(0xFFF5EDED, Page),
      Outline(0xFFBB7777, 0.5, Page),
      Fill(0xFFE0C8C8, PageCorner),
      Fill(Red, Rect(6, 9, 10, 13)),
      // Shackle: up from the body, over the top, back down.
      Line(Red, 1.0, Join([(6.8, 9.0)], Arc(8, 7.5, 1.2, 180, 360), [(9.2, 9.0)])),
    ],

    [IconKeys.UpArrow] = [
      Fill(Blue, Poly(8, 2, 3, 9, 6, 9, 6, 14, 10, 14, 10, 9, 13, 9)),
    ],

    [IconKeys.Open] = OpenFolder,

    [IconKeys.Create] = [
      Fill(PageBody, Page),
      Outline(PageEdge, 0.5, Page),
      Fill(PageFold, PageCorner),
      Fill(Green, Poly(7.5, 7, 8.5, 7, 8.5, 9.5, 11, 9.5, 11, 10.5, 8.5, 10.5, 8.5, 13, 7.5, 13, 7.5, 10.5, 5, 10.5, 5, 9.5, 7.5, 9.5)),
    ],

    [IconKeys.Add] = [
      Fill(Blue, Poly(7, 3, 9, 3, 9, 7, 13, 7, 13, 9, 9, 9, 9, 13, 7, 13, 7, 9, 3, 9, 3, 7, 7, 7)),
    ],

    [IconKeys.Extract] = [
      Fill(Blue, Poly(7, 2, 9, 2, 9, 9, 12, 9, 8, 14, 4, 9, 7, 9)),
      Fill(Blue, Tray),
    ],

    [IconKeys.ExtractSelected] = [
      Fill(Blue, Poly(7, 2, 9, 2, 9, 7, 11, 7, 8, 11, 5, 7, 7, 7)),
      Fill(Blue, Tray),
      Fill(Green, Poly(11, 1, 13, 3, 15, 1, 14, 0, 13, 1, 12, 0)),
    ],

    [IconKeys.Test] = [
      Fill(Green, Poly(2, 8, 4, 6, 7, 9, 13, 3, 15, 5, 7, 13)),
    ],

    [IconKeys.ViewText] = [
      Fill(Slate, Rect(2, 3, 14, 3.8), Rect(2, 5.5, 14, 6.3), Rect(2, 8, 14, 8.8), Rect(2, 10.5, 10, 11.3)),
    ],

    [IconKeys.ViewHex] = [
      Fill(0xFF667799, Rect(1, 2, 15, 14)),
      Outline(DarkSlate, 0.5, Rect(1, 2, 15, 14)),
      Fill(0xFF334466, Rect(2, 4, 5, 4.7), Rect(2, 6.5, 5, 7.2), Rect(2, 9, 5, 9.7), Rect(2, 11.5, 5, 12.2)),
      Fill(DarkSlate, Rect(6, 4, 14, 4.7), Rect(6, 6.5, 14, 7.2), Rect(6, 9, 14, 9.7), Rect(6, 11.5, 14, 12.2)),
    ],

    [IconKeys.Properties] = [
      Fill(Slate, Poly(
        7, 1, 9, 1, 9.5, 3, 11, 3.5, 13, 2, 14, 3, 12.5, 5, 13, 6.5, 15, 7, 15, 9, 13, 9.5,
        12.5, 11, 14, 13, 13, 14, 11, 12.5, 9.5, 13, 9, 15, 7, 15, 6.5, 13, 5, 12.5, 3, 14,
        2, 13, 3.5, 11, 3, 9.5, 1, 9, 1, 7, 3, 6.5, 3.5, 5, 2, 3, 3, 2, 5, 3.5, 6.5, 3)),
      Fill(White, Circle(8, 8, 2)),
    ],

    [IconKeys.Analyze] = [
      Outline(Blue, 1.5, Circle(7, 7, 5)),
      Fill(Blue, Poly(10.5, 11, 14, 14.5, 15, 13.5, 11.5, 10)),
      Fill(LightBlue, Rect(4, 6, 5, 9), Rect(6, 4, 7, 9), Rect(8, 7, 9, 9)),
    ],

    [IconKeys.NavigateUp] = [
      Fill(Blue, Poly(8, 2, 14, 8, 11, 8, 11, 14, 5, 14, 5, 8, 2, 8)),
    ],

    [IconKeys.Exit] = [
      Fill(Red, Poly(2, 2, 9, 2, 9, 5, 8, 5, 8, 3, 3, 3, 3, 13, 8, 13, 8, 11, 9, 11, 9, 14, 2, 14)),
      Fill(Red, Poly(7, 7, 13, 7, 13, 5, 16, 8, 13, 11, 13, 9, 7, 9)),
    ],

    [IconKeys.About] = [
      Fill(Blue, Circle(8, 8, 7)),
      Fill(White, Rect(7.2, 5, 8.8, 6.5), Rect(7.2, 7.5, 8.8, 12)),
    ],

    [IconKeys.Save] = [
      Fill(Blue, Poly(2, 1, 2, 15, 14, 15, 14, 4, 11, 1)),
      Fill(White, Rect(4, 1, 10, 5), Rect(4, 9, 12, 14)),
      Fill(Blue, Rect(8, 2, 9, 4)),
    ],

    [IconKeys.ViewImage] = [
      Fill(0xFFE8F2FB, Rect(1, 2, 15, 14)),
      Outline(DarkSlate, 0.7, Rect(1, 2, 15, 14)),
      Fill(0xFFFFC857, Circle(4.5, 6.195, 1.195)),
      Fill(Slate, Poly(2, 12.5, 6, 8, 9, 11, 12, 7, 14, 12.5)),
    ],

    [IconKeys.Preview] = [
      Fill(Slate, Join(
        Quad((1, 8), (8, 2), (15, 8)),
        Quad((15, 8), (8, 14), (1, 8)))),
      Fill(White, Circle(8, 8, 3)),
      Fill(0xFF334466, Circle(8, 8, 1.5)),
    ],

    [IconKeys.Browse] = [
      .. OpenFolder,
      Line(Blue, 1.0, Poly(11, 1, 13, 3, 15, 1)),
    ],

    [IconKeys.Remove] = [
      Fill(Red, Rect(2, 4, 14, 5.5)),
      Fill(Red, Rect(6, 2, 10, 3.5)),
      Fill(0xFFE05555, Poly(3, 6, 13, 6, 12, 15, 4, 15)),
      Fill(0xFFAA3333,
        Poly(5.5, 7, 6.2, 7, 6, 14, 5.3, 14),
        Poly(7.7, 7, 8.3, 7, 8.3, 14, 7.7, 14),
        Poly(9.8, 7, 10.5, 7, 10.7, 14, 10, 14)),
    ],

    [IconKeys.Defragment] = [
      // Row 1: scattered, before the pass.
      Fill(0xFFE64A19, Rect(1, 1, 4, 4)),
      Fill(0xFFE0E0E0, Rect(5, 1, 7, 4), Rect(11, 1, 13, 4)),
      Fill(0xFF42A5F5, Rect(8, 1, 10, 4)),
      Fill(0xFF66BB6A, Rect(14, 1, 15, 4)),
      Fill(0xFF555555, Poly(7, 6, 9, 6, 9, 9, 11, 9, 8, 12, 5, 9, 7, 9)),
      // Row 3: packed, after.
      Fill(0xFFE64A19, Rect(1, 12, 4, 15)),
      Fill(0xFF42A5F5, Rect(4, 12, 7, 15)),
      Fill(0xFF66BB6A, Rect(7, 12, 10, 15)),
      Fill(0xFFE0E0E0, Rect(10, 12, 15, 15)),
    ],
  };

  /// <summary>Every icon key this set can render.</summary>
  public static IReadOnlyCollection<string> Keys => Definitions.Keys;

  /// <summary>Rasterizes one icon at <paramref name="size"/> pixels square.</summary>
  public static int[] Render(string key, int size) {
    if (!Definitions.TryGetValue(key, out var shapes))
      throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown icon key.");
    return VectorIconRenderer.Render(shapes, size);
  }

  /// <summary>Builds an <see cref="ImageList"/> holding every icon, keyed by <see cref="IconKeys"/>.</summary>
  public static ImageList CreateImageList(int size) {
    var list = new ImageList(size);
    foreach (var key in Definitions.Keys)
      list.Add(key, Render(key, size));
    return list;
  }
}
