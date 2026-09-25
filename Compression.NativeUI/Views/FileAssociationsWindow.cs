using System.Diagnostics;
using System.Drawing;
using System.Security.Principal;
using Compression.Lib;
using Compression.Registry;
using Hawkynt.NativeForms;
using Microsoft.Win32;

namespace Compression.NativeUI.Views;

/// <summary>Data item for the extension list.</summary>
internal sealed class ExtensionItem {
  public required string Extension { get; init; }
  public required string FormatName { get; init; }
  public required string Description { get; init; }
  public bool IsSelected { get; set; }
}

/// <summary>
/// Registers the shell as the handler for archive extensions, plus the Explorer context-menu verbs.
/// <para>
/// The registrations are Windows registry keys. On any other platform the window still lists what
/// the build recognises, but the apply and remove actions are disabled and say why rather than
/// pretending to write something.
/// </para>
/// </summary>
internal sealed class FileAssociationsWindow : Form {
  private const string AppName = "CompressionWorkbench";

  /// <summary>Commonly used archive extensions, for the quick-select button.</summary>
  private static readonly HashSet<string> CommonExtensions = [
    ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".zst", ".tar",
    ".lz4", ".cab", ".lzh", ".arj", ".lzma", ".br", ".snappy",
    ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lz4",
    ".tgz", ".tbz2", ".txz",
  ];

  private readonly GroupBox _scopeBox = new() { Text = "Registration Scope" };
  private readonly RadioButton _currentUser = new() { Text = "Current user only (HKCU)", Checked = true };
  private readonly RadioButton _allUsers = new() { Text = "All users (HKLM — requires administrator)" };

  private readonly Button _selectAll = new() { Text = "All" };
  private readonly Button _selectNone = new() { Text = "None" };
  private readonly Button _selectCommon = new() { Text = "Common" };
  private readonly Button _selectInvert = new() { Text = "Invert" };
  private readonly Label _selectionCount = new() { ForeColor = Color.Gray };

  private readonly ListView _extensions = new() {
    View = ListViewView.Details,
    CheckBoxes = true,
    FullRowSelect = true,
    MultiSelect = true,
  };

  private readonly GroupBox _contextBox = new() { Text = "Context Menu Entries" };
  private readonly CheckBox _openWith = new() { Text = "Open with CompressionWorkbench", Checked = true };
  private readonly CheckBox _extractHere = new() { Text = "Extract here", Checked = true };
  private readonly CheckBox _addToArchive = new() { Text = "Add to ZIP/7z archive (on files and folders)", Checked = true };

  private readonly Button _apply = new() { Text = "Apply" };
  private readonly Button _removeAll = new() { Text = "Remove All" };
  private readonly Button _close = new() { Text = "Close", DialogResult = DialogResult.Cancel };
  private readonly Label _platformNotice = new() { ForeColor = Color.FromArgb(0xB0, 0x60, 0x00), Visible = false };

  private List<ExtensionItem> _items = [];

  public FileAssociationsWindow() {
    this.Text = "File Associations";
    this.ClientSize = new(600, 550);
    this.MinimumSize = new(450, 400);
    this.StartPosition = FormStartPosition.CenterParent;

    this.BuildLayout();
    this.LoadExtensions();

    if (!OperatingSystem.IsWindows()) {
      this._apply.Enabled = false;
      this._removeAll.Enabled = false;
      this._scopeBox.Enabled = false;
      this._contextBox.Enabled = false;
      this._platformNotice.Text = "File associations are registered in the Windows registry; this build is not running on Windows.";
      this._platformNotice.Visible = true;
    }

    this.CancelButton = this._close;
    this.Resize += (_, _) => this.LayoutChildren();
    this.LayoutChildren();
  }

  private readonly ToolTip _toolTips = new();

  private void BuildLayout() {
    this._toolTips.SetToolTip(this._selectCommon,
      "Select commonly used formats: zip, 7z, rar, gz, bz2, xz, zst, tar, lz4, cab");

    this._scopeBox.Controls.AddRange(this._currentUser, this._allUsers);
    this._currentUser.Bounds = new(10, 22, 300, 20);
    this._allUsers.Bounds = new(10, 46, 360, 20);

    foreach (var (button, action) in new (Button, Func<ExtensionItem, bool>)[] {
      (this._selectAll, _ => true),
      (this._selectNone, _ => false),
      (this._selectCommon, i => CommonExtensions.Contains(i.Extension)),
      (this._selectInvert, i => !i.IsSelected),
    }) {
      var captured = action;
      button.Click += (_, _) => this.SetSelection(captured);
    }

    this._extensions.Columns.AddRange([
      new ColumnHeader("Extension", 90),
      new ColumnHeader("Format", 100),
      new ColumnHeader("Description", 300),
    ]);
    this._extensions.ItemChecked += (_, e) => {
      if (e.Item.Tag is ExtensionItem item) item.IsSelected = e.Item.Checked;
      this.UpdateSelectionCount();
    };

    this._contextBox.Controls.AddRange(this._openWith, this._extractHere, this._addToArchive);
    this._openWith.Bounds = new(10, 22, 320, 20);
    this._extractHere.Bounds = new(10, 44, 320, 20);
    this._addToArchive.Bounds = new(10, 66, 400, 20);

    this._apply.Click += (_, _) => this.OnApply();
    this._removeAll.Click += (_, _) => this.OnRemoveAll();
    this._close.Click += (_, _) => this.Close();

    this.Controls.AddRange(
      this._scopeBox,
      this._selectAll, this._selectNone, this._selectCommon, this._selectInvert, this._selectionCount,
      this._extensions,
      this._contextBox, this._platformNotice,
      this._apply, this._removeAll, this._close);
  }

