using ServerShared.Titles;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plugin.NaughtyDog;

/// <summary>
/// One Naughty Dog game, read from <c>&lt;ResourceDir&gt;/naughtydog/title.json</c>
/// of a title whose <c>ProtocolFamily</c> is <c>naughtydog</c>. Matchmaking
/// itself is PSN (NP Matching2/Signaling, served by RPCN); this is what the
/// game wanted from Naughty Dog's own servers on Amazon S3/SQS.
/// </summary>
public sealed class NaughtyDogTitle
{
    public const string ProtocolFamily = "naughtydog";
    public const string FileName = "naughtydog/title.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [JsonIgnore]
    public TitleDescriptor Descriptor { get; set; } = new();

    /// <summary>The title's naughtydog/ directory, where its relative paths start.</summary>
    [JsonIgnore]
    public string Directory => Path.Combine(Descriptor.ResourceDir, "naughtydog");

    /// <summary>Names of the net crypt keys in <see cref="NaughtyDogSettings.Keys"/>.</summary>
    public NaughtyDogCryptKeys Crypt { get; set; } = new();

    /// <summary>
    /// S3 buckets the game reads and writes (virtual-host style,
    /// <c>&lt;bucket&gt;.s3.amazonaws.com</c>, or path style on <c>s3.amazonaws.com</c>).
    /// </summary>
    public List<string> Buckets { get; set; } = [];

    /// <summary>Other hosts the game fetches files from (its "content delivery").</summary>
    public List<string> ContentHosts { get; set; } = [];

    /// <summary>Hosts answered as Amazon SQS (SendMessage).</summary>
    public List<string> SqsHosts { get; set; } = [];

    /// <summary>Directory (under <see cref="Directory"/>) of files served from buckets and content hosts: <c>&lt;host or bucket&gt;/&lt;key&gt;</c>.</summary>
    public string StaticDir { get; set; } = "static";

    /// <summary>
    /// Directory (under <see cref="Directory"/>) of game files served sealed: a GET for <c>&lt;key&gt;&lt;suffix&gt;</c> on any
    /// of the title's hosts, with a suffix from <see cref="SealedForms"/>, is answered with <c>&lt;ContentDir&gt;/&lt;key&gt;</c> sealed
    /// in that form. Extracted from the user's disc by tools/re (game data, not in git).
    /// </summary>
    public string ContentDir { get; set; } = "content";

    /// <summary>The sealed forms the game's loaders ask for, told apart by the URL suffix.</summary>
    public List<SealedForm> SealedForms { get; set; } = [];

    /// <summary>The net module the game downloads before it goes online.</summary>
    public NetPatchDefinition NetPatch { get; set; } = new();

    public bool ClaimsHost(string host) =>
        Buckets.Any(b => host.Equals($"{b}.s3.amazonaws.com", StringComparison.OrdinalIgnoreCase)) ||
        ContentHosts.Contains(host, StringComparer.OrdinalIgnoreCase) ||
        SqsHosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    public override string ToString() => Descriptor.ToString();

    public static List<NaughtyDogTitle> LoadAll(IEnumerable<TitleDescriptor> titles, List<string> problems)
    {
        List<NaughtyDogTitle> loaded = [];
        foreach (TitleDescriptor descriptor in titles.Where(t =>
                     string.Equals(t.ProtocolFamily, ProtocolFamily, StringComparison.OrdinalIgnoreCase)))
        {
            string path = Path.Combine(descriptor.ResourceDir, FileName);
            if (!File.Exists(path))
            {
                problems.Add($"{descriptor}: {path} not found");
                continue;
            }
            try
            {
                NaughtyDogTitle? title = Parse(File.ReadAllText(path));
                if (title is null)
                {
                    problems.Add($"{descriptor}: {path} is empty");
                    continue;
                }
                title.Descriptor = descriptor;
                loaded.Add(title);
            }
            catch (JsonException exception)
            {
                problems.Add($"{descriptor}: {path} is not valid: {exception.Message}");
            }
        }
        return loaded;
    }

    /// <summary>Parses the "0x..." numbers title.json uses for hashes and offsets.</summary>
    /// <summary>A title.json's contents (comments allowed).</summary>
    public static NaughtyDogTitle? Parse(string json) => JsonSerializer.Deserialize<NaughtyDogTitle>(json, JsonOptions);

