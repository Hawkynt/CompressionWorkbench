# Compression.Shell

Windows Explorer context menu integration for CompressionWorkbench.

## Features

- Right-click context menu entries for archive files
- "Extract here", "Extract to folder", "Open with CompressionWorkbench"
- Registration/unregistration of shell extensions

## Components

| File | Description |
|------|-------------|
| `ShellRegistrar` | Registers/unregisters Explorer context menu entries via the Windows registry |

## Usage

Shell registration is typically performed by the installer or manually:

```csharp
ShellRegistrar.Register();    // Add context menu entries
ShellRegistrar.Unregister();  // Remove context menu entries
```

## Requirements

- Windows with .NET 10 runtime
- Administrator privileges for registry modification
