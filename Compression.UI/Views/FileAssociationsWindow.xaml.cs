using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using Compression.Lib;
using Compression.Registry;
using Microsoft.Win32;

namespace Compression.UI.Views;

public partial class FileAssociationsWindow : Window {

  private const string AppName = "CompressionWorkbench";

  /// <summary>Commonly used archive extensions for quick-select.</summary>
  private static readonly HashSet<string> CommonExtensions = [
    ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".zst", ".tar",
    ".lz4", ".cab", ".lzh", ".arj", ".lzma", ".br", ".snappy",
    ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lz4",
    ".tgz", ".tbz2", ".txz"
  ];

  private List<ExtensionItem> _items = [];

  public FileAssociationsWindow() {
    InitializeComponent();
    LoadExtensions();
  }

  private void LoadExtensions() {
    FormatRegistration.EnsureInitialized();

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    _items = [];

    foreach (var desc in FormatRegistry.All.OrderBy(d => d.DisplayName)) {
      // Add compound extensions first (they take priority during detection).
      foreach (var ext in desc.CompoundExtensions) {
        if (!seen.Add(ext)) continue;
        _items.Add(new ExtensionItem {
          Extension = ext,
          FormatName = desc.DisplayName,
          Description = desc.Description,
          IsSelected = false
        });
      }
      foreach (var ext in desc.Extensions) {
        if (!seen.Add(ext)) continue;
        _items.Add(new ExtensionItem {
          Extension = ext,
          FormatName = desc.DisplayName,
          Description = desc.Description,
          IsSelected = false
        });
      }
    }

    _items = [.. _items.OrderBy(i => i.Extension, StringComparer.OrdinalIgnoreCase)];
    ExtensionList.ItemsSource = _items;
    UpdateSelectionCount();
  }

  private void UpdateSelectionCount() {
    var selected = _items.Count(i => i.IsSelected);
    SelectionCountText.Text = $"{selected} of {_items.Count} extensions selected";
  }

  private void SetSelection(Func<ExtensionItem, bool> predicate) {
    foreach (var item in _items)
      item.IsSelected = predicate(item);
    ExtensionList.Items.Refresh();
    UpdateSelectionCount();
  }

  private void OnSelectAll(object sender, RoutedEventArgs e) => SetSelection(_ => true);
  private void OnSelectNone(object sender, RoutedEventArgs e) => SetSelection(_ => false);
  private void OnSelectInvert(object sender, RoutedEventArgs e) => SetSelection(i => !i.IsSelected);

  private void OnSelectCommon(object sender, RoutedEventArgs e) =>
    SetSelection(i => CommonExtensions.Contains(i.Extension));

  private void OnExtCheckChanged(object sender, RoutedEventArgs e) => UpdateSelectionCount();

