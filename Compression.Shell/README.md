# Compression.Shell

Windows Explorer context menu integration for CompressionWorkbench.

## What gets registered

The archive extensions are not a fixed list: they are every `Extensions` and
`CompoundExtensions` entry of every descriptor in `FormatRegistry`, so a newly
added format picks up its context menu with no change here.

| Entry | Applies to | Runs |
|---|---|---|
| Open with CompressionWorkbench | every archive extension | the UI, with the file |
| Extract here | every archive extension | `cwb extract "%1" --output "%V"` |
| Extract to folder... | every archive extension, `Extended` so it needs **Shift**+right-click | the UI with `--extract`, which shows the folder picker |
| Add to ZIP archive | `Directory` and `*` | `cwb create "%1.zip" "%1"` |
| Add to 7z archive | `Directory` and `*` | `cwb create "%1.7z" "%1"` |

## Components

| File | Description |
|------|-------------|
| `ShellRegistrar` | Registers and unregisters the entries above |

## Usage

Registration needs the paths of both executables, because some verbs run the CLI
and some run the UI:

```csharp
ShellRegistrar.Register(cwbExePath, uiExePath);  // Add context menu entries
ShellRegistrar.Unregister();                     // Remove them again
```

## Requirements

- Windows with the .NET 10 runtime.
- No elevation. Every key is written under
  `HKEY_CURRENT_USER\Software\Classes`, so registration affects the current user
  only.

## Relationship to the UI

`Compression.UI` does not reference this project. Its *File associations* window
carries its own registration code, which additionally offers an HKLM (all-users)
mode — that mode is the one that needs administrator rights. This project is the
standalone path, for an installer or a script that has no UI to drive.
