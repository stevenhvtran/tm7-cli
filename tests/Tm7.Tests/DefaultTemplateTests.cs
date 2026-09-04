using System.Security.Cryptography;
using Tm7.Cli;
using Tm7.Cli.Commands;
using Tm7.Cli.Parsers;
using Xunit;

namespace Tm7.Tests;

/// <summary>
/// Guards the bundled default template that <c>tm7 new</c> and <c>tm7 import dot</c>
/// fall back to when <c>--template</c> is omitted. The embedded copy of
/// <c>samples/template.tm7</c> must:
///   1. Be present and deserializable.
///   2. Be byte-identical to the on-disk sample (no drift between the source-of-truth
///      and what gets baked into the binary).
///   3. Define every non-generic TypeId that <see cref="DotToTm7Mapper"/> can emit —
///      otherwise an <c>import dot</c> produced with the default template will trigger
///      TMT's "Unable to resolve a type" downgrade on load.
/// </summary>
public class DefaultTemplateTests
{
    private static string SamplePath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "samples", name));

    [Fact]
    public void LoadDefaultTemplate_Succeeds_AndExposesAzureKb()
    {
        var model = Tm7File.LoadDefaultTemplate();

        Assert.NotNull(model);
        Assert.NotNull(model.KnowledgeBase);
        Assert.NotNull(model.KnowledgeBase.Manifest);
        Assert.Equal("Azure Threat Model Template", model.KnowledgeBase.Manifest.Name);
        Assert.NotEmpty(model.KnowledgeBase.StandardElements);
    }

    [Fact]
    public void EmbeddedDefaultTemplate_MatchesSampleOnDisk()
    {
        // Catches drift between samples/template.tm7 and the EmbeddedResource baked
        // into the assembly (e.g., stale --no-build runs, wrong LogicalName, MSBuild
        // path change). Note: this only protects against drift at build time — once a
        // binary is published, edits to the on-disk sample require a rebuild to take
        // effect.
        var asm = typeof(Tm7File).Assembly;
        using var resourceStream = asm.GetManifestResourceStream("Tm7.Cli.Resources.DefaultTemplate.tm7");
        Assert.NotNull(resourceStream);

        using var sha = SHA256.Create();
        var embeddedHash = sha.ComputeHash(resourceStream!);

        using var fileStream = File.OpenRead(SamplePath("template.tm7"));
        var fileHash = sha.ComputeHash(fileStream);

        Assert.Equal(Convert.ToHexString(fileHash), Convert.ToHexString(embeddedHash));
    }

    /// <summary>
    /// Inputs exercise every recognized predicate/alias in
    /// <see cref="DotToTm7Mapper.MapEntityType"/> — including the branches that today
    /// return generic IDs (FRONT DOOR, GRAPH/DIRECTORY, APP INSIGHTS, outside-boundary
    /// external). Adding generic branches now means future mapper changes that swap a
    /// generic for a specific Azure TypeId will automatically be checked against the
    /// bundled KB.
    /// </summary>
    public static IEnumerable<object[]> MapperBranchInputs() => new[]
    {
        // (id, label, isInsideBoundary)
        new object[] { "kv1",        "Key Vault",                       true  },
        new object[] { "blob1",      "Blob",                            true  },
        new object[] { "storage1",   "Storage",                         true  },
        new object[] { "redis1",     "Redis",                           true  },
        new object[] { "pg1",        "Postgres DB",                     true  },
        new object[] { "sql1",       "Azure SQL Database",              true  },
        new object[] { "db1",        "Database",                        true  },
        new object[] { "gds1",       "Generic Data Store",              true  },
        new object[] { "cosmos1",    "Some Cosmos thing",               true  },
        new object[] { "COSMOS",     "cosmos",                          true  }, // matched by id
        new object[] { "adx1",       "Azure Data Explorer",             true  },
        new object[] { "kusto1",     "Kusto cluster",                   true  },
        new object[] { "adx2",       "ADX shard",                       true  },
        new object[] { "ai1",        "App Insights",                    true  },
        new object[] { "ai2",        "Application Insights",            true  },
        new object[] { "APPINSIGHTS","appinsights",                     true  }, // matched by id
        new object[] { "entra1",     "Entra ID",                        true  },
        new object[] { "aad1",       "Azure AD",                        true  },
        new object[] { "aad2",       "AAD service",                     true  },
        new object[] { "AAD",        "service",                         true  }, // matched by id
        new object[] { "fd1",        "Front Door",                      true  },
        new object[] { "eh1",        "Event Hub",                       true  },
        new object[] { "tm1",        "Traffic Manager",                 true  },
        new object[] { "host1",      "Host",                            true  },
        new object[] { "wa1",        "Web App",                         true  },
        new object[] { "wa2",        "Node.js api",                     true  },
        new object[] { "wa3",        "NodeJS api",                      true  },
        new object[] { "wj1",        "Cronjob runner",                  true  },
        new object[] { "wj2",        "Cron job runner",                 true  },
        new object[] { "wj3",        "Web Job runner",                  true  },
        new object[] { "wj4",        "Webjob runner",                   true  },
        new object[] { "graph1",     "MS Graph",                        true  },
        new object[] { "dir1",       "Directory service",               true  },
        new object[] { "br1",        "Browser",                         true  },
        new object[] { "ext1",       "Random external thing",           false },
        new object[] { "generic1",   "Some inside-boundary thing",      true  },
    };

    [Fact]
    public void DefaultTemplate_DefinesEveryTypeIdEmittedByDotMapper()
    {
        var model = Tm7File.LoadDefaultTemplate();
        var definedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in model.KnowledgeBase.StandardElements) definedIds.Add(el.Id);
        foreach (var el in model.KnowledgeBase.GenericElements) definedIds.Add(el.Id);

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in MapperBranchInputs())
        {
            var id = (string)row[0];
            var label = (string)row[1];
            var inside = (bool)row[2];
            var m = DotToTm7Mapper.MapEntityType(id, label, inside);
            emitted.Add(m.TypeId);
            emitted.Add(m.GenericTypeId);
        }

        // Boundary mapper — both branches (Azure-tagged and generic).
        var azureBoundary = DotToTm7Mapper.MapBoundaryType("Azure subscription");
        emitted.Add(azureBoundary.TypeId);
        emitted.Add(azureBoundary.GenericTypeId);
        var nonAzureBoundary = DotToTm7Mapper.MapBoundaryType("Corp trust");
        emitted.Add(nonAzureBoundary.TypeId);
        emitted.Add(nonAzureBoundary.GenericTypeId);

        // Flow TypeIds — pulled from the production constants in ImportCommand
        // (rather than literals) so a typo in production is caught by this test.
        emitted.Add(ImportCommand.ForwardFlowTypeId);
        emitted.Add(ImportCommand.ReverseFlowTypeId);
        emitted.Add(ImportCommand.GenericFlowTypeId);

        var missing = emitted.Where(id => !definedIds.Contains(id)).OrderBy(s => s).ToList();
        Assert.True(missing.Count == 0,
            $"Default template is missing TypeIds emitted by DotToTm7Mapper / ImportCommand: {string.Join(", ", missing)}. " +
            "Either add the stencils to samples/template.tm7 or update the mapper.");
    }
}
