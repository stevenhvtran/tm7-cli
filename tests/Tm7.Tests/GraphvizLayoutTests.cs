using Tm7.Cli;
using Tm7.Cli.Layout;
using Tm7.Cli.Model;
using Tm7.Cli.Parsers;
using Xunit;

namespace Tm7.Tests;

public class GraphvizLayoutTests
{
    [Fact]
    public void ParsePlainOutput_ConvertsGraphvizCoordinatesToTm7Coordinates()
    {
        var layout = GraphvizLayoutEngine.ParsePlainOutput("""
            graph 1 4 2
            node "web_api" 1 1.5 1.5 0.8 "Web API" solid box black lightgrey
            node store 3 0.5 1.5 0.8 Store solid box black lightgrey
            edge web_api store 4 1.5 1.4 2 1.2 2.5 0.8 2.8 0.6 solid "#000001"
            stop
            """);

        var webApi = layout.GetModelBounds("web_api");
        var store = layout.GetModelBounds("store");

        Assert.Equal(new ModelNodeBounds(145, 130, 150, 80), webApi);
        Assert.Equal(new ModelNodeBounds(345, 230, 150, 80), store);
        var route = layout.GetModelRoute("#000001");
        Assert.True(route.Count > 4);
        Assert.Equal(new ModelRoutePoint(270, 180), route[0]);
        Assert.Equal(new ModelRoutePoint(400, 260), route[^1]);
    }

