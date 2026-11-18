using Plugin.NaughtyDog.Content;
using Plugin.NaughtyDog.Crypto;
using System.Text;

namespace Plugin.NaughtyDog.Tests;

public class ContentTests
{
    private static readonly NdCrypt Crypt = new(TestData.BlowfishKey, TestData.HmacKey);

    private static NetPatchDefinition Definition() => new()
    {
        Objects = [Target],
        Endpoints =
        [
            new() { Symbol = "0x1d38f2f9", HostFields = ["0x00", "0x10"], PortFields = { ["0x04"] = "NDI", ["0x14"] = "TICKET" } },
        ],
        Values = [new() { Symbol = "0x1d38f2f9", Fields = { ["0x24"] = 1 } }],
    };

    private static readonly NetPatchObject Target = new()
    {
        Name = "net-patch9.bin",
        Widen = [new() { Symbol = "0x5a4731d2", OldStride = 8, NewStride = 12 }],
    };

    private static int PortOf(string component) => component switch { "NDI" => 4235, "TICKET" => 4233, _ => 0 };

    [Fact]
    public void Seal_and_open_round_trip_and_leave_the_partial_block_plain()
    {
        byte[] plain = Encoding.ASCII.GetBytes("thirteen byte");
        byte[] sealedFile = Crypt.Seal(plain);
        Assert.Equal(NdCrypt.FileHeaderSize + 16, sealedFile.Length);
        Assert.Equal(plain, Crypt.Open(sealedFile));

        sealedFile[^1] ^= 1;
        Assert.Null(Crypt.Open(sealedFile));

        byte[] message = Crypt.SealMessage(Encoding.ASCII.GetBytes("a ticket of 21 bytes."));
        Assert.Equal(21 + NdCrypt.HmacSize, message.Length);
        Assert.Equal("a ticket of 21 bytes.", Encoding.ASCII.GetString(Crypt.OpenMessage(message)!));
    }

    [Fact]
    public void Blowfish_matches_the_published_string_key_vector_and_skips_the_tail()
    {
        // Eric Young's bf_test vector: key "abcdefghijklmnopqrstuvwxyz", "BLOWFISH" -> 324ED0FEF413A203.
        NdCrypt crypt = new("abcdefghijklmnopqrstuvwxyz", TestData.HmacKey);
        byte[] data = [.. Encoding.ASCII.GetBytes("BLOWFISH"), 1, 2, 3];
        crypt.EncryptBlocks(data);
        Assert.Equal("324ED0FEF413A203010203", Convert.ToHexString(data));
        crypt.DecryptBlocks(data);
        Assert.Equal("BLOWFISH", Encoding.ASCII.GetString(data, 0, 8));
    }

    [Fact]
    public void Endpoints_are_rewritten_without_moving_existing_data()
    {
        byte[] original = TestData.NetModule();
        DcModule module = new(NetPatchBuilder.BuildModule(original, Definition(), Target, "192.168.1.179", PortOf));
        int config = module.Find(TestData.ConfigSymbol);
        Assert.Equal(0x40, config);
        Assert.Equal("192.168.1.179", module.ReadStringAt(config));
        Assert.Equal("192.168.1.179", module.ReadStringAt(config + 0x10));
        Assert.Equal(4235u, module.ReadU32(config + 4));
        Assert.Equal(4233u, module.ReadU32(config + 0x14));
        Assert.Equal(1u, module.ReadU32(config + 0x24));
        // The old strings are still where they were (another record may point at them).
        Assert.Equal("174.129.16.161", Encoding.ASCII.GetString(module.ToArray(), 0x110, 14));
    }

