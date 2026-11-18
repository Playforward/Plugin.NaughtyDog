using ModdableWebServer.Attributes;
using ModdableWebServer.Senders;
using System.Net;

namespace Plugin.NaughtyDog.Web;

/// <summary>
/// ServerApp HTTP routes for the HTTP listener: any path (up to eight
/// segments, with or without a query) goes to <see cref="AwsWebService"/> when
/// its Host is one a title claims; anything else is declined so other routes
/// still run. Header routes (any Host) like Plugin.Medius's, so the same
/// patterns from another plugin do not collide as dictionary keys; merged only
/// onto this plugin's HTTP listeners.
/// </summary>
internal static class NaughtyDogHttpRoutes
{
    public static AwsWebService? Service { get; set; }

    private const string Host = "host";

    [HTTPHeader("GET", "/{p1}?{!args}", Host)]
    [HTTPHeader("PUT", "/{p1}?{!args}", Host)]
    [HTTPHeader("POST", "/{p1}?{!args}", Host)]
    [HTTPHeader("DELETE", "/{p1}?{!args}", Host)]
    [HTTPHeader("HEAD", "/{p1}?{!args}", Host)]
    [HTTPHeader("GET", "/", Host)]
    [HTTPHeader("POST", "/", Host)]
    [HTTPHeader("GET", "/{p1}/{p2}?{!args}", Host)]
    [HTTPHeader("PUT", "/{p1}/{p2}?{!args}", Host)]
    [HTTPHeader("POST", "/{p1}/{p2}?{!args}", Host)]
    [HTTPHeader("DELETE", "/{p1}/{p2}?{!args}", Host)]
    [HTTPHeader("GET", "/{p1}/{p2}/{p3}?{!args}", Host)]
    [HTTPHeader("PUT", "/{p1}/{p2}/{p3}?{!args}", Host)]
    [HTTPHeader("POST", "/{p1}/{p2}/{p3}?{!args}", Host)]
    [HTTPHeader("DELETE", "/{p1}/{p2}/{p3}?{!args}", Host)]
    [HTTPHeader("GET", "/{p1}/{p2}/{p3}/{p4}?{!args}", Host)]
    [HTTPHeader("PUT", "/{p1}/{p2}/{p3}/{p4}?{!args}", Host)]
    [HTTPHeader("DELETE", "/{p1}/{p2}/{p3}/{p4}?{!args}", Host)]
    [HTTPHeader("GET", "/{p1}/{p2}/{p3}/{p4}/{p5}?{!args}", Host)]
    [HTTPHeader("PUT", "/{p1}/{p2}/{p3}/{p4}/{p5}?{!args}", Host)]
    [HTTPHeader("DELETE", "/{p1}/{p2}/{p3}/{p4}/{p5}?{!args}", Host)]
    [HTTPHeader("GET", "/{p1}/{p2}/{p3}/{p4}/{p5}/{p6}?{!args}", Host)]
    [HTTPHeader("PUT", "/{p1}/{p2}/{p3}/{p4}/{p5}/{p6}?{!args}", Host)]
    [HTTPHeader("GET", "/{p1}/{p2}/{p3}/{p4}/{p5}/{p6}/{p7}?{!args}", Host)]
    [HTTPHeader("PUT", "/{p1}/{p2}/{p3}/{p4}/{p5}/{p6}/{p7}?{!args}", Host)]
    [HTTPHeader("GET", "/{p1}/{p2}/{p3}/{p4}/{p5}/{p6}/{p7}/{p8}?{!args}", Host)]
    [HTTPHeader("PUT", "/{p1}/{p2}/{p3}/{p4}/{p5}/{p6}/{p7}/{p8}?{!args}", Host)]
    public static bool Call(ServerSender sender)
    {
        if (Service is not { } service)
            return false;
        NdHttpRequest request = new()
        {
            Method = sender.Request.Method,
            Target = sender.Request.Url,
            Headers = new Dictionary<string, string>(sender.Headers, StringComparer.OrdinalIgnoreCase),
            Body = sender.Request.BodyBytes,
            Remote = (sender.Session as NetCoreServer.TcpSession)?.Socket?.RemoteEndPoint as IPEndPoint,
        };
        if (!service.Claims(request))
            return false;
        NdHttpResponse response = service.Handle(request);
        sender.Response.Clear().SetBegin(response.Status).SetHeader("Content-Type", response.ContentType);
        foreach ((string name, string value) in response.Headers)
            sender.Response.SetHeader(name, value);
        sender.Response.SetBody(response.Body);
        sender.SendResponse();
        return true;
    }
}
