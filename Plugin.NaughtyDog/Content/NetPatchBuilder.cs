using Plugin.NaughtyDog.Crypto;

namespace Plugin.NaughtyDog.Content;

/// <summary>A built net patch: the sealed archive and the time it is served as last modified.</summary>
public sealed record NetPatch(byte[] Sealed, DateTimeOffset LastModified, int ModuleSize);

/// <summary>
/// Makes the net patch the game downloads before it goes online
/// (<c>&lt;bucket&gt;/./&lt;object&gt;.psarc.crypt</c>): its net DC module with the
/// endpoints pointed at this server, in a PSARC, sealed with the net crypt.
/// </summary>
/// <remarks>
/// [C 1.09 0x465a78] The client saves the download as <c>&lt;object&gt;.psarc</c>,
/// mounts it and copies out <c>&lt;object&gt;/&lt;object&gt;</c>; which part of that
/// path is the mount point is not settled, so the archive holds the module under
/// both <c>&lt;object&gt;</c> and <c>&lt;object&gt;/&lt;object&gt;</c>. On a disc boot
/// (g_gameInfo.m_onDisc, +0x219c) [C 0x447c28] then loads the copy and it replaces
/// the game's net module (0x6f1c3f87) [C 0x465fe8].
/// </remarks>
public static class NetPatchBuilder
{
    public static byte[] BuildModule(byte[] baseModule, NetPatchDefinition definition, NetPatchObject target, string host, Func<string, int> portOf)
    {
        DcModule module = new(baseModule);
        foreach (NetEndpointRecord record in definition.Endpoints)
        {
            int at = module.Find(NaughtyDogTitle.ParseHex(record.Symbol));
            foreach (string field in record.HostFields)
            {
                int word = at + (int)NaughtyDogTitle.ParseHex(field);
                if (!module.IsPointer(word))
                    throw new InvalidDataException($"{record.Symbol}+{field} is not a string pointer in the base module");
                module.SetString(word, host);
            }
            foreach ((string field, string component) in record.PortFields)
            {
                int port = portOf(component);
                if (port == 0)
                    throw new InvalidOperationException($"{record.Symbol}+{field} wants the port of {component}, which has no listener");
                module.WriteU32(at + (int)NaughtyDogTitle.ParseHex(field), (uint)port);
            }
        }
        foreach (NetValueRecord record in definition.Values)
        {
            int at = module.Find(NaughtyDogTitle.ParseHex(record.Symbol));
            foreach ((string field, uint value) in record.Fields)
            {
                int word = at + (int)NaughtyDogTitle.ParseHex(field);
                if (module.IsPointer(word))
                    throw new InvalidDataException($"{record.Symbol}+{field} is a pointer in the base module, not a plain word");
                module.WriteU32(word, value);
            }
        }
        foreach (NetArrayWidening widen in target.Widen)
        {
            int at = module.Find(NaughtyDogTitle.ParseHex(widen.Symbol));
            int count = (int)module.ReadU32(at + (int)NaughtyDogTitle.ParseHex(widen.CountField));
            module.WidenArray(at + (int)NaughtyDogTitle.ParseHex(widen.ArrayField), count, widen.OldStride, widen.NewStride);
        }
        foreach (NetAddedSymbol added in target.AddSymbols)
        {
            if (added.Records.Count == 0)
            {
                module.AddSymbol(NaughtyDogTitle.ParseHex(added.Symbol), Words(added.Words));
                continue;
            }
            if (added.Records.Select(r => r.Count).Distinct().Count() != 1)
                throw new InvalidDataException($"{added.Symbol}: its records do not all have the same number of words");
            int records = module.Append(Words([.. added.Records.SelectMany(r => r).Select(NaughtyDogTitle.ParseWord)]));
            module.AddSymbol(NaughtyDogTitle.ParseHex(added.Symbol), Words([(uint)added.Records.Count, (uint)records]), [4]);
        }
        byte[] bytes = module.ToArray();
        if (bytes.Length > definition.MaxModuleSize)
            throw new InvalidDataException($"the net module is 0x{bytes.Length:x} bytes; the game loads at most 0x{definition.MaxModuleSize:x}");
        return bytes;
    }

    private static byte[] Words(IReadOnlyList<uint> words)
    {
        byte[] bytes = new byte[4 * words.Count];
        for (int i = 0; i < words.Count; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4 * i), words[i]);
        return bytes;
    }

    public static byte[] Archive(string objectName, byte[] module) =>
        PsarcWriter.Write([(objectName, module), ($"{objectName}/{objectName}", module)]);

    public static NetPatch Build(byte[] baseModule, NetPatchDefinition definition, NetPatchObject target, string host,
        Func<string, int> portOf, NdCrypt crypt, DateTimeOffset lastModified)
    {
        byte[] module = BuildModule(baseModule, definition, target, host, portOf);
        return new NetPatch(crypt.Seal(Archive(target.Name, module)), lastModified, module.Length);
    }
}
