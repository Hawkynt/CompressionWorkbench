using System.Runtime.CompilerServices;
using Hawkynt.NativeForms.ComponentModel;

namespace Compression.NativeUI.ViewModels;

/// <summary>
/// Base for every view model in the NativeForms frontend. NativeForms already ships
/// <see cref="ObservableObject"/> with change notification and per-property errors; this adds the
/// <c>SetField</c> spelling the view models were written against so they read the same on both.
/// </summary>
internal abstract class ViewModelBase : ObservableObject {
  protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    => this.SetProperty(ref field, value, name ?? string.Empty);

  protected new void OnPropertyChanged([CallerMemberName] string? name = null)
    => base.OnPropertyChanged(name ?? string.Empty);
}