  private void OnApply(object sender, RoutedEventArgs e) {
    var selectedExts = _items.Where(i => i.IsSelected).Select(i => i.Extension).ToArray();
    if (selectedExts.Length == 0) {
      MessageBox.Show("No extensions selected.", "File Associations", MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }

    var allUsers = RbAllUsers.IsChecked == true;
    if (allUsers && !IsRunningAsAdmin()) {
      var result = MessageBox.Show(
        "Registering for all users requires administrator privileges.\n\nRelaunch as administrator?",
        "Elevation Required", MessageBoxButton.YesNo, MessageBoxImage.Question);
      if (result == MessageBoxResult.Yes)
        RelaunchAsAdmin();
      return;
    }

    try {
      var exePath = GetExePath();
      var rootKey = allUsers ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser;

      if (ChkOpenWith.IsChecked == true || ChkExtractHere.IsChecked == true) {
        RegisterExtensions(rootKey, selectedExts, exePath);
      }

      if (ChkAddToArchive.IsChecked == true) {
        RegisterAddToArchive(rootKey, exePath);
      }

      MessageBox.Show($"Registered {selectedExts.Length} file associations successfully.",
        "File Associations", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    catch (Exception ex) {
      MessageBox.Show($"Failed to register: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
  }

  private void OnRemoveAll(object sender, RoutedEventArgs e) {
    var result = MessageBox.Show(
      "Remove all CompressionWorkbench file associations and context menu entries?",
      "Remove Associations", MessageBoxButton.YesNo, MessageBoxImage.Question);
    if (result != MessageBoxResult.Yes) return;

    var allUsers = RbAllUsers.IsChecked == true;
    if (allUsers && !IsRunningAsAdmin()) {
      MessageBox.Show("Removing all-users associations requires administrator privileges.",
        "Elevation Required", MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }

    try {
      var rootKey = allUsers ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser;
      var allExts = _items.Select(i => i.Extension).ToArray();
      UnregisterAll(rootKey, allExts);
      MessageBox.Show("All associations removed.", "File Associations", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    catch (Exception ex) {
      MessageBox.Show($"Failed to remove: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
  }

  private void OnClose(object sender, RoutedEventArgs e) => Close();

  // ── Registration Logic ─────────────────────────────────────────────

  private void RegisterExtensions(RegistryKey rootKey, string[] extensions, string exePath) {
    foreach (var ext in extensions) {
      if (ChkOpenWith.IsChecked == true) {
        using var key = rootKey.CreateSubKey($@"Software\Classes\{ext}\shell\{AppName}");
        if (key == null) continue;
        key.SetValue("", "Open with CompressionWorkbench");
        key.SetValue("Icon", $"\"{exePath}\",0");
        using var cmdKey = key.CreateSubKey("command");
        cmdKey?.SetValue("", $"\"{exePath}\" \"%1\"");
      }

      if (ChkExtractHere.IsChecked == true) {
        using var key = rootKey.CreateSubKey($@"Software\Classes\{ext}\shell\{AppName}.ExtractHere");
        if (key == null) continue;
        key.SetValue("", "Extract here (CWB)");
        key.SetValue("Icon", $"\"{exePath}\",0");
        using var cmdKey = key.CreateSubKey("command");
        cmdKey?.SetValue("", $"\"{exePath}\" --extract-here \"%1\"");
      }
    }
  }

  private static void RegisterAddToArchive(RegistryKey rootKey, string exePath) {
    foreach (var fileClass in new[] { "Directory", "*" }) {
      using var zipKey = rootKey.CreateSubKey($@"Software\Classes\{fileClass}\shell\{AppName}.AddToZip");
      if (zipKey != null) {
        zipKey.SetValue("", "Add to ZIP archive (CWB)");
        zipKey.SetValue("Icon", $"\"{exePath}\",0");
        using var cmd = zipKey.CreateSubKey("command");
        cmd?.SetValue("", $"\"{exePath}\" --create-zip \"%1\"");
      }

      using var szKey = rootKey.CreateSubKey($@"Software\Classes\{fileClass}\shell\{AppName}.AddTo7z");
      if (szKey != null) {
        szKey.SetValue("", "Add to 7z archive (CWB)");
        szKey.SetValue("Icon", $"\"{exePath}\",0");
        using var cmd = szKey.CreateSubKey("command");
        cmd?.SetValue("", $"\"{exePath}\" --create-7z \"%1\"");
      }
    }
  }

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

  private static void TryDeleteKey(RegistryKey rootKey, string path) {
    try { rootKey.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    catch { /* ignore */ }
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static string GetExePath() {
    var exePath = Environment.ProcessPath;
    if (string.IsNullOrEmpty(exePath))
      exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "CompressionWorkbench.exe";
    return exePath;
  }

  private static bool IsRunningAsAdmin() {
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
  }

  private static void RelaunchAsAdmin() {
    try {
      var psi = new ProcessStartInfo {
        FileName = GetExePath(),
        UseShellExecute = true,
        Verb = "runas"
      };
      Process.Start(psi);
      System.Windows.Application.Current.Shutdown();
    }
    catch {
      // User cancelled UAC
    }
  }
}

/// <summary>Data item for the extension list.</summary>
public sealed class ExtensionItem : INotifyPropertyChanged {
  private bool _isSelected;

  public required string Extension { get; init; }
  public required string FormatName { get; init; }
  public required string Description { get; init; }

  public bool IsSelected {
    get => _isSelected;
    set {
      if (_isSelected == value) return;
      _isSelected = value;
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
    }
  }

  public event PropertyChangedEventHandler? PropertyChanged;
}
