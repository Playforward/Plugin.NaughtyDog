using System.Net;
using System.Net.Sockets;

namespace Plugin.NaughtyDog;

/// <summary>
/// Plugin-wide settings, kept in <c>Configs/NaughtyDogConfig.json</c>. What
/// differs per game is in the title's <c>naughtydog/title.json</c>
/// (<see cref="NaughtyDogTitle"/>).
/// </summary>
public sealed class NaughtyDogSettings
{
    /// <summary>
    /// The address written into the net patch as every server the game talks
    /// to. Empty means this machine's first LAN IPv4 address.
    /// </summary>
    public string PublicHost { get; set; } = "";

    /// <summary>
    /// Which service answers on which ServerApp listener. Each port also needs
    /// an entry in <c>ServerAppSettings.json</c>. The game's S3/SQS requests go
    /// to port 80 (its URLs carry no port), so the HTTP listener is either 80
    /// or a port that 80 is redirected to (docs/SETUP.md).
    /// </summary>
    public List<NaughtyDogListener> Listeners { get; set; } =
    [
        new() { Component = NaughtyDogComponents.Http, Port = 10080, GamePort = 80 },
        new() { Component = NaughtyDogComponents.Ticket, Port = 13233, Udp = true, GamePort = 3233 },
        new() { Component = NaughtyDogComponents.Ndi, Port = 13235, Udp = true, GamePort = 3235 },
    ];

    /// <summary>
    /// Secrets the titles refer to by name (the net crypt keys). Kept here, out
    /// of the repositories; tools/re/uc2keys.py prints them from a decrypted EBOOT.
    /// </summary>
    public Dictionary<string, string> Keys { get; set; } = [];

    /// <summary>Where uploads (S3 PUTs, SQS messages) are kept, relative to the working directory.</summary>
    public string DataDir { get; set; } = "Data/naughtydog";

    /// <summary>Log every request and datagram at Debug level.</summary>
    public bool LogTraffic { get; set; } = true;

    public int PortOf(string component) =>
        Listeners.FirstOrDefault(l => string.Equals(l.Component, component, StringComparison.OrdinalIgnoreCase))?.Port ?? 0;

    /// <summary>The port the game is told for a component (written into the net patch), or 0 when it has no listener.</summary>
    public int GamePortOf(string component) =>
        Listeners.FirstOrDefault(l => string.Equals(l.Component, component, StringComparison.OrdinalIgnoreCase)) is { } l
            ? (l.GamePort != 0 ? l.GamePort : l.Port) : 0;

    public IPAddress PublicAddress()
    {
        if (!string.IsNullOrWhiteSpace(PublicHost))
        {
            if (IPAddress.TryParse(PublicHost, out IPAddress? parsed))
                return parsed;
            return Dns.GetHostAddresses(PublicHost).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        return Dns.GetHostAddresses(Dns.GetHostName()).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
               ?? IPAddress.Loopback;
    }
}

public sealed class NaughtyDogListener
{
    public string Component { get; set; } = "";

    /// <summary>The ServerApp listener's port.</summary>
    public int Port { get; set; }
    public bool Udp { get; set; }

    /// <summary>
    /// The port the game is told (in the net patch), when it is not <see cref="Port"/>; 0 = the same.
    /// Uncharted 2 binds the ticket port on its own machine before sending to it
    /// [C 1.09 0x4dc5a8 "bind to localhost:%d", called by NetInit 0x447c28 with net config +0x14]; with
    /// RPCS3 on the server's machine that bind fails (EADDRINUSE, "failed to initialize socket" [live
    /// 2026-11-09]) unless the server listens elsewhere and tools/port-redirect.sh forwards GamePort to Port.
    /// </summary>
    public int GamePort { get; set; }
}

public static class NaughtyDogComponents
{
    /// <summary>S3 buckets, SQS and the content-delivery hosts (plain HTTP).</summary>
    public const string Http = "HTTP";

    /// <summary>The UDP ticket check the game makes before it goes online [C 1.09 0x447c28].</summary>
    public const string Ticket = "TICKET";

    /// <summary>The net config's other UDP endpoints (fields +0x00/+0x08): purpose not known yet; logged.</summary>
    public const string Ndi = "NDI";
}
