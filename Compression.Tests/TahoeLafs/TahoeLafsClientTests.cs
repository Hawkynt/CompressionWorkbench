#pragma warning disable CS1591
using System.Net;
using System.Text;
using FileSystem.TahoeLafs;

namespace Compression.Tests.TahoeLafs;

[TestFixture]
public sealed class TahoeLafsClientTests {
  private const string RootWrite = "URI:DIR2:root-write:fingerprint";
  private const string RootRead = "URI:DIR2-RO:root-read:fingerprint";

  [Test]
  public void RecursiveList_UsesChildReadCapabilityAndPreservesHierarchy() {
    const string subWrite = "URI:DIR2:sub-write:fingerprint";
    const string subRead = "URI:DIR2-RO:sub-read:fingerprint";
    var handler = new RecordingHandler(request => {
      var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
      if (path.Contains(subRead, StringComparison.Ordinal))
        return Json("[\"dirnode\",{\"children\":{\"nested.bin\":[\"filenode\",{\"ro_uri\":\"URI:CHK:nested:hash:3:10:4\",\"size\":4,\"mutable\":false,\"format\":\"CHK\"}]}}]");
      if (path.Contains(subWrite, StringComparison.Ordinal))
        return new(HttpStatusCode.InternalServerError);
      return Json("[\"dirnode\",{\"children\":{\"file.txt\":[\"filenode\",{\"ro_uri\":\"URI:CHK:file:hash:3:10:5\",\"size\":5,\"mutable\":false,\"format\":\"CHK\"}],\"sub\":[\"dirnode\",{\"rw_uri\":\"URI:DIR2:sub-write:fingerprint\",\"ro_uri\":\"URI:DIR2-RO:sub-read:fingerprint\",\"mutable\":true,\"format\":\"SDMF\"}]}}]");
    });
    using var http = new HttpClient(handler);
    using var client = new TahoeLafsClient(new Uri("http://127.0.0.1:3456/"), http);

    var entries = client.ListDirectory(TahoeLafsCapability.Parse(RootRead), recursive: true);
    Assert.That(entries.Select(entry => entry.Path), Is.EquivalentTo(new[] { "file.txt", "sub", "sub/nested.bin" }));
    Assert.That(handler.Requests.Any(r => Uri.UnescapeDataString(r.Uri.AbsolutePath).Contains(subRead, StringComparison.Ordinal)), Is.True);
    Assert.That(handler.Requests.Any(r => Uri.UnescapeDataString(r.Uri.AbsolutePath).Contains(subWrite, StringComparison.Ordinal)), Is.False,
      "Recursive reads should use the least-authority child read-cap when Tahoe supplies one.");
  }

  [Test]
  public void ReadFile_ReturnsGatewayPlaintext() {
    var expected = Encoding.UTF8.GetBytes("hello tahoe");
    var handler = new RecordingHandler(_ => Bytes(expected));
    using var http = new HttpClient(handler);
    using var client = new TahoeLafsClient(new Uri("http://localhost:3456/"), http);

    var actual = client.ReadFile(TahoeLafsCapability.Parse("URI:CHK:key:hash:3:10:11"));
    Assert.That(actual, Is.EqualTo(expected));
  }

