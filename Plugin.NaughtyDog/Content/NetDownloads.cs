namespace Plugin.NaughtyDog.Content;

/// <summary>A record of a download list whose flag asks the game to fetch it.</summary>
/// <param name="Key">The object key below the content host, without <c>.psarc.crypt</c>.</param>
/// <param name="FileName">The name inside the sealed archive (the key's last segment).</param>
/// <param name="FlagWord">Module offset of the record's flag word; -1 for a list fetched unconditionally.</param>
/// <param name="Record">Module offset of the record.</param>
/// <param name="Flagged">Whether the game fetches it (its flag is set, or the list has no flag field).</param>
public sealed record NetDownload(NetDownloadList List, string Key, string FileName, int FlagWord, int Record, bool Flagged);

/// <summary>
/// The files a net module tells the game to download before it goes online ([C 1.09 0x4aca08]: a record of
/// the actor list with +0x1c != 0 is fetched as <c>build/main/actor27/&lt;name&gt;.pak</c> through 0x465a78;
/// a zero flag registers the game's own copy instead).
/// </summary>
public static class NetDownloads
{
    /// <summary>
    /// What <see cref="Resolve"/> decided: files served, records changed (with how), files nobody has, records added from a
    /// supply manifest, and manifest lines not added (with why).
    /// </summary>
    public sealed record Resolution(byte[] Module, List<(NetDownload Download, string File)> Served,
        List<(NetDownload Download, string Change)> Replaced, List<(NetDownload Download, string File)> Missing,
        List<(NetDownload Download, string File)> Added, List<(NetDownloadList List, string Line, string Reason)> NotAdded);

    /// <summary>
    /// Matches each record with what exists: a flagged record is fetched, so it needs a file under
    /// <paramref name="contentDir"/>; an unflagged one is loaded from the game's own data, so (when the list names a
    /// <see cref="NetDownloadList.LocalNames"/> file) its name must be in it. A record with neither is unobtainable and is
    /// handled as <see cref="NetDownloadList.WhenMissing"/> says: <see cref="MissingDownload.Local"/> clears a flag,
    /// <see cref="MissingDownload.Substitute"/> copies the substitute record's fields over it (fetch failures and missing
    /// paks both stop the game [C 1.09 0x4aca08; live: "Error in level '...' -- loading stalled!"]),
    /// <see cref="MissingDownload.SubstituteEntry"/> copies those of the first entry of the record's own list the game has
    /// (a map's package level no data defines: the host waits for it forever [C 0x49d410 -> 0xb9d3c; live]).
    /// First, each list with a <see cref="NetDownloadList.Supply"/> gets a record for each manifest line whose file is in
    /// <paramref name="contentDir"/> (the level paks the game fetches and registers [C 0x4aca08]), and names those records
    /// define count as the game's own for lists that name them in <see cref="NetDownloadList.LocalFromLists"/>.
    /// </summary>
    public static Resolution Resolve(byte[] baseModule, IEnumerable<NetDownloadList> lists, string contentDir)
    {
        DcModule module = new(baseModule);
        Resolution result = new(baseModule, [], [], [], [], []);
        List<NetDownloadList> all = [.. lists];
        foreach (NetDownloadList list in all.Where(l => l.Supply is not null))
            Supply(module, list, contentDir, result);
        foreach (NetDownloadList list in all)
        {
            List<NetDownload> records = Records(module, list);
            string? localFile = list.LocalNames.Length == 0 ? null : Path.Combine(contentDir, list.LocalNames);
            HashSet<string>? local = localFile is not null && File.Exists(localFile)
                ? [.. File.ReadAllLines(localFile).Select(l => l.Trim()).Where(l => l.Length > 0)]
                : null;
            if (local is not null)
                foreach (NetListField other in list.LocalFromLists)
                    local.UnionWith(Names(module, all, other).Select(n => n + list.Extension));
            foreach (NetDownload record in records)
            {
                string file = Path.Combine(contentDir, record.Key);
                if (record.Flagged)
                {
                    if (File.Exists(file))
                    {
                        result.Served.Add((record, file));
                        continue;
                    }
                }
                else if (local is null || local.Contains(record.FileName))
                    continue;

                switch (list.WhenMissing)
                {
                    case MissingDownload.Local when record.FlagWord >= 0 && record.Flagged:
                        module.WriteU32(record.FlagWord, 0);
                        result.Replaced.Add((record, "flag cleared"));
                        break;
                    case MissingDownload.Substitute when Substitute(list, records) is var with && with != record:
                        foreach (string field in list.SubstituteFields)
                        {
                            int offset = (int)NaughtyDogTitle.ParseHex(field);
                            module.CopyWord(with.Record + offset, record.Record + offset);
                        }
                        result.Replaced.Add((record, $"substituted by {with.FileName}"));
                        break;
                    case MissingDownload.SubstituteEntry when local is not null && Entry(module, list, record, local) is { } entry:
                        foreach ((string to, string from) in list.SubstituteEntry!.Fields)
                            module.CopyWord(entry.At + (int)NaughtyDogTitle.ParseHex(from), record.Record + (int)NaughtyDogTitle.ParseHex(to));
                        result.Replaced.Add((record, $"substituted by its entry {entry.Name}"));
                        break;
                    default:
                        result.Missing.Add((record, file));
                        break;
                }
            }
        }
        return result.Replaced.Count == 0 && result.Added.Count == 0 ? result : result with { Module = module.ToArray() };
    }

