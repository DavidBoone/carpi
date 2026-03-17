public static class BoltDefinitionLoader
{
    public static List<BoltPid> Load(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return DefinitionCatalog.LoadFromJson(filePath).ToBoltPids();

        return BoltPidLoader.LoadFromCsv(filePath);
    }
}
