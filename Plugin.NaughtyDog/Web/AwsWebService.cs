using Plugin.NaughtyDog.Content;
using Plugin.NaughtyDog.Crypto;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace Plugin.NaughtyDog.Web;

public sealed class NdHttpRequest
{
    public string Method { get; init; } = "GET";
    /// <summary>Path and query, as sent.</summary>
    public string Target { get; init; } = "/";
    /// <summary>Lower-case header names.</summary>
    public Dictionary<string, string> Headers { get; init; } = [];
    public byte[] Body { get; init; } = [];
    public IPEndPoint? Remote { get; init; }

    public string Host => (Headers.GetValueOrDefault("host") ?? "").Split(':')[0].ToLowerInvariant();
    public string Path => Uri.UnescapeDataString(Target.Split('?')[0]);
}

public sealed class NdHttpResponse
{
    public int Status { get; init; } = 200;
    public string ContentType { get; init; } = "application/octet-stream";
    public Dictionary<string, string> Headers { get; init; } = [];
    public byte[] Body { get; init; } = [];

    public static NdHttpResponse Xml(int status, string xml) =>
        new() { Status = status, ContentType = "application/xml", Body = Encoding.UTF8.GetBytes(xml) };
}

/// <summary>
/// The Amazon services a Naughty Dog game used, as far as the game sees them:
/// S3 objects (GET, PUT, DELETE) on its buckets, SQS SendMessage, and plain
/// files on its content-delivery hosts. Requests are not authenticated (the
/// game signs them with Naughty Dog's dead AWS key; nothing checks it).
/// </summary>
/// <remarks>
/// [C 1.09 game/net/http.cpp] S3 virtual-host URLs <c>http://&lt;bucket&gt;.s3.amazonaws.com/&lt;key&gt;</c>
/// (0x465a78), path-style <c>s3.amazonaws.com/&lt;bucket&gt;/&lt;key&gt;</c> for uploads,
/// SQS form POSTs to <c>queue.amazonaws.com/&lt;queue&gt;</c> (0x44b148, Version 2009-02-01).
/// A GET is first made only to read Last-Modified (0x44d56c): equal to the cached copy's
/// time means the client keeps the cache.
/// </remarks>
public sealed class AwsWebService(IReadOnlyList<NaughtyDogTitle> titles, string dataDir, bool logTraffic, Func<NaughtyDogTitle, NdCrypt?> cryptOf)
{
    private const string S3Host = "s3.amazonaws.com";
    private readonly ConcurrentDictionary<(NaughtyDogTitle, string), (byte[] Body, DateTimeOffset LastModified)> _sealed = new();
    private readonly ConcurrentDictionary<(NaughtyDogTitle, string), (DateTime Source, byte[] Body)> _sealedContent = new();

    /// <summary>Serves <paramref name="patch"/> as <c>&lt;object&gt;.psarc.crypt</c> in every bucket of <paramref name="title"/>.</summary>
    public void PublishNetPatch(NaughtyDogTitle title, string objectName, NetPatch patch) =>
        PublishSealed(title, objectName, patch.Sealed, patch.LastModified);

    /// <summary>Serves a sealed archive as <c>&lt;key&gt;.psarc.crypt</c> on every host of <paramref name="title"/> (the loader [C 1.09 0x465a78] adds the suffix).</summary>
    public void PublishSealed(NaughtyDogTitle title, string key, byte[] sealedArchive, DateTimeOffset lastModified) =>
        _sealed[(title, Normalise($"{key}.psarc.crypt"))] = (sealedArchive, lastModified);

    public bool Claims(NdHttpRequest request) =>
        request.Host == S3Host || titles.Any(t => t.ClaimsHost(request.Host));

    public NdHttpResponse Handle(NdHttpRequest request)
    {
        NdHttpResponse response = Route(request);
        if (logTraffic)
            Log.Debug("[naughtydog/http] {Remote} {Method} {Host}{Target} ({Length}B) -> {Status} {Size}B", request.Remote, request.Method,
                request.Host, request.Target, request.Body.Length, response.Status, response.Body.Length);
        return response;
    }

    private NdHttpResponse Route(NdHttpRequest request)
    {
        string host = request.Host;
        if (titles.FirstOrDefault(t => t.SqsHosts.Contains(host, StringComparer.OrdinalIgnoreCase)) is { } sqsTitle)
            return Sqs(sqsTitle, request);

        string path = request.Path.TrimStart('/');
        string? bucket = null;
        if (host == S3Host)
        {
            int slash = path.IndexOf('/');
            bucket = slash < 0 ? path : path[..slash];
            path = slash < 0 ? "" : path[(slash + 1)..];
        }
        else if (host.EndsWith("." + S3Host, StringComparison.Ordinal))
            bucket = host[..^(S3Host.Length + 1)];

        NaughtyDogTitle? title = bucket is null
            ? titles.FirstOrDefault(t => t.ContentHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            : titles.FirstOrDefault(t => t.Buckets.Contains(bucket, StringComparer.OrdinalIgnoreCase));
        if (title is null)
            return S3Error(404, "NoSuchBucket", $"no title serves {bucket ?? host}");
        string container = bucket ?? host;
        string key = Normalise(path);

        return request.Method.ToUpperInvariant() switch
        {
            "GET" or "HEAD" => Get(title, container, key, request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)),
            "PUT" or "POST" => Put(container, key, request.Body),
            "DELETE" => Delete(container, key),
            _ => S3Error(405, "MethodNotAllowed", request.Method),
        };
    }