    /// <summary>Adds the manifest's records whose files exist and whose names the list does not have yet.</summary>
    private static void Supply(DcModule module, NetDownloadList list, string contentDir, Resolution result)
    {
        NetRecordSupply supply = list.Supply!;
        string manifest = Path.Combine(contentDir, supply.Manifest);
        if (!File.Exists(manifest))
            return;
        int nameIndex = supply.Fields.FindIndex(f => NaughtyDogTitle.ParseHex(f) == NaughtyDogTitle.ParseHex(list.NameField));
        if (nameIndex < 0)
            throw new InvalidDataException($"{list.Symbol}: Supply.Fields must include the name field {list.NameField}");
        HashSet<string> names = [.. Records(module, list).Select(r => r.FileName)];
        List<IReadOnlyDictionary<int, string>> added = [];
        List<(string Line, string Key, string FileName)> keys = [];
        foreach (string raw in File.ReadAllLines(manifest))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            string[] values = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length != supply.Fields.Count)
            {
                result.NotAdded.Add((list, line, $"wants {supply.Fields.Count} names ({string.Join(" ", supply.Fields)})"));
                continue;
            }
            (string key, string fileName) = KeyOf(list, values[nameIndex]);
            if (!names.Add(fileName))
            {
                result.NotAdded.Add((list, line, "the list already has it"));
                continue;
            }
            if (list.Fetched && !File.Exists(Path.Combine(contentDir, key)))
            {
                result.NotAdded.Add((list, line, $"{key} is not in the content directory"));
                continue;
            }
            added.Add(supply.Fields.Select((f, i) => ((int)NaughtyDogTitle.ParseHex(f), values[i])).ToDictionary());
            keys.Add((line, key, fileName));
        }
        if (added.Count == 0)
            return;
        int at = module.Find(NaughtyDogTitle.ParseHex(list.Symbol));
        int before = (int)module.ReadU32(at + (int)NaughtyDogTitle.ParseHex(list.CountField));
        module.AppendRecords(at + (int)NaughtyDogTitle.ParseHex(list.CountField), at + (int)NaughtyDogTitle.ParseHex(list.ArrayField), list.Stride, added);
        List<NetDownload> records = Records(module, list);
        for (int i = 0; i < keys.Count; i++)
            result.Added.Add((records[before + i], Path.Combine(contentDir, keys[i].Key)));
    }

    /// <summary>The names in a string field of another download list's records.</summary>
    private static IEnumerable<string> Names(DcModule module, List<NetDownloadList> lists, NetListField field)
    {
        NetDownloadList list = lists.FirstOrDefault(l => NaughtyDogTitle.ParseHex(l.Symbol) == NaughtyDogTitle.ParseHex(field.Symbol))
            ?? throw new InvalidDataException($"LocalFromLists: {field.Symbol} is not a download list");
        int at = module.Find(NaughtyDogTitle.ParseHex(list.Symbol));
        int count = (int)module.ReadU32(at + (int)NaughtyDogTitle.ParseHex(list.CountField));
        int array = (int)module.ReadU32(at + (int)NaughtyDogTitle.ParseHex(list.ArrayField));
        for (int i = 0; i < count; i++)
            if (module.ReadStringAt(array + i * list.Stride + (int)NaughtyDogTitle.ParseHex(field.Field)) is { } name)
                yield return name;
    }

    /// <summary>The object key and file name the game fetches for a record named <paramref name="name"/>.</summary>
    private static (string Key, string FileName) KeyOf(NetDownloadList list, string name)
    {
        NetNameSuffix? suffix = list.NameSuffix;
        string fetched = suffix is not null && name.Contains(suffix.Contains, StringComparison.Ordinal) ? name + suffix.Append : name;
        string fileName = fetched + list.Extension;
        return ($"{list.Directory.Trim('/')}/{fileName}", fileName);
    }

    private static NetDownload Substitute(NetDownloadList list, List<NetDownload> records) =>
        records.FirstOrDefault(r => r.FileName == list.SubstituteWith + list.Extension) ??
        throw new InvalidDataException($"{list.Symbol}: substitute \"{list.SubstituteWith}\" is not a record of the list");

    /// <summary>The first entry of the record's own list whose name the game has, or null.</summary>
    private static (int At, string Name)? Entry(DcModule module, NetDownloadList list, NetDownload record, HashSet<string> local)
    {
        NetEntrySubstitute inner = list.SubstituteEntry
            ?? throw new InvalidDataException($"{list.Symbol}: WhenMissing SubstituteEntry needs SubstituteEntry");
        int field = record.Record + (int)NaughtyDogTitle.ParseHex(inner.ListField);
        if (!module.IsPointer(field))
            return null;
        int header = (int)module.ReadU32(field);
        int count = (int)module.ReadU32(header + (int)NaughtyDogTitle.ParseHex(inner.CountField));
        int array = (int)module.ReadU32(header + (int)NaughtyDogTitle.ParseHex(inner.ArrayField));
        for (int i = 0; i < count; i++)
        {
            int at = array + i * inner.Stride;
            if (module.ReadStringAt(at + (int)NaughtyDogTitle.ParseHex(inner.NameField)) is { } name
                && local.Contains(name + list.Extension))
                return (at, name);
        }
        return null;
    }

    /// <summary>The records the game fetches (flagged ones, or all when the list has no flag field).</summary>
    public static List<NetDownload> Requested(DcModule module, IEnumerable<NetDownloadList> lists) =>
        [.. lists.SelectMany(list => Records(module, list)).Where(r => r.Flagged)];

    private static List<NetDownload> Records(DcModule module, NetDownloadList list)
    {
        List<NetDownload> records = [];
        int at = module.Find(NaughtyDogTitle.ParseHex(list.Symbol));
        int count = (int)module.ReadU32(at + (int)NaughtyDogTitle.ParseHex(list.CountField));
        int array = (int)module.ReadU32(at + (int)NaughtyDogTitle.ParseHex(list.ArrayField));
        for (int i = 0; i < count; i++)
        {
            int record = array + i * list.Stride;
            int flag = list.FlagField.Length == 0 ? -1 : record + (int)NaughtyDogTitle.ParseHex(list.FlagField);
            string name = module.ReadStringAt(record + (int)NaughtyDogTitle.ParseHex(list.NameField))
                ?? throw new InvalidDataException($"{list.Symbol} record {i}: +{list.NameField} is not a string");
            (string key, string fileName) = KeyOf(list, name);
            records.Add(new NetDownload(list, key, fileName, flag, record,
                list.Fetched && (flag < 0 || module.ReadU32(flag) != 0)));
        }
        return records;
    }
}