    public static uint ParseHex(string value)
    {
        string digits = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return uint.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>A word written "0x..." (hex, hashes) or plain decimal (amounts).</summary>
    public static uint ParseWord(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ParseHex(value)
            : uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
}

public sealed class NaughtyDogCryptKeys
{
    public string BlowfishKey { get; set; } = "";
    public string HmacKey { get; set; } = "";
}

/// <summary>
/// How the net patch is made: the game's own net module (extracted from its
/// data by tools/re), with its server endpoints pointed at this server and its
/// layout brought up to the build being served. Served from every bucket as
/// <c>&lt;object&gt;.psarc.crypt</c> [C 1.09 0x465a78].
/// </summary>
public sealed class NetPatchDefinition
{
    /// <summary>The objects served, one per game build that asks for its own (<c>net-patch9.bin</c> for 1.09, <c>net-patch7.bin</c> for 1.07).</summary>
    public List<NetPatchObject> Objects { get; set; } = [];

    /// <summary>The DC module to start from, relative to the title's naughtydog/ directory.</summary>
    public string BaseModule { get; set; } = "";

    /// <summary>Largest module the game loads (its buffer, [C 1.09 0x465fe8] allocates 0x40000).</summary>
    public int MaxModuleSize { get; set; } = 0x40000;

    /// <summary>Server endpoint records to point at this server (the same in every object).</summary>
    public List<NetEndpointRecord> Endpoints { get; set; } = [];

    /// <summary>Plain words set to other values (the same in every object): game-side limits that only held for the original services.</summary>
    public List<NetValueRecord> Values { get; set; } = [];

    /// <summary>Lists in the module whose (flagged) records the game downloads; the files are served from the title's <see cref="NaughtyDogTitle.ContentDir"/>.</summary>
    public List<NetDownloadList> Downloads { get; set; } = [];
}

/// <summary>A URL suffix one of the game's loaders adds, and whether the file is wrapped in a PSARC before sealing.</summary>
public sealed class SealedForm
{
    public string Suffix { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>True: a PSARC holding the file at <c>name</c> and <c>name/name</c> (<see cref="Content.NetPatchBuilder.Archive"/>), sealed; false: the file itself, sealed.</summary>
    public bool Archive { get; set; }
}

/// <summary>Where a download list's diff record names the pak it patches.</summary>
public sealed class NetDiffSource
{
    public string BaseField { get; set; } = "";
    public string BaseExtension { get; set; } = "";
}

/// <summary>What the server does about a download it has no file for.</summary>
public enum MissingDownload
{
    /// <summary>Leave the flag: the game asks for it, gets 404 and fails.</summary>
    Fail,

    /// <summary>Clear the flag in the served module: the game uses its own path for it instead of downloading.</summary>
    Local,

    /// <summary>Copy <see cref="NetDownloadList.SubstituteFields"/> from the <see cref="NetDownloadList.SubstituteWith"/> record: the game loads that one instead.</summary>
    Substitute,

    /// <summary>
    /// Copy fields from the first entry of the record's own list (<see cref="NetDownloadList.SubstituteEntry"/>) whose name
    /// the game has: the record then names something of its own that exists. A record with no such entry is reported missing.
    /// </summary>
    SubstituteEntry,
}

/// <summary>A list inside each record ({u32 count, pointer to entries} at <see cref="ListField"/>) and which entry fields replace which record fields.</summary>
public sealed class NetEntrySubstitute
{
    public string ListField { get; set; } = "";
    public string CountField { get; set; } = "0x0";
    public string ArrayField { get; set; } = "0x4";
    public int Stride { get; set; }
    public string NameField { get; set; } = "0x0";

    /// <summary>Record field offset to the entry field copied into it.</summary>
    public Dictionary<string, string> Fields { get; set; } = [];
}

/// <summary>
/// A symbol whose value is {u32 count, pointer to records} where a record with a non-zero flag word (every
/// record when <see cref="FlagField"/> is empty) is fetched as <c>&lt;Directory&gt;/&lt;name&gt;&lt;Extension&gt;</c>
/// through a sealed loader, and a failed fetch is fatal. Field offsets are in the base module's layout.
/// </summary>
public sealed class NetDownloadList
{
    public string Symbol { get; set; } = "";
    public string Note { get; set; } = "";
    public string CountField { get; set; } = "0x0";
    public string ArrayField { get; set; } = "0x4";
    public int Stride { get; set; }
    public string NameField { get; set; } = "";
    public string FlagField { get; set; } = "";

    /// <summary>False: the records name the game's own data and nothing is downloaded; only <see cref="LocalNames"/> is checked.</summary>
    public bool Fetched { get; set; } = true;

    /// <summary>For tools/re: the record names a diff to the disc's <c>&lt;BaseField's name&gt;&lt;BaseExtension&gt;</c>; with no real diff left, an identity diff is written.</summary>
    public NetDiffSource? Diff { get; set; }
    public string Directory { get; set; } = "";
    public string Extension { get; set; } = "";
    public MissingDownload WhenMissing { get; set; } = MissingDownload.Fail;

    /// <summary>File (under the content directory, one name per line) of the files the game has itself; an unflagged record not in it is unobtainable. Empty: not checked.</summary>
    public string LocalNames { get; set; } = "";

    /// <summary>For tools/re: what <see cref="LocalNames"/> lists: "entries" (archive entry names under <see cref="Directory"/>) or "level-ids" (the level-id lines of pak22.txt and the updates' pak22/*.txt).</summary>
    public string LocalNamesFrom { get; set; } = "entries";

    /// <summary>The record (by name) whose fields replace an unobtainable record's with <see cref="MissingDownload.Substitute"/>.</summary>
    public string SubstituteWith { get; set; } = "";
    public List<string> SubstituteFields { get; set; } = [];

    /// <summary>For <see cref="MissingDownload.SubstituteEntry"/>: the record's own list to take a replacement from.</summary>
    public NetEntrySubstitute? SubstituteEntry { get; set; }

    /// <summary>How the game turns a record's name into the file it fetches, when it is more than name + <see cref="Extension"/>.</summary>
    public NetNameSuffix? NameSuffix { get; set; }

    /// <summary>Records the server adds for content it has (the base module may list none).</summary>
    public NetRecordSupply? Supply { get; set; }

    /// <summary>Names other lists define, which count as the game's own besides <see cref="LocalNames"/> (after their records are added).</summary>
    public List<NetListField> LocalFromLists { get; set; } = [];
}

/// <summary>The game appends <see cref="Append"/> to a name that contains <see cref="Contains"/> before fetching it.</summary>
public sealed class NetNameSuffix
{
    public string Contains { get; set; } = "";
    public string Append { get; set; } = "";
}

/// <summary>
/// A file (under the content directory) of records to add to a list, one per line: whitespace-separated strings for
/// <see cref="Fields"/> in order. A record is added only when the file it names is in the content directory, and not when
/// the list already has a record of that name.
/// </summary>
public sealed class NetRecordSupply
{
    public string Manifest { get; set; } = "";

    /// <summary>The record's string fields, in the order a manifest line gives them; the other words are zero.</summary>
    public List<string> Fields { get; set; } = [];
}

/// <summary>A string field of the records of another download list (by symbol).</summary>
public sealed class NetListField
{
    public string Symbol { get; set; } = "";
    public string Field { get; set; } = "";
}

/// <summary>One served net patch: the name its build asks for and how that build's layout differs from the base module.</summary>
public sealed class NetPatchObject
{
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>Record arrays whose records grew in this build.</summary>
    public List<NetArrayWidening> Widen { get; set; } = [];

    /// <summary>Symbols this build looks up that the base module does not have.</summary>
    public List<NetAddedSymbol> AddSymbols { get; set; } = [];
}

/// <summary>
/// A symbol whose value is a server endpoint record: string fields that hold
/// a host, and u32 fields that hold the port of a listener component.
/// </summary>
public sealed class NetEndpointRecord
{
    public string Symbol { get; set; } = "";
    public string Note { get; set; } = "";
    public List<string> HostFields { get; set; } = [];

    /// <summary>Field offset to the listener component whose port goes there.</summary>
    public Dictionary<string, string> PortFields { get; set; } = [];
}

/// <summary>A symbol the served build looks up that the base module lacks, added with a plain value or a record list.</summary>
public sealed class NetAddedSymbol
{
    public string Symbol { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>The value's u32 words (no pointers), when <see cref="Records"/> is empty.</summary>
    public List<uint> Words { get; set; } = [];

    /// <summary>
    /// When not empty, the value is {u32 count, pointer to these records}, each record its words in order ("0x" hex or
    /// decimal). Empty with <see cref="Words"/> [0, 0] is the same list with no records.
    /// </summary>
    public List<List<string>> Records { get; set; } = [];
}

/// <summary>Words of a symbol's record to overwrite; a field holding a pointer is refused.</summary>
public sealed class NetValueRecord
{
    public string Symbol { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>Field offset to the u32 written there.</summary>
    public Dictionary<string, uint> Fields { get; set; } = [];
}

/// <summary>A symbol whose value is {u32 count, pointer to records}, with records grown from <c>OldStride</c> to <c>NewStride</c> bytes.</summary>
public sealed class NetArrayWidening
{
    public string Symbol { get; set; } = "";
    public string Note { get; set; } = "";
    public string CountField { get; set; } = "0x0";
    public string ArrayField { get; set; } = "0x4";
    public int OldStride { get; set; }
    public int NewStride { get; set; }
}
