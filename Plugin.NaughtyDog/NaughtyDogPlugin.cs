using ModdableWebServer.Helper;
using ModdableWebServer.Interfaces;
using Plugin.NaughtyDog.Content;
using Plugin.NaughtyDog.Crypto;
using Plugin.NaughtyDog.Ticket;
using Plugin.NaughtyDog.Web;
using ServerShared.Controllers;
using ServerShared.EventArguments;
using ServerShared.Plugins;
using ServerShared.Server;
using ServerShared.Titles;

namespace Plugin.NaughtyDog;

/// <summary>
/// Naughty Dog's own online services for its PS3 games (Uncharted 2 first),
/// served on ServerApp listeners. Their matchmaking is PSN's (RPCN serves it);
/// what died with Naughty Dog's servers is the S3-hosted net patch every
/// online session starts by downloading, the UDP ticket check, and the S3/SQS
/// uploads (stats, cinema files). Which games are served comes from title data
/// (<see cref="NaughtyDogTitle"/>).
/// </summary>
public sealed class NaughtyDogPlugin : ServerPlugin<NaughtyDogSettings>
{
    public override uint Priority => 1;
    public override string Name => "NaughtyDog";

    private readonly Dictionary<int, Action<CoreUdpSession, ReadOnlyMemory<byte>>> _udpHandlers = [];
    private readonly Dictionary<NaughtyDogTitle, NdCrypt> _crypts = [];

    public override void Start()
    {
        List<string> problems = [];
        List<NaughtyDogTitle> titles = NaughtyDogTitle.LoadAll(TitleRegistry.All, problems);
        foreach (string problem in problems)
            Log.Error("[naughtydog] {Problem}", problem);
        if (titles.Count == 0)
        {
            Log.Warning("[naughtydog] no title with ProtocolFamily \"{Family}\" in Configs/{File}; not starting",
                NaughtyDogTitle.ProtocolFamily, TitleRegistry.ConfigFileName);
            return;
        }

        string host = Config.PublicAddress().ToString();
        AwsWebService web = new(titles, Config.DataDir, Config.LogTraffic, t => _crypts.GetValueOrDefault(t));
        foreach (NaughtyDogTitle title in titles)
        {
            if (CryptFor(title) is not { } crypt)
                continue;
            _crypts[title] = crypt;
            PublishNetPatches(title, web, crypt, host);
        }
        NaughtyDogHttpRoutes.Service = web;

        TicketService tickets = new(_ => _crypts.Values.FirstOrDefault(), Config.LogTraffic);
        if (_crypts.Values.Distinct().Count() > 1)
            Log.Warning("[naughtydog] more than one title with its own crypt keys shares the ticket port; only the first is answered");
        BindListeners(tickets);
        CoreUdpSession.OnBytesReceived += OnUdpBytes;

        Log.Information("[naughtydog] serving {Titles}; listening: {Listeners}; net patches point at {Host}",
            string.Join(", ", titles), string.Join(", ", Config.Listeners.Select(l =>
                $"{l.Component} {l.Port}/{(l.Udp ? "udp" : "tcp")}{(l.GamePort != 0 && l.GamePort != l.Port ? $" (game uses {l.GamePort})" : "")}")), host);
    }

    public override void Stop()
    {
        CoreUdpSession.OnBytesReceived -= OnUdpBytes;
        _udpHandlers.Clear();
        _crypts.Clear();
        NaughtyDogHttpRoutes.Service = null;
    }

    private NdCrypt? CryptFor(NaughtyDogTitle title)
    {
        string? blowfish = Config.Keys.GetValueOrDefault(title.Crypt.BlowfishKey);
        string? hmac = Config.Keys.GetValueOrDefault(title.Crypt.HmacKey);
        if (string.IsNullOrEmpty(blowfish) || string.IsNullOrEmpty(hmac))
        {
            Log.Error("[naughtydog] {Title}: keys \"{Blowfish}\" and \"{Hmac}\" are not in {File} Keys (tools/re/uc2keys.py prints them); " +
                      "no net patch and no ticket check for it", title, title.Crypt.BlowfishKey, title.Crypt.HmacKey, ConfigFileName);
            return null;
        }
        return new NdCrypt(blowfish, hmac);
    }