    /// <summary>"./net-patch9.bin.psarc.crypt" and "a//b" name the same object the game means.</summary>
    private static string Normalise(string key) =>
        string.Join('/', key.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != "."));

    private NdHttpResponse Get(NaughtyDogTitle title, string container, string key, bool head)
    {
        if (_sealed.TryGetValue((title, key), out var sealedObject))
            return Object(sealedObject.Body, sealedObject.LastModified, head);
        if (SealedContent(title, key) is { } content)
            return Object(content.Body, content.LastModified, head);

        foreach (string root in new[] { UploadRoot(container), Path.Combine(title.Directory, title.StaticDir, container) })
        {
            if (Resolve(root, key) is { } file && File.Exists(file))
                return Object(File.ReadAllBytes(file), File.GetLastWriteTimeUtc(file), head);
        }
        Log.Information("[naughtydog/http] {Title}: no object {Container}/{Key}", title, container, key);
        return S3Error(404, "NoSuchKey", $"{container}/{key}");
    }

    /// <summary>
    /// <c>&lt;key&gt;&lt;suffix&gt;</c> answered from <c>&lt;ContentDir&gt;/&lt;key&gt;</c>, sealed in the form the suffix names
    /// (the longest matching suffix wins: ".psarc.crypt" before ".crypt"). Sealed copies are kept until the file changes.
    /// </summary>
    private (byte[] Body, DateTimeOffset LastModified)? SealedContent(NaughtyDogTitle title, string key)
    {
        if (title.SealedForms.Where(f => f.Suffix.Length > 0 && key.EndsWith(f.Suffix, StringComparison.Ordinal))
                .MaxBy(f => f.Suffix.Length) is not { } form ||
            Resolve(Path.Combine(title.Directory, title.ContentDir), key[..^form.Suffix.Length]) is not { } file ||
            !File.Exists(file) || cryptOf(title) is not { } crypt)
            return null;
        DateTime modified = File.GetLastWriteTimeUtc(file);
        if (!_sealedContent.TryGetValue((title, key), out var cached) || cached.Source != modified)
        {
            byte[] data = File.ReadAllBytes(file);
            byte[] body = crypt.Seal(form.Archive ? NetPatchBuilder.Archive(Path.GetFileName(file), data) : data);
            _sealedContent[(title, key)] = cached = (modified, body);
        }
        return (cached.Body, new DateTimeOffset(modified));
    }

    private NdHttpResponse Put(string container, string key, byte[] body)
    {
        if (Resolve(UploadRoot(container), key) is not { } file || key.Length == 0)
            return S3Error(400, "InvalidRequest", key);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, body);
        Log.Information("[naughtydog/http] stored {Container}/{Key} ({Length}B)", container, key, body.Length);
        return new NdHttpResponse { Headers = { ["ETag"] = $"\"{Convert.ToHexStringLower(MD5.HashData(body))}\"" } };
    }

    private NdHttpResponse Delete(string container, string key)
    {
        if (Resolve(UploadRoot(container), key) is { } file && File.Exists(file))
            File.Delete(file);
        return new NdHttpResponse { Status = 204 };
    }

    private NdHttpResponse Sqs(NaughtyDogTitle title, NdHttpRequest request)
    {
        string form = Encoding.UTF8.GetString(request.Body);
        if (request.Target.Contains('?'))
            form = request.Target[(request.Target.IndexOf('?') + 1)..] + "&" + form;
        var fields = HttpUtility.ParseQueryString(form);
        string action = fields["Action"] ?? "";
        string queue = Normalise(request.Path).Replace('/', '_');
        if (!action.Equals("SendMessage", StringComparison.Ordinal))
        {
            Log.Information("[naughtydog/sqs] {Title}: {Queue} action {Action} not handled", title, queue, action);
            return Xml(400, $"<ErrorResponse><Error><Type>Sender</Type><Code>InvalidAction</Code><Message>{action}</Message></Error><RequestId>{Guid.NewGuid()}</RequestId></ErrorResponse>");
        }

        string body = fields["MessageBody"] ?? "";
        string messageId = Guid.NewGuid().ToString();
        string directory = Path.Combine(dataDir, "sqs", Safe(queue));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{messageId}.txt"), body);
        Log.Information("[naughtydog/sqs] {Title}: {Queue} <- {Length} chars", title, queue, body.Length);
        string md5 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(body)));
        return Xml(200, "<SendMessageResponse><SendMessageResult>" +
                        $"<MD5OfMessageBody>{md5}</MD5OfMessageBody><MessageId>{messageId}</MessageId>" +
                        $"</SendMessageResult><ResponseMetadata><RequestId>{Guid.NewGuid()}</RequestId></ResponseMetadata></SendMessageResponse>");
    }

    private string UploadRoot(string container) => Path.Combine(dataDir, "s3", Safe(container));

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));

    /// <summary>The file for <paramref name="key"/> under <paramref name="root"/>, or null if the key climbs out of it.</summary>
    private static string? Resolve(string root, string key)
    {
        string full = Path.GetFullPath(Path.Combine(root, key));
        string top = Path.GetFullPath(root);
        return full.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.Ordinal) || full == top ? full : null;
    }

    private static NdHttpResponse Object(byte[] body, DateTimeOffset lastModified, bool head) => new()
    {
        Body = head ? [] : body,
        Headers =
        {
            ["Last-Modified"] = lastModified.UtcDateTime.ToString("r", CultureInfo.InvariantCulture),
            ["ETag"] = $"\"{Convert.ToHexStringLower(MD5.HashData(body))}\"",
        },
    };

    private static NdHttpResponse Xml(int status, string xml) => NdHttpResponse.Xml(status, "<?xml version=\"1.0\"?>" + xml);

    private static NdHttpResponse S3Error(int status, string code, string message) =>
        Xml(status, $"<Error><Code>{code}</Code><Message>{WebUtility.HtmlEncode(message)}</Message><RequestId>{Guid.NewGuid():N}</RequestId></Error>");
}
