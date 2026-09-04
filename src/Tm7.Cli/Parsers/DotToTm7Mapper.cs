namespace Tm7.Cli.Parsers;

public record EntityTypeMapping(string TypeId, string GenericTypeId);

public static class DotToTm7Mapper
{
    /// <summary>
    /// Determines the TM7 type and generic type for an entity based on its label and boundary membership.
    /// </summary>
    public static EntityTypeMapping MapEntityType(string id, string label, bool isInsideBoundary)
    {
        var upper = label.ToUpperInvariant();
        var idUpper = id.ToUpperInvariant();

        // Key Vault
        if (upper.Contains("KEY VAULT"))
            return new("SE.DS.TMCore.AzureKeyVault", "GE.DS");

        // Blob / Storage
        if (upper.Contains("BLOB") || upper.Contains("STORAGE"))
            return new("SE.DS.TMCore.AzureStorage", "GE.DS");

        // Redis
        if (upper.Contains("REDIS"))
            return new("SE.P.TMCore.AzureRedis", "GE.DS");

        // Postgres
        if (upper.Contains("POSTGRES"))
            return new("SE.DS.TMCore.AzurePostgresDB", "GE.DS");

        // Cosmos
        if (upper.Contains("COSMOS") || idUpper == "COSMOS")
            return new("SE.P.TMCore.AzureDocumentDB", "GE.DS");

        // SQL / generic database
        if (upper.Contains("GENERIC DATA STORE"))
            return new("GE.DS", "GE.DS");
        if (upper.Contains("AZURE SQL"))
            return new("SE.DS.TMCore.AzureSQLDB", "GE.DS");
        if (upper.Contains("DATABASE") || upper.Contains("DATA STORE"))
            return new("SE.DS.TMCore.SQL", "GE.DS");

        // Azure Data Explorer / Kusto / ADX
        if (upper.Contains("DATA EXPLORER") || upper.Contains("KUSTO") || upper.Contains("ADX"))
            return new("SE.P.TMCore.ADE", "GE.P");

        // App Insights - no specific TMT type, use generic process
        if (upper.Contains("APP INSIGHTS") || upper.Contains("APPLICATION INSIGHTS") || idUpper == "APPINSIGHTS")
            return new("GE.P", "GE.P");

        // Entra / Azure AD / AAD
        if (upper.Contains("ENTRA") || upper.Contains("AZURE AD") || upper.Contains("AAD") || idUpper == "AAD")
            return new("SE.P.TMCore.AzureAD", "GE.P");

        // Front Door
        if (upper.Contains("FRONT DOOR"))
            return new("GE.P", "GE.P");

        // Event Hub / Traffic Manager / Host
        if (upper.Contains("EVENT HUB"))
            return new("SE.P.TMCore.AzureEventHub", "GE.P");
        if (upper.Contains("TRAFFIC MANAGER"))
            return new("SE.P.TMCore.AzureTrafficManager", "GE.P");
        if (upper == "HOST")
            return new("SE.P.TMCore.Host", "GE.P");

        // Web App / Node.js
        if (upper.Contains("WEB APP") || upper.Contains("NODE.JS") || upper.Contains("NODEJS"))
            return new("SE.P.TMCore.AzureAppServiceWebApp", "GE.P");

        // cronjob / Web Job
        if (upper.Contains("CRONJOB") || upper.Contains("CRON JOB") || upper.Contains("WEB JOB") || upper.Contains("WEBJOB"))
            return new("SE.P.TMCore.AzureWebJob", "GE.P");

        // MS Graph / Directory
        if (upper.Contains("GRAPH") || upper.Contains("DIRECTORY"))
            return new("GE.P", "GE.P");

        // Browser
        if (upper.Contains("BROWSER"))
            return new("SE.EI.TMCore.Browser", "GE.EI");

        // External interactor if outside boundary
        if (!isInsideBoundary)
            return new("GE.EI", "GE.EI");

        // Default: generic process inside boundary
        return new("GE.P", "GE.P");
    }

    /// <summary>
    /// Maps a boundary to its TM7 type.
    /// </summary>
    public static EntityTypeMapping MapBoundaryType(string label)
    {
        if (label.ToUpperInvariant().Contains("AZURE"))
            return new("SE.TB.TMCore.AzureTrustBoundary", "GE.TB.B");
        return new("GE.TB.B", "GE.TB.B");
    }

}