    [Fact]
    public void Flagged_records_of_a_download_list_are_requested()
    {
        DcModule module = new(TestData.NetModule());
        int array = (int)module.ReadU32(module.Find(TestData.ListSymbol) + 4);
        module.WriteU32(array + 8, 0);   // record 1's id: used as the flag here
        NetDownloadList list = new()
        {
            Symbol = "0x5a4731d2", Stride = 8, FlagField = "0x0", NameField = "0x4", Directory = "build/main/actor27/", Extension = ".pak",
        };
        List<NetDownload> requested = NetDownloads.Requested(module, [list]);
        Assert.Equal(["build/main/actor27/actor-0.pak", "build/main/actor27/actor-2.pak"], requested.Select(r => r.Key));
        Assert.Equal("actor-2.pak", requested[1].FileName);
        Assert.Equal(array + 16, requested[1].FlagWord);

        // No flag field: every record is fetched, and none can be cleared.
        list.FlagField = "";
        Assert.Equal(3, NetDownloads.Requested(module, [list]).Count(r => r.FlagWord == -1));
        var resolution = NetDownloads.Resolve(module.ToArray(), [new() { Symbol = "0x5a4731d2", Stride = 8, NameField = "0x4", WhenMissing = MissingDownload.Local }],
            Path.Combine(Path.GetTempPath(), "no-such-content-dir"));
        Assert.Empty(resolution.Replaced);
        Assert.Equal(3, resolution.Missing.Count);
    }

    [Fact]
    public void Records_nobody_has_are_substituted_and_keep_their_id()
    {
        string content = Directory.CreateTempSubdirectory("naughtydog-content").FullName;
        try
        {
            // actor-0 is on the "disc", actor-2 is served, actor-1 is nowhere: it becomes actor-0 with its own id.
            File.WriteAllLines(Path.Combine(content, "local.names"), ["actor-0.pak"]);
            Directory.CreateDirectory(Path.Combine(content, "actors"));
            File.WriteAllText(Path.Combine(content, "actors", "actor-2.pak"), "x");
            DcModule module = new(TestData.NetModule());
            int array = (int)module.ReadU32(module.Find(TestData.ListSymbol) + 4);
            module.WriteU32(array, 0);      // ids double as flags here: actor-0 and actor-1 unflagged (loaded locally), actor-2 flagged
            module.WriteU32(array + 8, 0);
            NetDownloadList list = new()
            {
                Symbol = "0x5a4731d2", Stride = 8, FlagField = "0x0", NameField = "0x4", Directory = "actors", Extension = ".pak",
                LocalNames = "local.names", WhenMissing = MissingDownload.Substitute, SubstituteWith = "actor-0", SubstituteFields = ["0x4"],
            };
            var resolution = NetDownloads.Resolve(module.ToArray(), [list], content);
            Assert.Equal(["actors/actor-2.pak"], resolution.Served.Select(s => s.Download.Key));
            Assert.Equal([("actors/actor-1.pak", "substituted by actor-0.pak")], resolution.Replaced.Select(r => (r.Download.Key, r.Change)));
            Assert.Empty(resolution.Missing);
            DcModule served = new(resolution.Module);
            Assert.Equal("actor-0", served.ReadStringAt(array + 8 + 4));
            Assert.True(served.IsPointer(array + 8 + 4));
        }
        finally
        {
            Directory.Delete(content, true);
        }
    }