  [Test]
  public void DirectoryMutation_UsesDocumentedPutPostAndDeleteRoutes() {
    var handler = new RecordingHandler(request => request.Method == HttpMethod.Put
      ? Text("URI:CHK:new-key:new-hash:3:10:3")
      : Text("ok"));
    using var http = new HttpClient(handler);
    using var client = new TahoeLafsClient(new Uri("http://localhost:3456/"), http);
    var root = TahoeLafsCapability.Parse(RootWrite);

    client.CreateDirectory(root, "folder");
    var cap = client.UploadFile(root, "folder/file.bin", new byte[] { 1, 2, 3 });
    client.Remove(root, "old.bin");

    Assert.That(cap?.Format, Is.EqualTo(TahoeLafsObjectFormat.Chk));
    Assert.Multiple(() => {
      Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Post && r.Query.Contains("t=mkdir", StringComparison.Ordinal)), Is.True);
      Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Put && r.Query.Contains("format=CHK", StringComparison.Ordinal)
                                            && Uri.UnescapeDataString(r.Uri.AbsolutePath).EndsWith("/folder/file.bin", StringComparison.Ordinal)), Is.True);
      Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Delete
                                            && Uri.UnescapeDataString(r.Uri.AbsolutePath).EndsWith("/old.bin", StringComparison.Ordinal)), Is.True);
      Assert.That(handler.Requests.Single(r => r.Method == HttpMethod.Put).Body, Is.EqualTo(new byte[] { 1, 2, 3 }));
    });
  }

  [Test]
  public void ReadOnlyDirectory_RejectsMutationBeforeNetworkAccess() {
    var handler = new RecordingHandler(_ => throw new AssertionException("Network must not be touched."));
    using var http = new HttpClient(handler);
    using var client = new TahoeLafsClient(new Uri("http://localhost:3456/"), http);
    var readOnly = TahoeLafsCapability.Parse(RootRead);

    Assert.Throws<UnauthorizedAccessException>(() => client.UploadFile(readOnly, "x.bin", new byte[] { 1 }));
    Assert.Throws<UnauthorizedAccessException>(() => client.Remove(readOnly, "x.bin"));
    Assert.That(handler.Requests, Is.Empty);
  }

  [Test]
  public void MutableFileWrite_RequiresMutableWriteCapability() {
    var handler = new RecordingHandler(_ => Text("ok"));
    using var http = new HttpClient(handler);
    using var client = new TahoeLafsClient(new Uri("http://localhost:3456/"), http);

    client.WriteMutableFile(TahoeLafsCapability.Parse("URI:MDMF:write-key:fingerprint"), new byte[] { 9, 8, 7 });
    Assert.That(handler.Requests.Single().Method, Is.EqualTo(HttpMethod.Put));
    Assert.Throws<UnauthorizedAccessException>(() =>
      client.WriteMutableFile(TahoeLafsCapability.Parse("URI:CHK:key:hash:3:10:3"), new byte[] { 1 }));
  }

  [Test]
  public void Purge_UnlinksImmediateChildrenAndKeepsRoot() {
    var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
      ? Json("[\"dirnode\",{\"children\":{\"a\":[\"filenode\",{\"ro_uri\":\"URI:CHK:a:h:3:10:1\",\"size\":1}],\"dir\":[\"dirnode\",{\"ro_uri\":\"URI:DIR2-RO:r:f\"}]}}]")
      : Text("ok"));
    using var http = new HttpClient(handler);
    using var client = new TahoeLafsClient(new Uri("http://localhost:3456/"), http);

    var removed = client.PurgeDirectory(TahoeLafsCapability.Parse(RootWrite));
    Assert.That(removed, Is.EqualTo(2));
    Assert.That(handler.Requests.Count(r => r.Method == HttpMethod.Delete), Is.EqualTo(2));
  }

  [Test]
  public void GatewayFailure_DoesNotLeakCapabilityInException() {
    const string secret = "URI:CHK:secret-key:secret-hash:3:10:1";
    var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
    using var http = new HttpClient(handler);
    using var client = new TahoeLafsClient(new Uri("http://localhost:3456/"), http);

    var exception = Assert.Throws<IOException>(() => client.ReadFile(TahoeLafsCapability.Parse(secret)));
    Assert.That(exception!.Message, Does.Not.Contain(secret));
    Assert.That(exception.Message, Does.Not.Contain("secret-key"));
  }

  private static HttpResponseMessage Json(string json) {
    var response = new HttpResponseMessage(HttpStatusCode.OK) {
      Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
    return response;
  }

  private static HttpResponseMessage Text(string text)
    => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

  private static HttpResponseMessage Bytes(byte[] bytes)
    => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

  private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler {
    public sealed record Captured(HttpMethod Method, Uri Uri, string Query, byte[] Body);
    public List<Captured> Requests { get; } = [];

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) {
      var body = request.Content == null ? [] : ReadContent(request.Content);
      this.Requests.Add(new(request.Method, request.RequestUri!, request.RequestUri!.Query, body));
      return responder(request);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      => Task.FromResult(this.Send(request, cancellationToken));

    private static byte[] ReadContent(HttpContent content) {
      using var source = content.ReadAsStream(cancellationToken: CancellationToken.None);
      using var target = new MemoryStream();
      source.CopyTo(target);
      return target.ToArray();
    }
  }
}