  private void LayoutChildren() {
    const int Margin = 12;
    var width = this.ClientSize.Width - 2 * Margin;

    this._scopeBox.Bounds = new(Margin, Margin, width, 74);

    var y = Margin + 82;
    var x = Margin;
    foreach (var button in new[] { this._selectAll, this._selectNone, this._selectCommon, this._selectInvert }) {
      button.Bounds = new(x, y, 70, 24);
      x += 74;
    }
    this._selectionCount.Bounds = new(x + 8, y + 4, 240, 18);

    y += 30;
    var listHeight = Math.Max(80, this.ClientSize.Height - y - 190);
    this._extensions.Bounds = new(Margin, y, width, listHeight);

    y += listHeight + 8;
    this._contextBox.Bounds = new(Margin, y, width, 94);

    y += 100;
    this._platformNotice.Bounds = new(Margin, y, width, 18);

    var buttonY = this.ClientSize.Height - Margin - 28;
    this._close.Bounds = new(this.ClientSize.Width - Margin - 90, buttonY, 90, 28);
    this._removeAll.Bounds = new(this.ClientSize.Width - Margin - 188, buttonY, 90, 28);
    this._apply.Bounds = new(this.ClientSize.Width - Margin - 286, buttonY, 90, 28);
  }

  private void LoadExtensions() {
    FormatRegistration.EnsureInitialized();

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var items = new List<ExtensionItem>();

    foreach (var desc in FormatRegistry.All.OrderBy(d => d.DisplayName)) {
      // Compound extensions first — they take priority during detection.
      foreach (var ext in desc.CompoundExtensions.Concat(desc.Extensions)) {
        if (!seen.Add(ext)) continue;
        items.Add(new() {
          Extension = ext,
          FormatName = desc.DisplayName,
          Description = desc.Description,
        });
      }
    }

    this._items = [.. items.OrderBy(i => i.Extension, StringComparer.OrdinalIgnoreCase)];
    this.RebuildList();
  }

  private void RebuildList() {
    this._extensions.Items.Clear();
    foreach (var item in this._items)
      this._extensions.Items.Add(new ListViewItem(item.Extension, [item.FormatName, item.Description]) {
        Tag = item,
        Checked = item.IsSelected,
      });

    this.UpdateSelectionCount();
  }

  private void UpdateSelectionCount()
    => this._selectionCount.Text = $"{this._items.Count(i => i.IsSelected)} of {this._items.Count} extensions selected";

  private void SetSelection(Func<ExtensionItem, bool> predicate) {
    foreach (var item in this._items) item.IsSelected = predicate(item);

    for (var i = 0; i < this._extensions.Items.Count; ++i)
      this._extensions.Items[i].Checked = this._items[i].IsSelected;

    this.UpdateSelectionCount();
  }