    [Fact]
    public void Supplied_records_are_added_for_files_that_exist_and_define_names_for_other_lists()
    {
        string content = Directory.CreateTempSubdirectory("naughtydog-content").FullName;
        try
        {
            // The game fetches "<name>10.pak" for a name containing "ingame"; only map-pak-ingame's file is there.
            Directory.CreateDirectory(Path.Combine(content, "paks"));
            File.WriteAllText(Path.Combine(content, "paks", "map-pak-ingame10.pak"), "x");
            File.WriteAllLines(Path.Combine(content, "paks.list"), ["# level package", "map-pak-ingame", "other-ingame", "actor-1", "a b"]);
            File.WriteAllLines(Path.Combine(content, "defined.names"), ["actor-0"]);
            NetDownloadList paks = new()
            {
                Symbol = "0x5a4731d2", Note = "paks", Stride = 8, NameField = "0x4", Directory = "paks", Extension = ".pak",
                NameSuffix = new() { Contains = "ingame", Append = "10" }, Supply = new() { Manifest = "paks.list", Fields = ["0x4"] },
            };
            NetDownloadList levels = new()
            {
                Symbol = "0x5a4731d2", Stride = 8, NameField = "0x4", Fetched = false, LocalNames = "defined.names",
                LocalFromLists = [new() { Symbol = "0x5a4731d2", Field = "0x4" }],
            };
            byte[] baseModule = TestData.NetModule();
            var resolution = NetDownloads.Resolve(baseModule, [levels, paks], content);

            Assert.Equal(["paks/map-pak-ingame10.pak"], resolution.Added.Select(a => a.Download.Key));
            Assert.Equal(["other-ingame", "actor-1", "a b"], resolution.NotAdded.Select(n => n.Line));
            DcModule served = new(resolution.Module);
            int list = served.Find(TestData.ListSymbol);
            int array = (int)served.ReadU32(list + 4);
            Assert.Equal(4u, served.ReadU32(list));
            Assert.Equal("actor-1", served.ReadStringAt(array + 8 + 4));
            Assert.Equal(11u, served.ReadU32(array + 8));
            Assert.Equal("map-pak-ingame", served.ReadStringAt(array + 24 + 4));
            Assert.Equal(0u, served.ReadU32(array + 24));
            // The added record is served like any fetched one, and the other list counts every name the list defines.
            Assert.Contains(resolution.Served, s => s.Download.Key == "paks/map-pak-ingame10.pak");
            Assert.All(resolution.Missing, m => Assert.Same(paks, m.Download.List));
            Assert.Equal(["paks/actor-0.pak", "paks/actor-1.pak", "paks/actor-2.pak"], resolution.Missing.Select(m => m.Download.Key));

            levels.LocalFromLists = [];
            Assert.Equal(["actor-1", "actor-2", "map-pak-ingame"],
                NetDownloads.Resolve(baseModule, [levels, paks], content).Missing.Where(m => m.Download.List == levels).Select(m => m.Download.FileName));
        }
        finally
        {
            Directory.Delete(content, true);
        }
    }

    [Fact]
    public void An_added_symbol_with_records_points_at_them()
    {
        NetPatchObject target = new()
        {
            Name = "net-patch9.bin",
            AddSymbols = [new() { Symbol = "0x9f9bcb43", Records = [["0xe458eb64", "80000"], ["0x47fcaa1a", "0x30d40"]] }],
        };
        DcModule module = new(NetPatchBuilder.BuildModule(TestData.NetModule(), Definition(), target, "h", PortOf));
        int value = module.Find(0x9f9bcb43);
        Assert.Equal(2u, module.ReadU32(value));
        Assert.True(module.IsPointer(value + 4));
        int records = (int)module.ReadU32(value + 4);
        Assert.Equal([0xe458eb64u, 80000u, 0x47fcaa1au, 200000u], Enumerable.Range(0, 4).Select(i => module.ReadU32(records + 4 * i)));

        target.AddSymbols[0].Records.Add(["0x1"]);
        Assert.Throws<InvalidDataException>(() => NetPatchBuilder.BuildModule(TestData.NetModule(), Definition(), target, "h", PortOf));
    }

    [Fact]
    public void An_added_symbol_is_found_and_the_table_stays_sorted()
    {
        NetPatchObject target = new() { Name = "net-patch9.bin", AddSymbols = [new() { Symbol = "0x9f9bcb43", Words = [0, 0] }, new() { Symbol = "0x00000001", Words = [7] }] };
        DcModule module = new(NetPatchBuilder.BuildModule(TestData.NetModule(), Definition(), target, "h", PortOf));
        Assert.Equal(0u, module.ReadU32(module.Find(0x9f9bcb43)));
        Assert.Equal(7u, module.ReadU32(module.Find(1)));
        Assert.Equal(module.Symbols.Select(s => s.Hash).Order(), module.Symbols.Select(s => s.Hash));
        Assert.Equal(4, module.Symbols.Count);
        Assert.Equal("actor-1", module.ReadStringAt((int)module.ReadU32(module.Find(TestData.ListSymbol) + 4) + 12));
        Assert.Throws<InvalidOperationException>(() => module.AddSymbol(0x9f9bcb43, new byte[4]));
    }

