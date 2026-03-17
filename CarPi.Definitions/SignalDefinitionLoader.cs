public static class SignalDefinitionLoader
{
    public static IReadOnlyList<SignalDefinition> Load(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return DefinitionCatalog.LoadFromJson(filePath).Signals;

        return DefinitionCatalog.FromBoltPids(BoltPidLoader.LoadFromCsv(filePath)).Signals;
    }
}
