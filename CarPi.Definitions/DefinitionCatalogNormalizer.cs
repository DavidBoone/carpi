public static class DefinitionCatalogNormalizer
{
    public static DefinitionCatalog NormalizeSeedCatalog(DefinitionCatalog catalog)
    {
        var signals = new List<SignalDefinition>(catalog.Signals);
        signals = NormalizeOdometer(signals);
        signals = signals.Select(NormalizeSignalIdentity).ToList();

        return new DefinitionCatalog
        {
            Version = catalog.Version,
            Vehicle = catalog.Vehicle,
            Signals = signals
        };
    }

    private static List<SignalDefinition> NormalizeOdometer(List<SignalDefinition> signals)
    {
        var odometerSignals = signals
            .Where(s => s.Bindings.Any(b => b.Query.RawCommand == "2200A6"))
            .Where(s => s.ShortName is "Odo km" or "Odo mi")
            .ToList();

        if (odometerSignals.Count < 2)
            return signals;

        var merged = odometerSignals[0] with
        {
            Id = "vehicle.odometer",
            FamilyId = "vehicle.odometer",
            Name = "Vehicle Odometer",
            ShortName = "Odo",
            Category = SignalCategory.Vehicle,
            Presentations = odometerSignals
                .SelectMany(s => s.Presentations)
                .Select((p, index) => p with
                {
                    Id = $"vehicle.odometer:{index}",
                    Index = null,
                    DisplayOrder = index
                })
                .ToList()
        };

        return signals
            .Except(odometerSignals)
            .Append(merged)
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static SignalDefinition NormalizeSignalIdentity(SignalDefinition signal)
    {
        if (signal.Id == "vehicle.odometer")
            return signal;

        if (signal.ShortName == "Speed" && signal.Bindings.Any(b => b.Query.RawCommand == "22000D"))
            return signal with
            {
                Id = "vehicle.speed",
                FamilyId = "vehicle.speed",
                Name = "Vehicle Speed",
                ShortName = "Speed",
                Category = SignalCategory.Vehicle
            };

        if (signal.Name == "Cell Voltage")
        {
            var index = signal.Dimensions.FirstOrDefault()?.Index;
            var suffix = index.HasValue ? $":{index.Value:D2}" : "";
            return signal with
            {
                Id = $"battery.cell_voltage{suffix}",
                FamilyId = "battery.cell_voltage",
                Name = "Battery Cell Voltage",
                ShortName = "Cell",
                Category = SignalCategory.Battery
            };
        }

        if (signal.Name == "Battery - Pack Temp")
        {
            var index = signal.Dimensions.FirstOrDefault()?.Index;
            var suffix = index.HasValue ? $":{index.Value:D2}" : "";
            return signal with
            {
                Id = $"battery.module_temperature{suffix}",
                FamilyId = "battery.module_temperature",
                Name = "Battery Module Temperature",
                ShortName = "Batt Mod",
                Category = SignalCategory.Battery
            };
        }

        if (signal.ShortName is "Inst P (KW)" or "W/kph" or "W/mph")
        {
            var familyId = signal.ShortName switch
            {
                "Inst P (KW)" => "vehicle.power.instantaneous",
                "W/kph" => "vehicle.energy_per_distance.kph",
                "W/mph" => "vehicle.energy_per_distance.mph",
                _ => null
            };

            if (familyId != null)
            {
                return signal with
                {
                    Id = familyId,
                    FamilyId = familyId,
                    Category = SignalCategory.Vehicle
                };
            }
        }

        return signal;
    }
}