    [Fact]
    public void A_value_cannot_overwrite_a_pointer()
    {
        NetPatchDefinition definition = Definition();
        definition.Values = [new() { Symbol = "0x1d38f2f9", Fields = { ["0x10"] = 1 } }];
        Assert.Throws<InvalidDataException>(() => NetPatchBuilder.BuildModule(TestData.NetModule(), definition, Target, "h", PortOf));
    }

    [Fact]
    public void A_widened_array_keeps_its_records_and_their_pointers()
    {
        DcModule module = new(NetPatchBuilder.BuildModule(TestData.NetModule(), Definition(), Target, "h", PortOf));
        int list = module.Find(TestData.ListSymbol);
        Assert.Equal(3u, module.ReadU32(list));
        int array = (int)module.ReadU32(list + 4);
        Assert.True(module.IsPointer(list + 4));
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal((uint)(10 + i), module.ReadU32(array + 12 * i));
            Assert.Equal($"actor-{i}", module.ReadStringAt(array + 12 * i + 4));
            Assert.Equal(0u, module.ReadU32(array + 12 * i + 8));
        }
        // Survives a write and a fresh parse (relocation table rebuilt).
        DcModule again = new(module.ToArray());
        Assert.Equal("actor-2", again.ReadStringAt((int)again.ReadU32(again.Find(TestData.ListSymbol) + 4) + 24 + 4));
    }

    [Fact]
    public void The_net_patch_opens_to_a_psarc_holding_the_module_under_both_mount_guesses()
    {
        NetPatch patch = NetPatchBuilder.Build(TestData.NetModule(), Definition(), Target, "10.0.0.2", PortOf, Crypt,
            DateTimeOffset.UnixEpoch);
        byte[] archive = Crypt.Open(patch.Sealed)!;
        Dictionary<string, byte[]> files = TestData.ReadPsarc(archive);
        Assert.Equal(["net-patch9.bin", "net-patch9.bin/net-patch9.bin"], files.Keys.Order());
        DcModule module = new(files["net-patch9.bin"]);
        Assert.Equal("10.0.0.2", module.ReadStringAt(module.Find(TestData.ConfigSymbol)));
        Assert.Equal(files["net-patch9.bin"], files["net-patch9.bin/net-patch9.bin"]);
    }

    [Fact]
    public void Big_bodies_span_several_blocks()
    {
        byte[] big = new byte[0x10000 * 2 + 77];
        new Random(5).NextBytes(big);   // incompressible, so stored raw (full blocks written as size 0)
        Dictionary<string, byte[]> files = TestData.ReadPsarc(PsarcWriter.Write([("a/b.pak", big), ("empty", [])]));
        Assert.Equal(big, files["a/b.pak"]);
        Assert.Empty(files["empty"]);
    }

    [Fact]
    public void A_module_too_big_for_the_game_buffer_is_refused()
    {
        NetPatchDefinition definition = Definition();
        definition.MaxModuleSize = 0x100;
        Assert.Throws<InvalidDataException>(() => NetPatchBuilder.BuildModule(TestData.NetModule(), definition, Target, "h", PortOf));
    }
}

public class SettingsTests
{
    [Fact]
    public void The_net_patch_gets_the_game_port_not_the_listener_port()
    {
        NaughtyDogSettings settings = new();
        Assert.Equal(13233, settings.PortOf("TICKET"));
        Assert.Equal(3233, settings.GamePortOf("TICKET"));
        settings.Listeners.Add(new NaughtyDogListener { Component = "EXTRA", Port = 5000, Udp = true });
        Assert.Equal(5000, settings.GamePortOf("EXTRA"));
        Assert.Equal(0, settings.GamePortOf("NONE"));
    }
}
