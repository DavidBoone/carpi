public static class DefinitionCatalogValidator
{
    public static ValidationResult Validate(DefinitionCatalog catalog)
    {
        var errors = new List<ValidationError>();

        CheckDuplicateSignalIds(catalog.Signals, errors);
        CheckConflictingFamilyIds(catalog.Signals, errors);

        foreach (var signal in catalog.Signals)
        {
            CheckSignalFields(signal, errors);
            CheckBindings(signal, errors);
            CheckDimensions(signal, errors);
            CheckPresentations(signal, errors);
        }

        return new ValidationResult(errors);
    }

    private static void CheckDuplicateSignalIds(IReadOnlyList<SignalDefinition> signals, List<ValidationError> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var signal in signals)
        {
            if (string.IsNullOrWhiteSpace(signal.Id))
                continue; // caught by CheckSignalFields

            if (!seen.Add(signal.Id) && reported.Add(signal.Id))
                errors.Add(new ValidationError(signal.Id, "signal", $"Duplicate signal id '{signal.Id}'"));
        }
    }

    private static void CheckConflictingFamilyIds(IReadOnlyList<SignalDefinition> signals, List<ValidationError> errors)
    {
        var familyCategories = new Dictionary<string, (SignalCategory Category, SignalCardinality Cardinality, string FirstSignalId)>(StringComparer.Ordinal);

        foreach (var signal in signals)
        {
            if (string.IsNullOrWhiteSpace(signal.FamilyId))
                continue;

            var signalId = signal.Id ?? "(empty)";
            if (familyCategories.TryGetValue(signal.FamilyId, out var existing))
            {
                if (existing.Category != signal.Category)
                    errors.Add(new ValidationError(signalId, "family",
                        $"Family '{signal.FamilyId}' has conflicting categories: " +
                        $"'{existing.Category}' (from '{existing.FirstSignalId}') vs '{signal.Category}'"));

                if (existing.Cardinality != signal.Cardinality)
                    errors.Add(new ValidationError(signalId, "family",
                        $"Family '{signal.FamilyId}' has conflicting cardinalities: " +
                        $"'{existing.Cardinality}' (from '{existing.FirstSignalId}') vs '{signal.Cardinality}'"));
            }
            else
            {
                familyCategories[signal.FamilyId] = (signal.Category, signal.Cardinality, signalId);
            }
        }
    }

    private static void CheckSignalFields(SignalDefinition signal, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(signal.Id))
            errors.Add(new ValidationError("(empty)", "signal", "Signal has empty id"));

        if (string.IsNullOrWhiteSpace(signal.Name))
            errors.Add(new ValidationError(signal.Id, "signal", "Signal has empty name"));

        if (string.IsNullOrWhiteSpace(signal.ShortName))
            errors.Add(new ValidationError(signal.Id, "signal", "Signal has empty short name"));
    }

    private static void CheckBindings(SignalDefinition signal, List<ValidationError> errors)
    {
        var bindingIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var binding in signal.Bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.Id))
                errors.Add(new ValidationError(signal.Id, "binding", "Binding has empty id"));
            else if (!bindingIds.Add(binding.Id))
                errors.Add(new ValidationError(signal.Id, "binding", $"Duplicate binding id '{binding.Id}'"));

            if (string.IsNullOrWhiteSpace(binding.Ecu.LogicalId))
                errors.Add(new ValidationError(signal.Id, "binding", $"Binding '{binding.Id}' has empty ECU logical id"));

            if (string.IsNullOrWhiteSpace(binding.Ecu.Header))
                errors.Add(new ValidationError(signal.Id, "binding", $"Binding '{binding.Id}' has empty ECU header"));

            if (string.IsNullOrWhiteSpace(binding.Query.Service))
                errors.Add(new ValidationError(signal.Id, "binding", $"Binding '{binding.Id}' has empty query service"));

            if (string.IsNullOrWhiteSpace(binding.Query.Identifier))
                errors.Add(new ValidationError(signal.Id, "binding", $"Binding '{binding.Id}' has empty query identifier"));

            if (!HasDecode(binding.Decode))
            {
                // Decode can live on presentation.DecodeOverride instead of the binding.
                // Only flag if no presentation provides one either.
                bool coveredByPresentation = signal.Presentations.Count > 0
                    && signal.Presentations.All(p => p.DecodeOverride != null && HasDecode(p.DecodeOverride));

                if (!coveredByPresentation)
                    errors.Add(new ValidationError(signal.Id, "binding",
                        $"Binding '{binding.Id}' has no decode (neither on binding nor all presentations)"));
            }
        }
    }

    private static bool HasDecode(DecodeDefinition decode)
        => decode.Decoder != null || !string.IsNullOrWhiteSpace(decode.LegacyEquation);

    private static void CheckDimensions(SignalDefinition signal, List<ValidationError> errors)
    {
        if (signal.Cardinality == SignalCardinality.Indexed && signal.Dimensions.Count == 0)
            errors.Add(new ValidationError(signal.Id, "dimension",
                "Signal has indexed cardinality but no dimensions"));

        if (signal.Cardinality == SignalCardinality.Scalar && signal.Dimensions.Count > 0)
            errors.Add(new ValidationError(signal.Id, "dimension",
                "Signal has scalar cardinality but has dimensions"));

        var dimNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dim in signal.Dimensions)
        {
            if (string.IsNullOrWhiteSpace(dim.Name))
                errors.Add(new ValidationError(signal.Id, "dimension", "Dimension has empty name"));
            else if (!dimNames.Add(dim.Name))
                errors.Add(new ValidationError(signal.Id, "dimension", $"Duplicate dimension name '{dim.Name}'"));
        }
    }

    private static void CheckPresentations(SignalDefinition signal, List<ValidationError> errors)
    {
        var presentationIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pres in signal.Presentations)
        {
            if (string.IsNullOrWhiteSpace(pres.Id))
                errors.Add(new ValidationError(signal.Id, "presentation", "Presentation has empty id"));
            else if (!presentationIds.Add(pres.Id))
                errors.Add(new ValidationError(signal.Id, "presentation", $"Duplicate presentation id '{pres.Id}'"));

            if (string.IsNullOrWhiteSpace(pres.Name))
                errors.Add(new ValidationError(signal.Id, "presentation", "Presentation has empty name"));

            if (string.IsNullOrWhiteSpace(pres.Units))
                errors.Add(new ValidationError(signal.Id, "presentation", "Presentation has empty units"));

            if (pres.MinValue.HasValue && pres.MaxValue.HasValue && pres.MinValue > pres.MaxValue)
                errors.Add(new ValidationError(signal.Id, "presentation",
                    $"Presentation '{pres.Id}' has min ({pres.MinValue}) > max ({pres.MaxValue})"));
        }
    }
}

public sealed record ValidationError(string SignalId, string Area, string Message);

public sealed class ValidationResult
{
    public IReadOnlyList<ValidationError> Errors { get; }
    public bool IsValid => Errors.Count == 0;

    public ValidationResult(IReadOnlyList<ValidationError> errors)
    {
        Errors = errors;
    }

    public string Summary()
    {
        if (IsValid)
            return "Catalog is valid.";

        var grouped = Errors.GroupBy(e => e.Area).OrderBy(g => g.Key);
        var lines = new List<string> { $"{Errors.Count} validation error(s):" };
        foreach (var group in grouped)
            lines.Add($"  {group.Key}: {group.Count()}");

        return string.Join(Environment.NewLine, lines);
    }
}