  private void OnApply() {
    var selected = this._items.Where(i => i.IsSelected).Select(i => i.Extension).ToArray();
    if (selected.Length == 0) {
      MessageBox.Show(this, "No extensions selected.", "File Associations", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      return;
    }

    if (!OperatingSystem.IsWindows()) return;

    var allUsers = this._allUsers.Checked;
    if (allUsers && !IsRunningAsAdmin()) {
      var answer = MessageBox.Show(this,
        "Registering for all users requires administrator privileges.\n\nRelaunch as administrator?",
        "Elevation Required", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
      if (answer == DialogResult.Yes) RelaunchAsAdmin();
      return;
    }

    try {
      var exePath = GetExePath();
      var rootKey = allUsers ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser;

      if (this._openWith.Checked || this._extractHere.Checked)
        this.RegisterExtensions(rootKey, selected, exePath);

      if (this._addToArchive.Checked)
        RegisterAddToArchive(rootKey, exePath);

      MessageBox.Show(this, $"Registered {selected.Length} file associations successfully.",
        "File Associations", MessageBoxButtons.OK, MessageBoxIcon.Information);
    } catch (Exception ex) {
      MessageBox.Show(this, $"Failed to register: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
  }

  private void OnRemoveAll() {
    var answer = MessageBox.Show(this,
      "Remove all CompressionWorkbench file associations and context menu entries?",
      "Remove Associations", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
    if (answer != DialogResult.Yes) return;

    if (!OperatingSystem.IsWindows()) return;

    var allUsers = this._allUsers.Checked;
    if (allUsers && !IsRunningAsAdmin()) {
      MessageBox.Show(this, "Removing all-users associations requires administrator privileges.",
        "Elevation Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      return;
    }

    try {
      var rootKey = allUsers ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser;
      UnregisterAll(rootKey, [.. this._items.Select(i => i.Extension)]);
      MessageBox.Show(this, "All associations removed.", "File Associations", MessageBoxButtons.OK, MessageBoxIcon.Information);
    } catch (Exception ex) {
      MessageBox.Show(this, $"Failed to remove: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
  }

  [System.Runtime.Versioning.SupportedOSPlatform("windows")]
  private void RegisterExtensions(RegistryKey rootKey, string[] extensions, string exePath) {
    foreach (var ext in extensions) {
      if (this._openWith.Checked) {
        using var key = rootKey.CreateSubKey($@"Software\Classes\{ext}\shell\{AppName}");
        if (key is null) continue;
        key.SetValue("", "Open with CompressionWorkbench");
        key.SetValue("Icon", $"\"{exePath}\",0");
        using var command = key.CreateSubKey("command");
        command?.SetValue("", $"\"{exePath}\" \"%1\"");
      }

      if (this._extractHere.Checked) {
        using var key = rootKey.CreateSubKey($@"Software\Classes\{ext}\shell\{AppName}.ExtractHere");
        if (key is null) continue;
        key.SetValue("", "Extract here (CWB)");
        key.SetValue("Icon", $"\"{exePath}\",0");
        using var command = key.CreateSubKey("command");
        command?.SetValue("", $"\"{exePath}\" {ShellVerbs.ExtractHere} \"%1\"");
      }
    }
  }

  [System.Runtime.Versioning.SupportedOSPlatform("windows")]
  private static void RegisterAddToArchive(RegistryKey rootKey, string exePath) {
    foreach (var fileClass in new[] { "Directory", "*" }) {
      using (var zipKey = rootKey.CreateSubKey($@"Software\Classes\{fileClass}\shell\{AppName}.AddToZip")) {
        if (zipKey is not null) {
          zipKey.SetValue("", "Add to ZIP archive (CWB)");
          zipKey.SetValue("Icon", $"\"{exePath}\",0");
          using var command = zipKey.CreateSubKey("command");
          command?.SetValue("", $"\"{exePath}\" {ShellVerbs.CreateZip} \"%1\"");
        }
      }

      using var sevenZipKey = rootKey.CreateSubKey($@"Software\Classes\{fileClass}\shell\{AppName}.AddTo7z");
      if (sevenZipKey is null) continue;
      sevenZipKey.SetValue("", "Add to 7z archive (CWB)");
      sevenZipKey.SetValue("Icon", $"\"{exePath}\",0");
      using var sevenZipCommand = sevenZipKey.CreateSubKey("command");
      sevenZipCommand?.SetValue("", $"\"{exePath}\" {ShellVerbs.Create7z} \"%1\"");
    }
  }

  [System.Runtime.Versioning.SupportedOSPlatform("windows")]
  private static void UnregisterAll(RegistryKey rootKey, string[] extensions) {
    foreach (var ext in extensions) {
      TryDeleteKey(rootKey, $@"Software\Classes\{ext}\shell\{AppName}");
      TryDeleteKey(rootKey, $@"Software\Classes\{ext}\shell\{AppName}.ExtractHere");
    }

    foreach (var fileClass in new[] { "Directory", "*" }) {
      TryDeleteKey(rootKey, $@"Software\Classes\{fileClass}\shell\{AppName}.AddToZip");
      TryDeleteKey(rootKey, $@"Software\Classes\{fileClass}\shell\{AppName}.AddTo7z");
    }
  }

  [System.Runtime.Versioning.SupportedOSPlatform("windows")]
  private static void TryDeleteKey(RegistryKey rootKey, string path) {
    try {
      rootKey.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    } catch {
      // A key another product owns, or one we lack rights to — neither is fatal here.
    }
  }

  private static string GetExePath() {
    var path = Environment.ProcessPath;
    if (!string.IsNullOrEmpty(path)) return path;
    return Process.GetCurrentProcess().MainModule?.FileName ?? "CompressionWorkbench.exe";
  }

  [System.Runtime.Versioning.SupportedOSPlatform("windows")]
  private static bool IsRunningAsAdmin() {
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
  }

  private static void RelaunchAsAdmin() {
    try {
      Process.Start(new ProcessStartInfo {
        FileName = GetExePath(),
        UseShellExecute = true,
        Verb = "runas",
      });
      Application.Exit();
    } catch {
      // The user dismissed the UAC prompt.
    }
  }
}
