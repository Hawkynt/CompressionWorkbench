namespace Compression.Registry;

/// <summary>
/// Thrown when an archive cannot be read without a password, or with the one given: the caller
/// should ask for a (different) password and try again. Nothing was extracted.
/// </summary>
/// <param name="message">What needed the password, for the user.</param>
/// <param name="passwordWasSupplied">
/// <see langword="true"/> when a password was given and proved wrong (a failed check, or data
/// that decrypts to garbage); <see langword="false"/> when none was given.
/// </param>
public sealed class ArchivePasswordException(string message, bool passwordWasSupplied) : UnauthorizedAccessException(message) {
  /// <summary>Whether a password was given (and is wrong) rather than missing.</summary>
  public bool PasswordWasSupplied { get; } = passwordWasSupplied;
}
