using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Backends;
using Hawkynt.NativeForms.Drawing;

namespace Compression.Tests.NativeUI;

#pragma warning disable CS0067  // Peers declare the platform's whole event surface; tests raise only some.

/// <summary>
/// A control peer that records what it was told and can raise what the platform would raise.
/// <para>
/// A peer that only absorbs calls is enough to construct a window, but not to drive one: the
/// interesting wiring — a right-click reaching the control's context menu, a click landing on a
/// menu item — travels from the platform into the framework through these events, so a peer that
/// cannot raise them leaves exactly the paths under test unexercised.
/// </para>
/// </summary>
internal class RecordingControlPeer : IControlPeer, ICanvasPeer, IContainerPeer {
  public Rectangle Bounds { get; private set; }
  public bool Visible { get; private set; }
  public bool Enabled { get; private set; } = true;
  public string Text { get; private set; } = "";
  public int InvalidateCount { get; private set; }
  public int ChildCount { get; private set; }
  public string? LastToolTip { get; private set; }

  public event EventHandler<PaintEventArgs>? Paint;
  public event EventHandler<MouseEventArgs>? MouseDown;
  public event EventHandler<MouseEventArgs>? MouseUp;
  public event EventHandler<MouseEventArgs>? MouseMove;
  public event EventHandler<MouseEventArgs>? MouseWheel;
  public event EventHandler? MouseLeave;
  public event EventHandler<KeyEventArgs>? KeyDown;
  public event EventHandler<KeyEventArgs>? KeyUp;
  public event EventHandler<KeyPressEventArgs>? KeyPress;
  public event EventHandler<ContextMenuRequestedEventArgs>? ContextMenuRequested;
  public event EventHandler? GotFocus;
  public event EventHandler? LostFocus;
  public event EventHandler? PointerLeave;
  public event EventHandler<MouseEventArgs>? PointerMove;

  public void Invalidate(Rectangle bounds) => ++this.InvalidateCount;
  public void InvalidateAll() => ++this.InvalidateCount;
  public void SetFocusable(bool focusable) { }
  public void Focus() { }
  public Point PointToScreen(Point clientPoint) => new(this.Bounds.X + clientPoint.X, this.Bounds.Y + clientPoint.Y);
  public void SetAccessibleInfo(string? name, string? description, AccessibleRole role) { }
  public void SetBounds(Rectangle bounds) => this.Bounds = bounds;
  public void SetColors(Color foreColor, Color backColor) { }
  public void SetCursor(Cursor cursor) { }
  public void SetEnabled(bool enabled) => this.Enabled = enabled;
  public void SetFont(Font font) { }
  public void SetText(string text) => this.Text = text;
  public void SetVisible(bool visible) => this.Visible = visible;
  public void ShowToolTip(string? text) => this.LastToolTip = text;
  public void AddChild(IControlPeer child) => ++this.ChildCount;
  public void RemoveChild(IControlPeer child) => --this.ChildCount;
  public void Dispose() { }

  /// <summary>Right-click, as the platform reports it. Returns whether anything handled it.</summary>
  public bool RaiseContextMenuRequested(Point location) {
    if (this.ContextMenuRequested is not { } handler) return false;

    var args = new ContextMenuRequestedEventArgs(location);
    handler(this, args);
    return args.Handled;
  }

  public bool RaisePaint(IGraphics graphics, Rectangle bounds) {
    if (this.Paint is not { } handler) return false;

    handler(this, new(graphics, bounds));
    return true;
  }

  public bool RaiseClick(int x, int y) {
    if (this.MouseDown is null && this.MouseUp is null) return false;

    this.MouseDown?.Invoke(this, new(MouseButtons.Left, x, y, 0, KeyModifiers.None));
    this.MouseUp?.Invoke(this, new(MouseButtons.Left, x, y, 0, KeyModifiers.None));
    return true;
  }

  public bool RaiseMouseMove(int x, int y) {
    if (this.MouseMove is null) return false;

    this.MouseMove(this, new(MouseButtons.None, x, y, 0, KeyModifiers.None));
    return true;
  }

  public bool RaiseKeyDown(Keys keys) {
    if (this.KeyDown is not { } handler) return false;

    handler(this, new(keys, KeyModifiers.None));
    return true;
  }
}

/// <summary>
/// A popup that keeps its state. The context-menu check has to tell "shown and still shown" from
/// "shown, then quietly hidden again", which a peer that forgets cannot express.
/// </summary>
internal sealed class RecordingPopup : RecordingControlPeer, IPopupPeer {
  public static readonly List<RecordingPopup> All = [];

  public RecordingPopup() => All.Add(this);

  public int ShowCount { get; private set; }
  public int HideCount { get; private set; }
  public int RegrabCount { get; private set; }
  public Size LastSize { get; private set; }
  public bool Shown { get; private set; }

  public event EventHandler? Dismissed;
  public bool LightDismiss { get; set; }
  public Action<Point>? OutsidePointerMove { get; set; }
  public Func<Point, bool>? OutsidePress { get; set; }

  public void ShowAt(Point screenLocation, Size size) {
    this.Shown = true;
    this.LastSize = size;
    ++this.ShowCount;
  }

  public void Hide() {
    this.Shown = false;
    ++this.HideCount;
  }

  public void Resize(Size size) => this.LastSize = size;
  public void Regrab() => ++this.RegrabCount;
  public void ExpectGrabHandoff() { }
  public void SetParentPopup(IPopupPeer parent) { }

  /// <summary>Whether anything is listening for the platform's own dismissal.</summary>
  public bool HasDismissedHandler => this.Dismissed is not null;

  /// <summary>The dismissal the platform raises when the user clicks away.</summary>
  public void RaiseDismissed() => this.Dismissed?.Invoke(this, EventArgs.Empty);
}

/// <summary>A window that exists only as state, so a form can be shown without a display.</summary>
internal sealed class RecordingWindow : RecordingControlPeer, IWindowPeer {
  public static readonly List<RecordingWindow> All = [];

  public RecordingWindow() => All.Add(this);

  public bool IsShown { get; private set; }

  public event EventHandler<Rectangle>? BoundsChangedByUser;
  public event EventHandler? Closed;
  public event EventHandler<CancelEventArgs>? CloseRequested;
  public event EventHandler<FormWindowState>? WindowStateChanged;

  public void Show() => this.IsShown = true;
  public void Close() {
    this.IsShown = false;
    this.Closed?.Invoke(this, EventArgs.Empty);
  }

  public void RunModal(IWindowPeer? owner) => this.IsShown = true;
  public void SetBorderStyle(FormBorderStyle borderStyle) { }
  public void SetIcon(int width, int height, ReadOnlySpan<int> argb) { }
  public void SetMaximizeBox(bool visible) { }
  public void SetMinimizeBox(bool visible) { }
  public void SetOpacity(double opacity) { }
  public void SetQuitsOnClose(bool quits) { }
  public void SetSizeLimits(Size minimum, Size maximum) { }
  public void SetTopMost(bool topMost) { }
  public void SetWindowState(FormWindowState state) { }
}

#pragma warning restore CS0067
