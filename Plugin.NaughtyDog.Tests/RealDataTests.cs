using Plugin.NaughtyDog.Content;
using Plugin.NaughtyDog.Crypto;
using Plugin.NaughtyDog.Web;

namespace Plugin.NaughtyDog.Tests;

/// <summary>
/// Builds the real Uncharted 2 net patches when the game data is on this
/// machine (nothing of it is committed): UC2_TITLE_DIR = a title's naughtydog/
/// directory with title.json and the net.bin uc2data.py extracted. UC2_OUT, if
/// set, receives the sealed files for tools/re/uc2check.py.
/// </summary>
public class RealDataTests
{
    [Fact]
    public void A_supplied_map_package_is_listed_and_its_map_keeps_its_package_level()
    {
        string? dir = Environment.GetEnvironmentVariable("UC2_TITLE_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "net.bin")), "UC2_TITLE_DIR with net.bin not set");
        NaughtyDogTitle title = NaughtyDogTitle.Parse(File.ReadAllText(Path.Combine(dir!, "title.json")))!;
        string levels = Path.Combine(dir!, title.ContentDir, "build/main/pak22.levels");
        Assert.SkipWhen(!File.Exists(levels), "content not extracted (uc2data.py content)");

        string content = Directory.CreateTempSubdirectory("naughtydog-mappak").FullName;
        try
        {
            // A Sanctuary package as a spawn builder would supply it: the game fetches gompa-multi-pak-ingame + "10" [C 0x4aca08].
            Directory.CreateDirectory(Path.Combine(content, "build/main/pak22"));
            File.Copy(levels, Path.Combine(content, "build/main/pak22.levels"));
            File.WriteAllText(Path.Combine(content, "build/main/pak22/gompa-multi-pak-ingame10.pak"), "x");
            File.WriteAllLines(Path.Combine(content, "build/main/pak22.packages"), ["gompa-multi-pak gompa-multi-pak-ingame"]);
            List<NetDownloadList> lists = [.. title.NetPatch.Downloads.Where(d => d.Symbol is "0xa76c8a22" or "0x0dec97a4")];
            var resolution = NetDownloads.Resolve(File.ReadAllBytes(Path.Combine(dir!, "net.bin")), lists, content);

            Assert.Equal(["build/main/pak22/gompa-multi-pak-ingame10.pak"], resolution.Added.Select(a => a.Download.Key));
            Assert.Contains(resolution.Served, s => s.Download.Key == "build/main/pak22/gompa-multi-pak-ingame10.pak");
            Assert.DoesNotContain(resolution.Replaced, r => r.Download.FileName == "gompa-multi-pak");
            Assert.Contains(resolution.Replaced, r => r.Download.FileName == "ice-cave-multi-pak");
            DcModule served = new(resolution.Module);
            int list = served.Find(0xa76c8a22);
            Assert.Equal(1u, served.ReadU32(list));
            int record = (int)served.ReadU32(list + 4);
            Assert.Equal("gompa-multi-pak", served.ReadStringAt(record));
            Assert.Equal("gompa-multi-pak-ingame", served.ReadStringAt(record + 4));
            int maps = (int)served.ReadU32(served.Find(0x0dec97a4) + 4);
            Assert.Equal("gompa-multi-pak", served.ReadStringAt(maps + 4 * 40 + 0x14));    // record 4: Sanctuary keeps its package level
        }
        finally
        {
            Directory.Delete(content, true);
        }
    }

    [Fact]
    public void The_real_net_patches_build_and_open()
    {
        string? dir = Environment.GetEnvironmentVariable("UC2_TITLE_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "net.bin")), "UC2_TITLE_DIR with net.bin not set");

        NaughtyDogTitle title = NaughtyDogTitle.Parse(File.ReadAllText(Path.Combine(dir!, "title.json")))!;
        byte[] baseModule = File.ReadAllBytes(Path.Combine(dir!, "net.bin"));
        NdCrypt crypt = new(TestData.BlowfishKey, TestData.HmacKey);
        // Patch 7's actor list flags these three for download [data, live 2026-11-10 for navarro]; the level diff list is fetched whole.
        Assert.Equal(["build/main/actor27/mp-hero-navarro.pak", "build/main/actor27/mp-hero-infamous-cole.pak", "build/main/actor27/mp-hero-infamous-cole-evil.pak",
                "build/main/pak22/net8.pak.diff"],
            NetDownloads.Requested(new DcModule(baseModule), title.NetPatch.Downloads).Select(d => d.Key));
        Assert.Equal(MissingDownload.Substitute, title.NetPatch.Downloads[0].WhenMissing);
        string content = Path.Combine(dir!, title.ContentDir);
        if (File.Exists(Path.Combine(content, "build/main/actor27/mp-hero-navarro.pak")))
        {
            // With the disc's content extracted (uc2data.py content): navarro and the identity diff are served; the 12 skins on no
            // disc or update [live: frosty stalled the match load] become mp-hero.
            var resolution = NetDownloads.Resolve(baseModule, title.NetPatch.Downloads, content);
            Assert.Equal(["build/main/actor27/mp-hero-navarro.pak", "build/main/pak22/net8.pak.diff"], resolution.Served.Select(s => s.Download.Key));
            Assert.Equal(12, resolution.Replaced.Count(r => r.Download.List.Symbol == "0x5a4731d2"));
            Assert.Contains(resolution.Replaced, r => r.Download.Key == "build/main/actor27/mp-hero-frosty.pak");
            // The map table's "<map>-multi-pak" package levels nothing defines [live: the host waited for gompa-multi-pak] take their first
            // defined level; stage-multi-pak is defined (pak22.txt). Maps whose levels are all undefined (DLC packs not installed, unreleased
            // maps) are left as they are.
            Assert.Equal(17, resolution.Replaced.Count(r => r.Download.List.Symbol == "0x0dec97a4"));
            Assert.Contains(resolution.Replaced, r => r.Download.FileName == "gompa-multi-pak" && r.Change == "substituted by its entry gompa-multi-wide");
            Assert.DoesNotContain(resolution.Replaced, r => r.Download.FileName == "stage-multi-pak");
            Assert.All(resolution.Missing, m => Assert.False(m.Download.List.Fetched));
            Assert.Contains(resolution.Missing, m => m.Download.FileName == "museum-multi-pak");
            Assert.Equal(["build/main/actor27/mp-hero-navarro.pak", "build/main/pak22/net8.pak.diff"],
                NetDownloads.Requested(new DcModule(resolution.Module), title.NetPatch.Downloads).Select(d => d.Key));
            DcModule resolved = new(resolution.Module);
            int actors = (int)resolved.ReadU32(resolved.Find(0x5a4731d2) + 4);
            Assert.Equal("mp-hero", resolved.ReadStringAt(actors + 45 * 0x20 + 8));     // model 45, was mp-hero-frosty
            Assert.Equal(0x3fu, resolved.ReadU32(actors + 45 * 0x20));                   // keeps its id
            int maps = (int)resolved.ReadU32(resolved.Find(0x0dec97a4) + 4);
            Assert.Equal("gompa-multi-wide", resolved.ReadStringAt(maps + 4 * 40 + 0x14));    // record 4: Sanctuary
            Assert.Equal(0x4452651eu, resolved.ReadU32(maps + 4 * 40 + 0x18));
            Assert.Equal(0xbc4f48fau, resolved.ReadU32(maps + 4 * 40));                       // keeps its key
            baseModule = resolution.Module;

            // The requests the game makes [live 2026-11-10 for the first two], answered from the content directory.
            title.Descriptor = new ServerShared.Titles.TitleDescriptor { TitleId = "uncharted2", ResourceDir = Path.GetDirectoryName(Path.GetFullPath(dir!))! };
            AwsWebService web = new([title], Path.Combine(Path.GetTempPath(), "naughtydog-realdata"), false, _ => crypt);
            byte[] Get(string host, string target)
            {
                var response = web.Handle(new NdHttpRequest { Method = "GET", Target = target, Headers = { ["host"] = host } });
                Assert.Equal(200, response.Status);
                return crypt.Open(response.Body)!;
            }
            byte[] pak = TestData.ReadPsarc(Get("u2livedata.final1.prod.naughtydog.com", "/build/main/actor27/mp-hero-navarro.pak.psarc.crypt"))["mp-hero-navarro.pak/mp-hero-navarro.pak"];
            Assert.Equal(File.ReadAllBytes(Path.Combine(content, "build/main/actor27/mp-hero-navarro.pak")), pak);
            Assert.Equal(File.ReadAllBytes(Path.Combine(content, "build/main/text1/2.networking")),
                Get("u2livedata.final1.prod.s3.amazonaws.com", "/build/main/text1/2.networking.crypt"));
            byte[] diff = TestData.ReadPsarc(Get("u2livedata.final1.prod.naughtydog.com", "/build/main/pak22/net8.pak.diff.psarc.crypt"))["net8.pak.diff"];
            Assert.Equal(20, diff.Length);
        }
        int PortOf(string c) => c == "TICKET" ? 3233 : 3235;

        foreach (NetPatchObject target in title.NetPatch.Objects)
        {
            NetPatch patch = NetPatchBuilder.Build(baseModule, title.NetPatch, target, "192.168.1.179", PortOf, crypt, DateTimeOffset.UnixEpoch);
            byte[] module = TestData.ReadPsarc(crypt.Open(patch.Sealed)!)[target.Name];
            DcModule dc = new(module);
            int config = dc.Find(0x1d38f2f9);
            Assert.Equal("192.168.1.179", dc.ReadStringAt(config + 0x10));
            Assert.Equal(3233u, dc.ReadU32(config + 0x14));
            Assert.Equal(new DcModule(baseModule).Symbols.Count + target.AddSymbols.Count, dc.Symbols.Count);
            foreach (NetAddedSymbol added in target.AddSymbols)
                dc.Find(NaughtyDogTitle.ParseHex(added.Symbol));
            Assert.True(module.Length <= title.NetPatch.MaxModuleSize);
            if (Environment.GetEnvironmentVariable("UC2_OUT") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                File.WriteAllBytes(Path.Combine(output, $"{target.Name}.psarc.crypt"), patch.Sealed);
            }
        }
    }
}
