using Plugin.NaughtyDog.Content;
using Plugin.NaughtyDog.Crypto;
using Plugin.NaughtyDog.Ticket;
using Plugin.NaughtyDog.Web;
using ServerShared.Titles;
using System.Net;
using System.Text;

namespace Plugin.NaughtyDog.Tests;

public sealed class ServiceTests : IDisposable
{
    private static readonly NdCrypt Crypt = new(TestData.BlowfishKey, TestData.HmacKey);
    private readonly string _dir = Directory.CreateTempSubdirectory("naughtydog-tests").FullName;
    private readonly NaughtyDogTitle _title;
    private readonly AwsWebService _web;

    public ServiceTests()
    {
        _title = new NaughtyDogTitle
        {
            Descriptor = new TitleDescriptor { TitleId = "uncharted2", ResourceDir = Path.Combine(_dir, "title") },
            Buckets = ["u2livedata.final1.prod"],
            ContentHosts = ["u2livedata.final1.prod.naughtydog.com"],
            SqsHosts = ["queue.amazonaws.com"],
            SealedForms = [new() { Suffix = ".psarc.crypt", Archive = true }, new() { Suffix = ".crypt" }],
        };
        _web = new AwsWebService([_title], Path.Combine(_dir, "data"), logTraffic: false, _ => Crypt);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private NdHttpResponse Send(string method, string host, string target, string body = "") =>
        _web.Handle(new NdHttpRequest { Method = method, Target = target, Headers = { ["host"] = host }, Body = Encoding.UTF8.GetBytes(body) });

    [Fact]
    public void The_net_patch_is_served_at_the_path_the_game_builds()
    {
        DateTimeOffset modified = new(2026, 11, 9, 20, 0, 0, TimeSpan.Zero);
        _web.PublishNetPatch(_title, "net-patch9.bin", new NetPatch([1, 2, 3], modified, 3));

        // [C 0x465a78] "%s/%s/%s.psarc.crypt" with the S3 URL, "." and the object.
        NdHttpResponse response = Send("GET", "u2livedata.final1.prod.s3.amazonaws.com", "/./net-patch9.bin.psarc.crypt");
        Assert.Equal(200, response.Status);
        Assert.Equal([1, 2, 3], response.Body);
        Assert.Equal("Mon, 09 Nov 2026 20:00:00 GMT", response.Headers["Last-Modified"]);
        Assert.Equal(200, Send("GET", "s3.amazonaws.com", "/u2livedata.final1.prod/net-patch9.bin.psarc.crypt").Status);
        Assert.Equal(404, Send("GET", "u2livedata.final1.prod.s3.amazonaws.com", "/net-patch8.bin.psarc.crypt").Status);
        Assert.Equal(404, Send("GET", "other.s3.amazonaws.com", "/net-patch9.bin.psarc.crypt").Status);
    }

    [Fact]
    public void Content_files_are_served_sealed_in_the_form_the_suffix_asks_for()
    {
        string actors = Path.Combine(_title.Directory, _title.ContentDir, "build/main/actor27");
        string text = Path.Combine(_title.Directory, _title.ContentDir, "build/main/text1");
        Directory.CreateDirectory(actors);
        Directory.CreateDirectory(text);
        File.WriteAllText(Path.Combine(actors, "mp-hero-navarro.pak"), "model");
        File.WriteAllText(Path.Combine(text, "2.networking"), "strings");

        // [C 0x4aca08 -> 0x465a78] content host + "build/main/actor27" + "<name>.pak" + ".psarc.crypt": a sealed PSARC.
        NdHttpResponse pak = Send("GET", "u2livedata.final1.prod.naughtydog.com", "/build/main/actor27/mp-hero-navarro.pak.psarc.crypt");
        Assert.Equal(200, pak.Status);
        var archive = TestData.ReadPsarc(Crypt.Open(pak.Body)!);
        Assert.Equal("model", Encoding.ASCII.GetString(archive["mp-hero-navarro.pak/mp-hero-navarro.pak"]));
        Assert.Equal("model", Encoding.ASCII.GetString(archive["mp-hero-navarro.pak"]));

        // [C 0x465768 -> 0x44d9b0] "%s/%s.crypt" with the S3 bucket URL: the file itself, sealed.
        NdHttpResponse strings = Send("GET", "u2livedata.final1.prod.s3.amazonaws.com", "/build/main/text1/2.networking.crypt");
        Assert.Equal(200, strings.Status);
        Assert.Equal("strings", Encoding.ASCII.GetString(Crypt.Open(strings.Body)!));
        Assert.True(strings.Headers.ContainsKey("Last-Modified"));

        Assert.Equal(404, Send("GET", "u2livedata.final1.prod.s3.amazonaws.com", "/build/main/text1/3.networking.crypt").Status);
        Assert.Equal(404, Send("GET", "u2livedata.final1.prod.s3.amazonaws.com", "/build/main/text1/2.networking").Status);
    }

    [Fact]
    public void Uploads_are_kept_and_served_back_and_cannot_climb_out()
    {
        Assert.Equal(200, Send("PUT", "s3.amazonaws.com", "/u2livedata.final1.prod/cinema1/abc/file.bin", "replay").Status);
        NdHttpResponse back = Send("GET", "u2livedata.final1.prod.s3.amazonaws.com", "/cinema1/abc/file.bin");
        Assert.Equal("replay", Encoding.UTF8.GetString(back.Body));
        Assert.Equal(204, Send("DELETE", "u2livedata.final1.prod.s3.amazonaws.com", "/cinema1/abc/file.bin").Status);
        Assert.Equal(404, Send("GET", "u2livedata.final1.prod.s3.amazonaws.com", "/cinema1/abc/file.bin").Status);
        Assert.Equal(400, Send("PUT", "u2livedata.final1.prod.s3.amazonaws.com", "/../../escape", "x").Status);
    }

    [Fact]
    public void Static_files_come_from_the_title_directory()
    {
        string file = Path.Combine(_title.Directory, _title.StaticDir, "u2livedata.final1.prod.naughtydog.com", "languages", "english.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "hello");
        Assert.Equal("hello", Encoding.UTF8.GetString(Send("GET", "u2livedata.final1.prod.naughtydog.com", "/languages/english.txt").Body));
        Assert.True(_web.Claims(new NdHttpRequest { Headers = { ["host"] = "u2livedata.final1.prod.naughtydog.com:80" } }));
        Assert.False(_web.Claims(new NdHttpRequest { Headers = { ["host"] = "example.com" } }));
    }

    [Fact]
    public void Sqs_send_message_is_stored_and_acknowledged()
    {
        // [C 0x44b148] form body; Signature etc. are ignored.
        NdHttpResponse response = Send("POST", "queue.amazonaws.com", "/prod-nat-queue",
            "Action=SendMessage&MessageBody=nat%3D2&Version=2009-02-01&SignatureVersion=2");
        Assert.Equal(200, response.Status);
        Assert.Contains("<MD5OfMessageBody>", Encoding.UTF8.GetString(response.Body));
        string stored = Assert.Single(Directory.GetFiles(Path.Combine(_dir, "data", "sqs", "prod-nat-queue")));
        Assert.Equal("nat=2", File.ReadAllText(stored));
        Assert.Equal(400, Send("POST", "queue.amazonaws.com", "/q", "Action=DeleteQueue").Status);
    }

    [Fact]
    public void The_ticket_comes_back_sealed_and_unchanged()
    {
        TicketService tickets = new(_ => Crypt, logTraffic: false);
        byte[] ticket = Encoding.ASCII.GetBytes("an NP ticket, whatever RPCN issued, echoed byte for byte");
        byte[] reply = tickets.Answer(3233, new IPEndPoint(IPAddress.Loopback, 3658), ticket)!;
        Assert.True(reply.Length >= 0x14);
        Assert.Equal(ticket, Crypt.OpenMessage(reply));
        Assert.Null(new TicketService(_ => null, false).Answer(3233, new IPEndPoint(IPAddress.Loopback, 1), ticket));
        Assert.Null(tickets.Answer(3233, new IPEndPoint(IPAddress.Loopback, 1), new byte[TicketService.MaxReply]));
    }
}
