using Tm7.Cli.Model;

namespace Tm7.Cli.Commands;

public static class CommandHelpers
{
    internal const string ParentBoundaryPropertyName = "Tm7.Cli.ParentBoundary";

    public static string? GetEntityName(SerializableTaggable entity)
    {
        if (entity.Properties is null) return null;
        foreach (var prop in entity.Properties)
        {
            if (prop is SerializableStringDisplayAttribute sda && sda.DisplayName == "Name")
                return sda.Value?.ToString();
        }
        return null;
    }

    public static string MapGenericTypeId(string? genericTypeId) => genericTypeId switch
    {
        "GE.P" => "Process",
        "GE.DS" => "Data Store",
        "GE.EI" => "External Interactor",
        "GE.TB.B" => "Trust Boundary",
        "GE.TB.LB" => "Trust Boundary Line",
        _ => genericTypeId ?? "(unknown)"
    };

    public static List<SerializableDisplayAttribute> CreateEntityProperties(string name)
    {
        // Clean up DOT escape sequences and raw IDs
        var cleanName = name.Replace("\\n", " ").Replace("\\l", " ").Trim();
        // Map common raw DOT IDs to readable names
        cleanName = cleanName switch
        {
            "appinsights" => "App Insights",
            "cosmos" => "Cosmos DB",
            _ => cleanName
        };
        return
        [
            new SerializableHeaderDisplayAttribute(cleanName),
            new SerializableStringDisplayAttribute("Name", null!, cleanName),
            new SerializableBooleanDisplayAttribute("Out Of Scope", "71f3d9aa-b8ef-4e54-8126-607a1d903103", false),
            new SerializableStringDisplayAttribute("Reason For Out Of Scope", "752473b6-52d4-4776-9a24-202153f7d579", "")
        ];
    }

    internal static void SetLayoutParent(
        SerializableTaggable entity,
        Guid parentBoundaryGuid)
    {
        entity.Properties.RemoveAll(property =>
            property is SerializableStringDisplayAttribute stringAttribute &&
            stringAttribute.Name == ParentBoundaryPropertyName);
        entity.Properties.Add(new SerializableStringDisplayAttribute(
            "",
            ParentBoundaryPropertyName,
            parentBoundaryGuid.ToString()));
    }

    internal static Guid? GetLayoutParent(SerializableTaggable entity)
    {
        var value = entity.Properties
            .OfType<SerializableStringDisplayAttribute>()
            .FirstOrDefault(property => property.Name == ParentBoundaryPropertyName)
            ?.Value
            ?.ToString();
        return Guid.TryParse(value, out var guid) ? guid : null;
    }

    internal static void ClearLayoutParent(SerializableTaggable entity)
    {
        entity.Properties.RemoveAll(property =>
            property is SerializableStringDisplayAttribute stringAttribute &&
            stringAttribute.Name == ParentBoundaryPropertyName);
    }

    public static List<SerializableDisplayAttribute> CreateFlowProperties(string name)
    {
        return
        [
            new SerializableHeaderDisplayAttribute(name),
            new SerializableStringDisplayAttribute("Name", null!, name),
            new SerializableStringDisplayAttribute("Dataflow Order", null!, "0"),
            new SerializableBooleanDisplayAttribute("Out Of Scope", "71f3d9aa-b8ef-4e54-8126-607a1d903103", false),
            new SerializableStringDisplayAttribute("Reason For Out Of Scope", "752473b6-52d4-4776-9a24-202153f7d579", "")
        ];
    }

    public static SerializableBorder CreateStencil(string genericTypeId, Guid guid, string typeId, List<SerializableDisplayAttribute> props, int left, int top, int width, int height)
    {
        return genericTypeId switch
        {
            "GE.P" => new SerializableStencilEllipse(guid, typeId, genericTypeId, props, left, top, width, height, 1.0, ""),
            "GE.DS" => new SerializableStencilParallelLines(guid, typeId, genericTypeId, props, left, top, width, height, 1.0, ""),
            "GE.EI" => new SerializableStencilRectangle(guid, typeId, genericTypeId, props, left, top, width, height, 1.0, ""),
            "GE.TB.B" => new SerializableBorderBoundary(guid, typeId, genericTypeId, props, left, top, width, height, 1.5, "4"),
            _ => throw new ArgumentException($"Unknown generic type id: {genericTypeId}")
        };
    }
}
