using System.Diagnostics;
using System.Runtime.InteropServices;
using Tm7.Cli.Model;
using Tm7.Cli;
using Xunit;

namespace Tm7.Tests;

/// <summary>
/// Integration tests that invoke the published NativeAOT executable to confirm that the
/// AOT round-trip works for cases the in-process JIT tests cannot reach (the JIT path
/// uses code-gen, the AOT path uses the reflection-based writer/reader). These tests
/// are skipped when the AOT publish output is not present.
/// </summary>
public class AotExeIntegrationTests
{
    private static string RepoRoot()
    {
        // bin/Tm7.Tests/debug/ -> up 4 = repo root
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    private static string SamplePath(string name) => Path.Combine(RepoRoot(), "samples", name);

    private static string? AotExePath()
    {
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "tm7.exe" : "tm7";
        // Check both publish layouts:
        //   release/                 -> `dotnet publish -c Release` (no explicit RID; the
        //                               CI workflow uses this layout).
        //   release_<RID>/           -> `dotnet publish -c Release -r <RID>`.
        var candidates = new[]
        {
            Path.Combine(RepoRoot(), "artifacts", "publish", "Tm7.Cli", "release", exeName),
            Path.Combine(RepoRoot(), "artifacts", "publish", "Tm7.Cli", $"release_{RuntimeInformation.RuntimeIdentifier}", exeName),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot(),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, stdout, stderr);
    }

    private static SerializableThreat MakeThreat(int id)
    {
        return new SerializableThreat(
            id: id,
            typeId: "ThreatType.GenericInformation",
            sourceGuid: Guid.NewGuid(),
            targetGuid: Guid.NewGuid(),
            flowGuid: Guid.NewGuid(),
            drawingSurfaceGuid: Guid.NewGuid(),
            state: ThreatState.NotApplicable,
            interactionKey: "k-" + id,
            priority: "High",
            wide: false,
            changedBy: "tester",
            modifiedAt: DateTime.UtcNow,
            upgraded: false,
            properties: new Dictionary<string, string>
            {
                ["Title"] = "T" + id,
                ["Description"] = "D" + id,
            });
    }

    [Fact]
    public void AotExe_Open_LoadsTemplate()
    {
        var exe = AotExePath();
        Assert.SkipUnless(exe is not null, "AOT publish output not present; run `dotnet publish src/Tm7.Cli -c Release -r win-x64` first.");

        var (code, stdout, stderr) = Run(exe!, "open", SamplePath("template.tm7"));
        Assert.True(code == 0, $"open exit={code} stderr={stderr}");
        Assert.Contains("Diagram 1", stdout);
    }

    [Fact]
    public void AotExe_AddEntity_RoundTripsAndPreservesExistingEntities()
    {
        var exe = AotExePath();
        Assert.SkipUnless(exe is not null, "AOT publish output not present; run `dotnet publish src/Tm7.Cli -c Release -r win-x64` first.");

        var work = Path.Combine(Path.GetTempPath(), $"tm7-aot-{Guid.NewGuid():N}.tm7");
        File.Copy(SamplePath("template.tm7"), work, overwrite: true);
        try
        {
            var (code, _, stderr) = Run(exe!,
                "add", "entity", work,
                "--name", "AotAdded",
                "--type-id", "StencilEllipse",
                "--generic-type-id", "GE.P");
            Assert.True(code == 0, $"add exit={code} stderr={stderr}");

            // Reload via JIT and assert structural preservation: original 17 borders + 1 new.
            var reloaded = Tm7File.Load(work);
            Assert.Equal(18, reloaded.DrawingSurfaceList[0].Borders.Count);
        }
        finally
        {
            File.Delete(work);
        }
    }

    [Fact]
    public void AotExe_PopulatedThreatsRoundTripThroughAddCommand()
    {
        // Most important AOT regression guard: JIT-produce a model with populated
        // AllThreatsDictionary + SerializableThreat.Properties, run the AOT exe to
        // load+modify+save it, then JIT-reload and verify the threats and properties
        // were preserved through the AOT write path (which uses the reflection writer
        // we had to refactor for AOT compatibility).
        var exe = AotExePath();
        Assert.SkipUnless(exe is not null, "AOT publish output not present; run `dotnet publish src/Tm7.Cli -c Release -r win-x64` first.");

        var work = Path.Combine(Path.GetTempPath(), $"tm7-aot-threats-{Guid.NewGuid():N}.tm7");
        var model = Tm7File.Load(SamplePath("template.tm7"));
        model.AllThreatsDictionary.Add("t1", MakeThreat(1));
        model.AllThreatsDictionary.Add("t2", MakeThreat(2));
        Tm7File.Save(model, work);

        try
        {
            // Sanity: JIT-written file is readable by JIT.
            var preCheck = Tm7File.Load(work);
            Assert.Equal(2, preCheck.AllThreatsDictionary.Count);

            // Run the AOT exe to load + modify + save the file.
            var (code, _, stderr) = Run(exe!,
                "add", "entity", work,
                "--name", "AotProbe",
                "--type-id", "StencilEllipse",
                "--generic-type-id", "GE.P");
            Assert.True(code == 0, $"AOT add failed exit={code} stderr={stderr}");

            // Reload via JIT and confirm AOT preserved the threats and their properties.
            var after = Tm7File.Load(work);
            Assert.Equal(2, after.AllThreatsDictionary.Count);
            Assert.True(after.AllThreatsDictionary.ContainsKey("t1"));
            Assert.True(after.AllThreatsDictionary.ContainsKey("t2"));
            var t1 = after.AllThreatsDictionary["t1"];
            Assert.Equal(1, t1.Id);
            Assert.Equal("T1", t1.Properties["Title"]);
            Assert.Equal("D1", t1.Properties["Description"]);
        }
        finally
        {
            File.Delete(work);
        }
    }

    [Fact]
    public void AotExe_NewWithoutTemplate_UsesBundledAzureKb()
    {
        // Guards the riskiest path of the embedded-default-template change: the
        // published NativeAOT exe must be able to read the EmbeddedResource via
        // Assembly.GetManifestResourceStream and deserialize it through the
        // reflection-based DataContractSerializer path.
        var exe = AotExePath();
        Assert.SkipUnless(exe is not null, "AOT publish output not present; run `dotnet publish src/Tm7.Cli -c Release -r win-x64` first.");

        var work = Path.Combine(Path.GetTempPath(), $"tm7-aot-newdef-{Guid.NewGuid():N}.tm7");
        try
        {
            var (code, stdout, stderr) = Run(exe!, "new", work, "--name", "AotDefaultTemplate");
            Assert.True(code == 0, $"new exit={code} stderr={stderr}");
            Assert.Contains("bundled default template", stdout);

            var reloaded = Tm7File.Load(work);
            Assert.Equal("AotDefaultTemplate", reloaded.MetaInformation.ThreatModelName);
            Assert.NotNull(reloaded.KnowledgeBase);
            Assert.NotNull(reloaded.KnowledgeBase.Manifest);
            Assert.Equal("Azure Threat Model Template", reloaded.KnowledgeBase.Manifest.Name);
            Assert.Single(reloaded.DrawingSurfaceList);
        }
        finally
        {
            if (File.Exists(work)) File.Delete(work);
        }
    }

    [Fact]
    public void AotExe_ImportDotWithoutTemplate_EmbedsAzureKbAndResolvesAllTypeIds()
    {
        // Two guarantees:
        //   1. AOT can load + serialize the embedded default template via `import dot`.
        //   2. Every TypeId emitted by the importer/mapper for a realistic input is
        //      defined by the bundled KB — i.e. opening the file in TMT will not
        //      trigger the "Unable to resolve a type" downgrade dialog.
        var exe = AotExePath();
        Assert.SkipUnless(exe is not null, "AOT publish output not present; run `dotnet publish src/Tm7.Cli -c Release -r win-x64` first.");

        var dotPath = Path.Combine(Path.GetTempPath(), $"tm7-aot-import-{Guid.NewGuid():N}.dot");
        var outPath = Path.Combine(Path.GetTempPath(), $"tm7-aot-import-{Guid.NewGuid():N}.tm7");
        // Inputs chosen so the mapper produces a representative mix of Azure TypeIds:
        // trust boundary, web app, key vault, browser external interactor, and
        // bidirectional flows (forward + reverse).
        File.WriteAllText(dotPath, """
            digraph G {
              user [label="Browser"];
              subgraph cluster_az {
                label="Azure Subscription";
                api [label="Web App"];
                kv  [label="Key Vault"];
              }
              user -> api [dir=both, label="HTTPS"];
              api -> kv [label="Get secret"];
            }
            """);
        try
        {
            var (code, stdout, stderr) = Run(exe!, "import", "dot", dotPath, "--output", outPath);
            Assert.True(code == 0, $"import exit={code} stdout={stdout} stderr={stderr}");
            Assert.Contains("bundled default template", stdout);

            var reloaded = Tm7File.Load(outPath);
            Assert.NotNull(reloaded.KnowledgeBase);
            Assert.Equal("Azure Threat Model Template", reloaded.KnowledgeBase.Manifest.Name);

            // Every TypeId actually written into the diagram must resolve to a stencil
            // defined in the embedded KB — otherwise TMT would downgrade the shape.
            var defined = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in reloaded.KnowledgeBase.StandardElements) defined.Add(el.Id);
            foreach (var el in reloaded.KnowledgeBase.GenericElements) defined.Add(el.Id);

            var surface = reloaded.DrawingSurfaceList[0];
            Assert.Contains(surface.Borders.Values.OfType<SerializableBorderBoundary>(),
                boundary => boundary.TypeId == "SE.TB.TMCore.AzureTrustBoundary");
            Assert.All(surface.Lines.Values.OfType<SerializableConnector>(), connector =>
            {
                Assert.NotEqual(StencilConnectionPort.None, connector.PortSource);
                Assert.NotEqual(StencilConnectionPort.None, connector.PortTarget);
                Assert.NotEqual((0, 0, 0, 0), (connector.X0, connector.Y0, connector.X1, connector.Y1));
            });
            var emittedTypeIds = surface.Borders.Values.OfType<SerializableTaggable>()
                .Concat(surface.Lines.Values.OfType<SerializableTaggable>())
                .SelectMany(t => new[] { t.TypeId, t.GenericTypeId })
                .Distinct(StringComparer.Ordinal)
                .ToList();
            Assert.NotEmpty(emittedTypeIds);
            var unresolved = emittedTypeIds.Where(id => !defined.Contains(id)).OrderBy(s => s).ToList();
            Assert.True(unresolved.Count == 0,
                $"Imported model uses TypeIds the bundled KB does not define: {string.Join(", ", unresolved)}.");
        }
        finally
        {
            if (File.Exists(dotPath)) File.Delete(dotPath);
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }
}
