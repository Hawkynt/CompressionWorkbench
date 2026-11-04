using Win32Exception = System.ComponentModel.Win32Exception;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Compression.Registry;
using FileFormat.Mht;

namespace Compression.Tests.Mht;

/// <summary>
/// Our MHTML output judged by two independent readers: CPython's stdlib <c>email</c> package (structure
/// and decoding — runs wherever a Python 3 is on the PATH) and Chromium itself, which must render a
/// created page with its stylesheet and image resolved from the archive (runs where a Chrome or
/// Chromium binary is found: <c>CWB_TOOL_CHROME</c>, a PortableApps install, the default install
/// paths, or <c>google-chrome</c>/<c>chromium</c> on the PATH).
/// </summary>
[TestFixture]
public sealed class MhtOracleTests {

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb_mht_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static readonly byte[] Html = """
    <!DOCTYPE html><html><head><meta charset="utf-8"><title>CWB MHT</title><link rel="stylesheet" href="css/site.css"></head>
    <body><p>Grüße</p><img src="img/dot%20one.png"></body></html>
    """u8.ToArray();
  private static readonly byte[] Css = "body{color:rgb(18, 52, 86)}"u8.ToArray();
  private static readonly byte[] Png = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAIAAAAmkwkpAAAAEElEQVR4nGP4z8AARwzEcQCukw/x0F8jngAAAABJRU5ErkJggg==");

  private string CreatePage() {
    var path = Path.Combine(this._tmp, "page.mht");
    using (var fs = File.Create(path))
      new MhtFormatDescriptor().Create(fs, [
        ArchiveInputInfo.InMemory("css/site.css", Css),
        ArchiveInputInfo.InMemory("index.html", Html),
        ArchiveInputInfo.InMemory("img/dot one.png", Png),
      ], new FormatCreateOptions());
    return path;
  }

  // ── Python email ─────────────────────────────────────────────────────

  private const string ListScript = """
    import email, email.policy, hashlib, sys
    msg = email.message_from_binary_file(open(sys.argv[1], 'rb'), policy=email.policy.compat32)
    print(msg.get_content_type(), msg.get_param('type'))
    for part in msg.walk():
        if part.is_multipart():
            continue
        data = part.get_payload(decode=True) or b''
        print(part.get_content_type(), part.get('Content-Location'), hashlib.sha256(data).hexdigest())
    """;

  private static readonly Lazy<string?> Python = new(() => {
    foreach (var candidate in new[] { "python3", "python" })
      try {
        if (Run(candidate, ["-c", "import email"]).ExitCode == 0) return candidate;
      } catch (Win32Exception) {
        // not on the PATH
      }
    return null;
  });