    [Fact]
    public void ParsePlainOutput_RejectsMissingGraphRecord()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            GraphvizLayoutEngine.ParsePlainOutput(
                "node api 1 1 1.5 0.8 API solid box black lightgrey"));

        Assert.Contains("graph record", exception.Message);
    }

    [Fact]
    public async Task Layout_RespectsExplicitTopToBottomRankDirection()
    {
        var dotPath = Path.Combine(Path.GetTempPath(), $"tm7-layout-{Guid.NewGuid():N}.dot");
        await File.WriteAllTextAsync(dotPath, """
            digraph G {
              rankdir=TB;
              first -> second;
            }
            """, TestContext.Current.CancellationToken);

        try
        {
            var layout = GraphvizLayoutEngine.Layout(dotPath);
            var first = layout.GetModelBounds("first");
            var second = layout.GetModelBounds("second");

            Assert.True(first.Top < second.Top);
            Assert.InRange(Math.Abs(first.Left - second.Left), 0, 5);
        }
        finally
        {
            if (File.Exists(dotPath)) File.Delete(dotPath);
        }
    }

    [Fact]
    public async Task ImportDot_UsesGraphvizRanksAndProducesReadableTerminalRender()
    {
        var dotPath = Path.Combine(Path.GetTempPath(), $"tm7-layout-{Guid.NewGuid():N}.dot");
        var outputPath = Path.Combine(Path.GetTempPath(), $"tm7-layout-{Guid.NewGuid():N}.tm7");
        await File.WriteAllTextAsync(dotPath, """
            digraph G {
              user [label="Browser"];
              subgraph cluster_azure {
                label="Azure Subscription";
                api [label="Web App"];
                store [label="Azure Storage"];
              }
              user -> api [label="HTTPS"];
              api -> store [label="Write"];
            }
            """, TestContext.Current.CancellationToken);

        try
        {
            var exitCode = await CommandFactory.CreateRootCommand()
                .Parse(["import", "dot", dotPath, "--output", outputPath])
                .InvokeAsync(null, TestContext.Current.CancellationToken);
            Assert.Equal(0, exitCode);

            var model = Tm7File.Load(outputPath);
            var surface = model.DrawingSurfaceList[0];
            var entities = surface.Borders.Values
                .OfType<SerializableBorder>()
                .Where(entity => entity is not SerializableBorderBoundary)
                .ToDictionary(EntityName, StringComparer.Ordinal);

            Assert.True(entities["Browser"].Left < entities["Web App"].Left);
            Assert.True(entities["Web App"].Left < entities["Azure Storage"].Left);
            AssertNoOverlap(entities.Values.ToList());

            var boundary = Assert.Single(surface.Borders.Values.OfType<SerializableBorderBoundary>());
            AssertContains(boundary, entities["Web App"]);
            AssertContains(boundary, entities["Azure Storage"]);

            var render = Tm7Renderer.Render(model, termWidth: 140, termHeight: 35, plain: true);
            Assert.Contains("Browser", render);
            Assert.Contains("Web App", render);
            Assert.Contains("Azure Storage", render);
            Assert.Contains("Azure Subscription", render);
        }
        finally
        {
            if (File.Exists(dotPath)) File.Delete(dotPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task ImportDot_PreservesExplicitTopToBottomRankDirection()
    {
        var dotPath = Path.Combine(Path.GetTempPath(), $"tm7-rankdir-{Guid.NewGuid():N}.dot");
        var outputPath = Path.Combine(Path.GetTempPath(), $"tm7-rankdir-{Guid.NewGuid():N}.tm7");
        await File.WriteAllTextAsync(dotPath, """
            digraph G {
              rankdir=TB;
              first [label="First"];
              second [label="Second"];
              third [label="Third"];
              first -> second;
              second -> third;
            }
            """, TestContext.Current.CancellationToken);

        try
        {
            var exitCode = await CommandFactory.CreateRootCommand()
                .Parse(["import", "dot", dotPath, "--output", outputPath])
                .InvokeAsync(null, TestContext.Current.CancellationToken);
            Assert.Equal(0, exitCode);

            var entities = Tm7File.Load(outputPath).DrawingSurfaceList[0].Borders.Values
                .OfType<SerializableBorder>()
                .ToDictionary(EntityName);
            Assert.True(entities["First"].Top < entities["Second"].Top);
            Assert.True(entities["Second"].Top < entities["Third"].Top);
            Assert.InRange(Math.Abs(entities["First"].Left - entities["Second"].Left), 0, 5);
        }
        finally
        {
            if (File.Exists(dotPath)) File.Delete(dotPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task ImportDot_PreservesNestedNamedClusterContainment()
    {
        var dotPath = Path.Combine(Path.GetTempPath(), $"tm7-clusters-{Guid.NewGuid():N}.dot");
        var outputPath = Path.Combine(Path.GetTempPath(), $"tm7-clusters-{Guid.NewGuid():N}.tm7");
        await File.WriteAllTextAsync(dotPath, """
            digraph G {
              subgraph cluster_outer {
                label="Outer boundary";
                outer [label="Outer service"];
                subgraph cluster_inner {
                  label="Inner boundary";
                  inner [label="Inner service"];
                }
                outer -> inner [label="Cross-cluster reference"];
              }
            }
            """, TestContext.Current.CancellationToken);

        try
        {
            var parsed = DotParser.Parse(dotPath);
            Assert.Equal("outer", Assert.Single(parsed.Entities, entity => entity.Label == "Outer service").BoundaryId);
            Assert.Equal("inner", Assert.Single(parsed.Entities, entity => entity.Label == "Inner service").BoundaryId);
            Assert.Equal("outer", Assert.Single(parsed.Boundaries, boundary => boundary.Id == "inner").ParentBoundaryId);
            Assert.Contains(
                "inner",
                Assert.Single(parsed.Boundaries, boundary => boundary.Id == "outer").ContainedEntityIds);

            var exitCode = await CommandFactory.CreateRootCommand()
                .Parse(["import", "dot", dotPath, "--output", outputPath])
                .InvokeAsync(null, TestContext.Current.CancellationToken);
            Assert.Equal(0, exitCode);

            var borders = Tm7File.Load(outputPath).DrawingSurfaceList[0].Borders.Values
                .OfType<SerializableBorder>()
                .ToList();
            var boundaries = borders
                .OfType<SerializableBorderBoundary>()
                .ToDictionary(EntityName);
            var entities = borders
                .Where(border => border is not SerializableBorderBoundary)
                .ToDictionary(EntityName);
            AssertContains(boundaries["Outer boundary"], boundaries["Inner boundary"]);
            AssertContains(boundaries["Outer boundary"], entities["Outer service"]);
            AssertContains(boundaries["Inner boundary"], entities["Inner service"]);
        }
        finally
        {
            if (File.Exists(dotPath)) File.Delete(dotPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    private static string EntityName(SerializableBorder border)
    {
        return border.Properties
            .OfType<SerializableDisplayAttribute>()
            .First(property => property.DisplayName == "Name")
            .Value?.ToString() ?? "";
    }

    private static void AssertNoOverlap(IReadOnlyList<SerializableBorder> entities)
    {
        for (var firstIndex = 0; firstIndex < entities.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < entities.Count; secondIndex++)
            {
                var first = entities[firstIndex];
                var second = entities[secondIndex];
                var overlaps =
                    first.Left < second.Left + second.Width &&
                    first.Left + first.Width > second.Left &&
                    first.Top < second.Top + second.Height &&
                    first.Top + first.Height > second.Top;
                Assert.False(overlaps, $"{EntityName(first)} overlaps {EntityName(second)}.");
            }
        }
    }

    private static void AssertContains(SerializableBorderBoundary boundary, SerializableBorder entity)
    {
        Assert.True(entity.Left >= boundary.Left);
        Assert.True(entity.Top >= boundary.Top);
        Assert.True(entity.Left + entity.Width <= boundary.Left + boundary.Width);
        Assert.True(entity.Top + entity.Height <= boundary.Top + boundary.Height);
    }
}
