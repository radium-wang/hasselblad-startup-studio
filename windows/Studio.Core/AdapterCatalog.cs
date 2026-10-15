namespace StartupStudio;

// Historical payloads remain separate, with their original manifest and hash.
public sealed class AdapterCatalog
{
    public const string LegacyHash = "a2edfe28ad8dbf91ed19fb87526cc6eceb21ceb00a40347ee58e9afaec6929e0";
    readonly Dictionary<string, Adapter> adapters = new(StringComparer.Ordinal);
    public AdapterCatalog(string resources, Adapter current)
    {
        adapters.Add(current.AdapterSHA256, current);
        var folder = Path.Combine(resources, "legacy", LegacyHash);
        if (Directory.Exists(folder))
        {
            var legacy = Check.Decode<Adapter>(File.ReadAllBytes(Path.Combine(folder, "adapter.json")));
            legacy.Validate(File.ReadAllBytes(Path.Combine(folder, "patch.bin")));
            Check.That(legacy.AdapterSHA256 == LegacyHash, "历史恢复适配层清单不符。");
            adapters.TryAdd(legacy.AdapterSHA256, legacy);
        }
    }
    public Adapter For(RecoveryRecord record) => adapters.TryGetValue(record.AdapterHash, out var adapter)
        ? adapter : throw new StudioException("恢复包对应的历史适配层缺失，请使用原版本软件恢复；首次备份已保留。");
    public bool Recognized(string hash) => hash == Adapter.StockHash || adapters.ContainsKey(hash);
}