  [Test, Category("ArchiveExternal")]
  public void Given_CreatedArchive_When_ParsedByPythonEmail_Then_RootFirstAndEveryPartDecodes() {
    if (Python.Value is not { } python) { Assert.Ignore("No Python 3 on the PATH."); return; }
    var script = Path.Combine(this._tmp, "list.py");
    File.WriteAllText(script, ListScript);
    var (exit, stdout, stderr) = Run(python, ["-I", script, this.CreatePage()]);
    Assert.That(exit, Is.Zero, stderr);
    Assert.That(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), Is.EqualTo(new[] {
      "multipart/related text/html",
      $"text/html https://mhtml.invalid/index.html {Sha(Html)}",
      $"text/css https://mhtml.invalid/css/site.css {Sha(Css)}",
      $"image/png https://mhtml.invalid/img/dot%20one.png {Sha(Png)}",
    }));
  }

  [Test, Category("ArchiveExternal")]
  public void Given_ChromeSnapshotWithAddedPart_When_ParsedByPythonEmail_Then_OldAndNewPartsDecode() {
    if (Python.Value is not { } python) { Assert.Ignore("No Python 3 on the PATH."); return; }
    var path = Path.Combine(this._tmp, "edited.mhtml");
    File.WriteAllBytes(path, MhtTests.Vector("chrome-blink.mhtml"));
    using (var fs = File.Open(path, FileMode.Open, FileAccess.ReadWrite)) {
      var d = new MhtFormatDescriptor();
      d.Remove(fs, ["style.css"]);
      d.Add(fs, [ArchiveInputInfo.InMemory("css/new.css", Css)]);
    }
    var script = Path.Combine(this._tmp, "list.py");
    File.WriteAllText(script, ListScript);
    var (exit, stdout, stderr) = Run(python, ["-I", script, path]);
    Assert.That(exit, Is.Zero, stderr);
    var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    Assert.That(lines, Has.Length.EqualTo(5));
    Assert.That(lines[2], Is.EqualTo("image/png http://127.0.0.1:8731/img/dot.png " + Sha(Png)));
    Assert.That(lines[4], Is.EqualTo("text/css http://127.0.0.1:8731/css/new.css " + Sha(Css)));
  }

  // ── Chromium ─────────────────────────────────────────────────────────

  private static string? FindChrome() {
    if (Environment.GetEnvironmentVariable("CWB_TOOL_CHROME") is { Length: > 0 } explicitPath && File.Exists(explicitPath))
      return explicitPath;
    if (OperatingSystem.IsWindows()) {
      var candidates = new List<string>();
      foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        candidates.Add(Path.Combine(drive.RootDirectory.FullName, "PortableApps", "GoogleChromePortable64", "App", "Chrome-bin", "chrome.exe"));
      foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        if (root.Length > 0) candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
      return candidates.FirstOrDefault(File.Exists);
    }
    foreach (var name in new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser" })
      foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        if (dir.Length > 0 && File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
    return null;
  }

  [Test, Category("ExternalInterop")]
  public async Task Given_CreatedArchive_When_OpenedInChromium_Then_StylesheetAndImageResolveFromTheArchive() {
    if (FindChrome() is not { } chrome) { Assert.Ignore("No Chrome/Chromium found."); return; }
    var page = this.CreatePage();
    var result = await EvaluateInChrome(chrome, new Uri(page).AbsoluteUri,
      "document.title + '|' + [...document.images].map(i => i.naturalWidth).join(',') + '|' + getComputedStyle(document.body).color + '|' + document.body.innerText.trim()");
    Assert.That(result, Is.EqualTo("CWB MHT|4|rgb(18, 52, 86)|Grüße"));
  }

  private async Task<string> EvaluateInChrome(string chrome, string url, string expression) {
    var port = 9500 + Random.Shared.Next(400);
    var start = new ProcessStartInfo(chrome) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var a in new[] { "--headless=new", $"--remote-debugging-port={port}", $"--user-data-dir={Path.Combine(this._tmp, "profile")}",
               "--no-first-run", "--no-sandbox", "--disable-gpu", "about:blank" })
      start.ArgumentList.Add(a);
    using var process = Process.Start(start)!;
    try {
      using var http = new HttpClient();
      string? wsUrl = null;
      for (var i = 0; i < 60 && wsUrl == null; ++i) {
        try {
          using var doc = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list"));
          wsUrl = doc.RootElement.EnumerateArray().Where(t => t.GetProperty("type").GetString() == "page")
            .Select(t => t.GetProperty("webSocketDebuggerUrl").GetString()).FirstOrDefault();
        } catch (HttpRequestException) {
          // not listening yet
        }
        if (wsUrl == null) await Task.Delay(500);
      }
      if (wsUrl == null) Assert.Ignore("Chrome did not open its DevTools endpoint.");
      using var ws = new ClientWebSocket();
      await ws.ConnectAsync(new Uri(wsUrl!), CancellationToken.None);
      var id = 0;
      async Task<JsonElement> Send(string method, object parameters) {
        var myId = ++id;
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { id = myId, method, @params = parameters }), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[1 << 16];
        while (true) {
          using var ms = new MemoryStream();
          WebSocketReceiveResult r;
          do {
            r = await ws.ReceiveAsync(buffer, CancellationToken.None);
            ms.Write(buffer, 0, r.Count);
          } while (!r.EndOfMessage);
          using var msg = JsonDocument.Parse(ms.ToArray());
          if (msg.RootElement.TryGetProperty("id", out var got) && got.GetInt32() == myId)
            return msg.RootElement.Clone();
        }
      }
      await Send("Page.enable", new { });
      await Send("Page.navigate", new { url });
      await Task.Delay(3000);
      var eval = await Send("Runtime.evaluate", new { expression });
      return eval.GetProperty("result").GetProperty("result").GetProperty("value").GetString() ?? "";
    } finally {
      try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }
  }

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  private static (int ExitCode, string StdOut, string StdErr) Run(string file, IEnumerable<string> arguments) {
    var start = new ProcessStartInfo(file) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
      StandardOutputEncoding = Encoding.UTF8,
    };
    start.Environment["PYTHONUTF8"] = "1";
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout.Result, stderr);
  }
}
