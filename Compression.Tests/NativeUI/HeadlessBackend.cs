using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Backends;
using Hawkynt.NativeForms.Drawing;

namespace Compression.Tests.NativeUI;

/// <summary>Answers every call with default. Peers exist so construction does not fault.</summary>
public class StubPeer : DispatchProxy {
  protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
    var returnType = targetMethod?.ReturnType;
    return returnType is null || returnType == typeof(void) || !returnType.IsValueType
      ? null
      : Activator.CreateInstance(returnType);
  }
}

/// <summary>An image that keeps its pixels, so a blit can be replayed rather than merely counted.</summary>
internal sealed class PixelImage(int width, int height, int[] argb) : IImage {
  public int Width => width;
  public int Height => height;
  public int[] Argb => argb;
  public IImage DisabledImage => this;
  public void Dispose() { }
}

/// <summary>
/// A platform backend with no platform behind it, so the shell can be built, shown and driven in a
/// test. Images are real — they carry their pixels — and text measures deterministically; anything
/// that would need a display is state and nothing more.
/// </summary>
internal sealed class HeadlessBackend : IPlatformBackend {
  /// <summary>
  /// Stands in for the message loop. A form cannot be shown and a popup cannot be opened outside a
  /// running loop, so whatever a test wants to do with a live window goes here.
  /// </summary>
  public static Action? WhileRunning { get; set; }

  /// <summary>Makes this the active backend, replacing whatever was registered before.</summary>
  public static void Install() {
    BackendRegistry.Clear();
    BackendRegistry.Register(new HeadlessBackend());
    RecordingPopup.All.Clear();
    RecordingWindow.All.Clear();
  }

  public string Name => "Headless";
  public bool IsSupported => true;
  public bool ButtonRendersImageWithText => true;
  public ITheme Theme => DefaultTheme.Instance;
  public event EventHandler? ThemeChanged { add { } remove { } }

  public IImage CreateImage(int width, int height, ReadOnlySpan<int> argb)
    => new PixelImage(width, height, argb.ToArray());

  /// <summary>
  /// A fixed advance per character. Layout that depends on measurement stays reproducible, which is
  /// what a rendering assertion needs; it is not trying to match any real font.
  /// </summary>
  public Size MeasureText(string text, Font font) {
    if (string.IsNullOrEmpty(text)) return new(0, (int)Math.Ceiling(font.SizeInPoints * 1.4));

    var lines = text.Split('\n');
    return new(
      (int)Math.Ceiling(lines.Max(l => l.Length) * font.SizeInPoints * 0.6),
      (int)Math.Ceiling(lines.Length * font.SizeInPoints * 1.4));
  }

  public double GetDpiScale() => 1.0;
  public Size GetScreenSize() => new(1920, 1080);

  public ICanvasPeer CreateCanvas() => new RecordingControlPeer();
  public IPopupPeer CreatePopup(IWindowPeer? owner) => new RecordingPopup();
  public IWindowPeer CreateWindow() => new RecordingWindow();

  // The widget peers only have to exist for the shell to build. Nothing is driven through them, so
  // rather than a file of empty overrides each is a proxy that answers every call with default.
  public IButtonPeer CreateButton() => Stub<IButtonPeer>();
  public ICheckBoxPeer CreateCheckBox() => Stub<ICheckBoxPeer>();
  public IComboBoxPeer CreateComboBox() => Stub<IComboBoxPeer>();
  public IGroupBoxPeer CreateGroupBox() => Stub<IGroupBoxPeer>();
  public ILabelPeer CreateLabel() => Stub<ILabelPeer>();
  public ILinkLabelPeer CreateLinkLabel() => Stub<ILinkLabelPeer>();
  public IListBoxPeer CreateListBox() => Stub<IListBoxPeer>();
  public INotifyIconPeer CreateNotifyIcon() => Stub<INotifyIconPeer>();
  public IProgressBarPeer CreateProgressBar() => Stub<IProgressBarPeer>();
  public IRadioButtonPeer CreateRadioButton() => Stub<IRadioButtonPeer>();
  public IRichTextBoxPeer CreateRichTextBox() => Stub<IRichTextBoxPeer>();
  public IScrollBarPeer CreateScrollBar(bool vertical) => Stub<IScrollBarPeer>();
  public ITextBoxPeer CreateTextBox() => Stub<ITextBoxPeer>();
  public ITimerPeer CreateTimer() => Stub<ITimerPeer>();
  public ITrackBarPeer CreateTrackBar(bool vertical) => Stub<ITrackBarPeer>();

  private static T Stub<T>() where T : class => DispatchProxy.Create<T, StubPeer>();

  public string GetClipboardText() => "";
  public void SetClipboardText(string text) { }
  public void Post(Action action) => action();
  public void Quit() { }
  public void Run(IWindowPeer mainWindow) => WhileRunning?.Invoke();
  public Color SampleScreenPixel(Point screen) => Color.Black;
  public Color? ShowColorDialog(Color color) => null;
  public string[] ShowFileDialog(in FileDialogOptions options) => [];
  public Font? ShowFontDialog(Font font) => null;

  public DialogResult ShowMessageBox(
    string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, IWindowPeer? owner = null)
    => DialogResult.OK;
}