    private void PublishNetPatches(NaughtyDogTitle title, AwsWebService web, NdCrypt crypt, string host)
    {
        NetPatchDefinition definition = title.NetPatch;
        if (definition.Objects.Count == 0)
            return;
        string basePath = Path.Combine(title.Directory, definition.BaseModule);
        if (!File.Exists(basePath))
        {
            Log.Error("[naughtydog] {Title}: net patch base module {Path} not found (extract it with tools/re/uc2data.py); " +
                      "the game cannot go online without the net patch", title, basePath);
            return;
        }
        // Stable across restarts while the inputs stay the same, so the client keeps its cached copy. The download
        // directories and names files count too: they decide which records are changed (CheckDownloads).
        DateTimeOffset lastModified = new[] { basePath, Path.Combine(title.Descriptor.ResourceDir, NaughtyDogTitle.FileName), ConfigFileName }
            .Concat(definition.Downloads.Select(d => Path.Combine(title.Directory, title.ContentDir, d.Directory)))
            .Concat(definition.Downloads.Where(d => d.LocalNames.Length > 0).Select(d => Path.Combine(title.Directory, title.ContentDir, d.LocalNames)))
            .Concat(definition.Downloads.Where(d => d.Supply is not null).Select(d => Path.Combine(title.Directory, title.ContentDir, d.Supply!.Manifest)))
            .Where(Path.Exists).Select(p => new DateTimeOffset(File.GetLastWriteTimeUtc(p))).Max();
        byte[] baseModule;
        try
        {
            baseModule = CheckDownloads(title, File.ReadAllBytes(basePath));
        }
        catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException)
        {
            Log.Error("[naughtydog] {Title}: download lists not read: {Message}; no net patch", title, exception.Message);
            return;
        }
        foreach (NetPatchObject target in definition.Objects)
        {
            string objectName = target.Name;
            try
            {
                NetPatch patch = NetPatchBuilder.Build(baseModule, definition, target, host, Config.GamePortOf, crypt, lastModified);
                web.PublishNetPatch(title, objectName, patch);
                Log.Information("[naughtydog] {Title}: {Object}.psarc.crypt built ({Module}B module, {Size}B sealed) in buckets {Buckets}",
                    title, objectName, patch.ModuleSize, patch.Sealed.Length, string.Join(", ", title.Buckets));
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or KeyNotFoundException)
            {
                Log.Error("[naughtydog] {Title}: {Object} not built: {Message}", title, objectName, exception.Message);
            }
        }
    }

    /// <summary>
    /// Checks the files the net module tells the game to download against the title's content directory (the web
    /// service serves them from there) and returns the module to build the patches from: with
    /// <see cref="MissingDownload.Local"/>/<see cref="MissingDownload.Substitute"/>, records nobody can supply are changed so
    /// the game does not stop on them (<see cref="NetDownloads.Resolve"/>).
    /// </summary>
    private static byte[] CheckDownloads(NaughtyDogTitle title, byte[] baseModule)
    {
        var resolution = NetDownloads.Resolve(baseModule, title.NetPatch.Downloads, Path.Combine(title.Directory, title.ContentDir));
        foreach ((NetDownload download, string file) in resolution.Added)
            Log.Information("[naughtydog] {Title}: {List} gets {Name} from {File}", title, download.List.Note, download.FileName, file);
        foreach ((NetDownloadList list, string line, string reason) in resolution.NotAdded)
            Log.Warning("[naughtydog] {Title}: {List} manifest line \"{Line}\" not added: {Reason}", title, list.Note, line, reason);
        foreach ((NetDownload download, string file) in resolution.Served)
            Log.Information("[naughtydog] {Title}: download {Key} served from {File}", title, download.Key, file);
        foreach ((NetDownload download, string change) in resolution.Replaced)
            Log.Warning("[naughtydog] {Title}: {Key} exists neither in {Content} nor in the game's own data; {Change}",
                title, download.Key, Path.Combine(title.Directory, title.ContentDir), change);
        foreach ((NetDownload download, string file) in resolution.Missing)
        {
            if (download.List.Fetched)
                Log.Error("[naughtydog] {Title}: no file for download {Key} ({File}); the game will fail when it asks for it (tools/re/uc2data.py content)",
                    title, download.Key, file);
            else
                Log.Warning("[naughtydog] {Title}: {List} names {Name}, which neither the game's data nor the record's own list supplies; " +
                            "what uses it will not work (content the player does not have)", title, download.List.Note, download.FileName);
        }
        return resolution.Module;
    }

    /// <summary>Hooks each configured listener; a port ServerApp has no server for is an error, not a silent timeout.</summary>
    private void BindListeners(TicketService tickets)
    {
        foreach (NaughtyDogListener listener in Config.Listeners)
        {
            var server = ServerController.Servers.FirstOrDefault(s => s.Port == listener.Port && s.IsUdp == listener.Udp);
            if (server is null)
            {
                Log.Error("[naughtydog] no {Kind} server on port {Port} for {Component}; add it to ServerAppSettings.json{Udp}",
                    listener.Udp ? "UDP" : "TCP", listener.Port, listener.Component, listener.Udp ? " with UseUDP=true" : "");
                continue;
            }
            if (listener.Udp && (listener.GamePort == 0 || listener.GamePort == listener.Port))
                Log.Warning("[naughtydog] {Component} listens on UDP {Port}, the port the game binds for itself: RPCS3 on this machine " +
                            "will fail with \"failed to initialize socket\"; give it a GamePort and run tools/port-redirect.sh", listener.Component, listener.Port);
            switch (listener.Component.ToUpperInvariant())
            {
                case NaughtyDogComponents.Http when !listener.Udp:
                    if (server.Server is IHttpServer http)
                        http.HeaderAttributeToMethods.Merge(typeof(NaughtyDogHttpRoutes));
                    break;
                case NaughtyDogComponents.Ticket when listener.Udp:
                    _udpHandlers[listener.Port] = (session, data) =>
                    {
                        if (tickets.Answer(listener.Port, session.EndPoint, data.Span) is { } reply)
                            session.SendAsync(reply);
                    };
                    break;
                case NaughtyDogComponents.Ndi when listener.Udp:
                    // NdiNetUpdate [C 1.09 0x449fa0]: text ("<online id> heartbeat ...", "<id> playercount", "<id> get-location ...")
                    // sealed like the ticket reply (Blowfish of text + HMAC). Replies are read by 0x4497c4; not answered yet.
                    _udpHandlers[listener.Port] = (session, data) =>
                    {
                        if (_crypts.Values.FirstOrDefault()?.OpenMessage(data.Span) is { } text)
                            Log.Information("[naughtydog/ndi] {Remote} -> {Port}: \"{Text}\" (not answered yet)",
                                session.EndPoint, listener.Port, System.Text.Encoding.Latin1.GetString(text).TrimEnd('\0'));
                        else
                            Log.Information("[naughtydog/ndi] {Remote} -> {Port}: {Length}B {Data} (does not open with the net crypt)",
                                session.EndPoint, listener.Port, data.Length, Convert.ToHexString(data.Span));
                    };
                    break;
                default:
                    Log.Error("[naughtydog] listener {Port}/{Kind}: component {Component} is not served that way (HTTP is TCP; TICKET and NDI are UDP)",
                        listener.Port, listener.Udp ? "udp" : "tcp", listener.Component);
                    break;
            }
        }
    }

    private void OnUdpBytes(object? sender, SessionBytesReceivedEventArgs e)
    {
        if (sender is not CoreUdpSession session || !_udpHandlers.TryGetValue(session.Server.Port, out var handler))
            return;
        try
        {
            handler(session, e.Data);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "[naughtydog] UDP {Port}: datagram from {Remote} could not be handled", session.Server.Port, session.EndPoint);
        }
    }
}
