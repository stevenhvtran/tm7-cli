using Tm7.Cli;
using Tm7.Cli.Commands;
using Tm7.Cli.Layout;
using Tm7.Cli.Model;
using Xunit;

namespace Tm7.Tests;

[CollectionDefinition("Console serial", DisableParallelization = true)]
public sealed class ConsoleSerialCollection;

[Collection("Console serial")]
public class AutomaticGraphvizLayoutTests
{
    [Fact]
    public void Apply_RanksEntitiesAndPreservesNestedBoundaries()
    {
        var model = CreateNestedBoundaryModel();
        var surface = model.DrawingSurfaceList[0];
        var entities = surface.Borders.Values
            .OfType<SerializableBorder>()
            .Where(border => border is not SerializableBorderBoundary)
            .ToDictionary(EntityName);

        Tm7GraphvizLayout.Apply(model);

        Assert.True(entities["User"].Left < entities["API"].Left);
        Assert.True(entities["API"].Left < entities["Database"].Left);
        AssertNoOverlap(entities.Values.ToList());

        var boundaries = surface.Borders.Values
            .OfType<SerializableBorderBoundary>()
            .ToDictionary(EntityName);
        AssertContains(boundaries["Application"], entities["API"]);
        AssertContains(boundaries["Data tier"], entities["Database"]);
        AssertContains(boundaries["Application"], boundaries["Data tier"]);

        foreach (var connector in surface.Lines.Values.OfType<SerializableConnector>())
        {
            Assert.NotEqual(0, connector.X0);
            Assert.NotEqual(0, connector.X1);
        }
    }

    [Fact]
    public async Task AddEntity_RequiresNoCoordinatesAndSupportsBoundaryMembership()
    {
        var modelPath = Path.Combine(Path.GetTempPath(), $"tm7-auto-layout-{Guid.NewGuid():N}.tm7");
        try
        {
            Assert.Equal(0, await Invoke("new", modelPath, "--name", "Automatic layout"));
            Assert.Equal(0, await Invoke(
                "add", "entity", modelPath,
                "--name", "Azure",
                "--type-id", "SE.TB.TMCore.AzureTrustBoundary",
                "--generic-type-id", "GE.TB.B"));

            var model = Tm7File.Load(modelPath);
            var boundary = Assert.Single(
                model.DrawingSurfaceList[0].Borders.Values.OfType<SerializableBorderBoundary>());

            Assert.Equal(0, await Invoke(
                "add", "entity", modelPath,
                "--name", "Web API",
                "--type-id", "SE.P.TMCore.AzureAppServiceWebApp",
                "--generic-type-id", "GE.P",
                "--boundary", boundary.Guid.ToString()));

            model = Tm7File.Load(modelPath);
            boundary = Assert.Single(
                model.DrawingSurfaceList[0].Borders.Values.OfType<SerializableBorderBoundary>());
            var api = Assert.Single(
                model.DrawingSurfaceList[0].Borders.Values.OfType<SerializableBorder>(),
                border => border is not SerializableBorderBoundary);
            AssertContains(boundary, api);
        }
        finally
        {
            if (File.Exists(modelPath)) File.Delete(modelPath);
        }
    }

    [Fact]
    public async Task AddWithoutLayout_AllowsBatchConstructionBeforeSingleLayout()
    {
        var modelPath = Path.Combine(Path.GetTempPath(), $"tm7-batch-layout-{Guid.NewGuid():N}.tm7");
        try
        {
            Assert.Equal(0, await Invoke("new", modelPath, "--name", "Batch layout"));
            Assert.Equal(0, await Invoke(
                "add", "entity", modelPath,
                "--name", "Batch boundary",
                "--type-id", "GE.TB.B",
                "--generic-type-id", "GE.TB.B",
                "--no-layout"));
            var model = Tm7File.Load(modelPath);
            var boundary = Assert.Single(
                model.DrawingSurfaceList[0].Borders.Values.OfType<SerializableBorderBoundary>());

            for (var index = 0; index < 22; index++)
            {
                Assert.Equal(0, await Invoke(
                    "add", "entity", modelPath,
                    "--name", $"Batch entity {index}",
                    "--type-id", "GE.P",
                    "--generic-type-id", "GE.P",
                    "--boundary", boundary.Guid.ToString(),
                    "--no-layout"));
            }

            model = Tm7File.Load(modelPath);
            var entities = model.DrawingSurfaceList[0].Borders.Values
                .OfType<SerializableBorder>()
                .Where(border => border is not SerializableBorderBoundary)
                .ToList();
            Assert.Equal(22, entities.Count);
            for (var index = 0; index < entities.Count - 1; index++)
            {
                Assert.Equal(0, await Invoke(
                    "add", "flow", modelPath,
                    "--name", $"Batch flow {index}",
                    "--source", entities[index].Guid.ToString(),
                    "--target", entities[index + 1].Guid.ToString(),
                    "--no-layout"));
            }

            Assert.Equal(0, await Invoke("layout", modelPath));
            model = Tm7File.Load(modelPath);
            Assert.Equal(23, model.DrawingSurfaceList[0].Borders.Count);
            Assert.Equal(21, model.DrawingSurfaceList[0].Lines.Count);
            Assert.All(model.DrawingSurfaceList[0].Borders.Values.OfType<SerializableBorder>(), border =>
            {
                Assert.InRange(border.Left, 1, 1450);
                Assert.InRange(border.Top, 1, 1450);
                Assert.InRange(border.Left + border.Width, 1, 1450);
                Assert.InRange(border.Top + border.Height, 1, 1450);
            });
        }
        finally
        {
            if (File.Exists(modelPath)) File.Delete(modelPath);
        }
    }

    [Fact]
    public async Task AddSurface_CreatesAnotherDiagramForBatchPopulation()
    {
        var modelPath = Path.Combine(Path.GetTempPath(), $"tm7-surface-{Guid.NewGuid():N}.tm7");
        try
        {
            Assert.Equal(0, await Invoke("new", modelPath, "--name", "Multiple surfaces"));
            Assert.Equal(0, await Invoke("add", "surface", modelPath, "--name", "Runtime"));

            var model = Tm7File.Load(modelPath);
            Assert.Equal(2, model.DrawingSurfaceList.Count);
            Assert.Equal("Runtime", model.DrawingSurfaceList[1].Header);
        }
        finally
        {
            if (File.Exists(modelPath)) File.Delete(modelPath);
        }
    }

    [Fact]
    public async Task DeferredLayout_PreservesExplicitMembershipAcrossMultipleBoundaries()
    {
        var modelPath = Path.Combine(Path.GetTempPath(), $"tm7-boundaries-{Guid.NewGuid():N}.tm7");
        try
        {
            Assert.Equal(0, await Invoke("new", modelPath, "--name", "Boundary membership"));
            Assert.Equal(0, await Invoke("add", "surface", modelPath, "--name", "Credentials"));
            var boundaryNames = new[]
            {
                "Callers",
                "Broker",
                "Stores",
                "Dependencies",
            };
            foreach (var boundaryName in boundaryNames)
            {
                Assert.Equal(0, await Invoke(
                    "add", "entity", modelPath,
                    "--surface", "1",
                    "--name", boundaryName,
                    "--type-id", "GE.TB.B",
                    "--generic-type-id", "GE.TB.B",
                    "--no-layout"));
            }

            var model = Tm7File.Load(modelPath);
            var boundaries = model.DrawingSurfaceList[1].Borders.Values
                .OfType<SerializableBorderBoundary>()
                .ToDictionary(EntityName);
            foreach (var boundaryName in boundaryNames)
            {
                Assert.Equal(0, await Invoke(
                    "add", "entity", modelPath,
                    "--surface", "1",
                    "--name", $"{boundaryName} entity",
                    "--type-id", "GE.P",
                    "--generic-type-id", "GE.P",
                    "--boundary", boundaries[boundaryName].Guid.ToString(),
                    "--no-layout"));
            }

            Assert.Equal(0, await Invoke("layout", modelPath));
            model = Tm7File.Load(modelPath);
            var surface = model.DrawingSurfaceList[1];
            boundaries = surface.Borders.Values
                .OfType<SerializableBorderBoundary>()
                .ToDictionary(EntityName);
            var entities = surface.Borders.Values
                .OfType<SerializableBorder>()
                .Where(border => border is not SerializableBorderBoundary)
                .ToDictionary(EntityName);

            foreach (var boundaryName in boundaryNames)
            {
                var entity = entities[$"{boundaryName} entity"];
                AssertContains(boundaries[boundaryName], entity);
                var readableWidth = Math.Clamp(
                    (int)Math.Round(EntityName(entity).Length * 0.065 * 100),
                    100,
                    240);
                Assert.True(entity.Width >= readableWidth);
                Assert.Null(CommandHelpers.GetLayoutParent(entity));
            }
            Assert.All(boundaries.Values, boundary =>
                Assert.Null(CommandHelpers.GetLayoutParent(boundary)));
            AssertNoOverlap(boundaries.Values.Cast<SerializableBorder>().ToList());
        }
        finally
        {
            if (File.Exists(modelPath)) File.Delete(modelPath);
        }
    }

    [Fact]
    public void Apply_RoutesDirectAndParallelFlowsAroundAnObstacle()
    {
        var template = Tm7File.LoadDefaultTemplate();
        var source = CreateEntity("Source", "GE.P", 100, 100);
        var obstacle = CreateEntity("Obstacle", "GE.P", 400, 100);
        var target = CreateEntity("Target", "GE.P", 700, 100);
        var sourceToObstacle = CreateConnector(source, obstacle);
        var obstacleToTarget = CreateConnector(obstacle, target);
        var directOne = CreateConnector(source, target);
        var directTwo = CreateConnector(source, target);
        var reverse = CreateConnector(target, source);
        var selfLoop = CreateConnector(obstacle, obstacle);
        var surface = new SerializableDrawingSurfaceModel(
            Guid.NewGuid(),
            "",
            "",
            Array.Empty<SerializableDisplayAttribute>(),
            [source, obstacle, target],
            [sourceToObstacle, obstacleToTarget, directOne, directTwo, reverse, selfLoop],
            80,
            "Routing");
        var model = new SerializableModelData(
            [surface],
            new SerializableMetaInformation("Routing", "", "", "", "", "", ""),
            Array.Empty<SerializableNote>(),
            new Dictionary<string, SerializableThreat>(),
            true,
            Array.Empty<SerializableValidation>(),
            template.Version,
            template.KnowledgeBase,
            template.Profile);

        var layout = Tm7GraphvizLayout.Apply(model);
        var routes = layout.GetRoutes(surface.Guid);

        Assert.True(source.Left < obstacle.Left);
        Assert.True(obstacle.Left < target.Left);
        AssertRouteAvoids(routes[directOne.Guid], obstacle);
        AssertRouteAvoids(routes[directTwo.Guid], obstacle);
        Assert.NotEqual(
            string.Join(";", routes[directOne.Guid]),
            string.Join(";", routes[directTwo.Guid]));
        Assert.NotEqual(
            (directOne.X0, directOne.Y0, directOne.X1, directOne.Y1, directOne.MPX, directOne.MPY),
            (directTwo.X0, directTwo.Y0, directTwo.X1, directTwo.Y1, directTwo.MPX, directTwo.MPY));
        Assert.NotEqual(
            (directOne.X0, directOne.Y0, directOne.X1, directOne.Y1, directOne.MPX, directOne.MPY),
            (reverse.X1, reverse.Y1, reverse.X0, reverse.Y0, reverse.MPX, reverse.MPY));
        Assert.InRange(HandleOffset(directOne), 0, 301);
        Assert.InRange(HandleOffset(directTwo), 0, 301);
        Assert.InRange(HandleOffset(reverse), 0, 301);
        Assert.NotEqual(StencilConnectionPort.None, directOne.PortSource);
        Assert.NotEqual(StencilConnectionPort.None, directOne.PortTarget);
        Assert.NotEqual((selfLoop.X0, selfLoop.Y0), (selfLoop.X1, selfLoop.Y1));
        Assert.NotEqual(
            ((selfLoop.X0 + selfLoop.X1) / 2, (selfLoop.Y0 + selfLoop.Y1) / 2),
            (selfLoop.MPX, selfLoop.MPY));
        Assert.NotEqual(StencilConnectionPort.None, selfLoop.PortSource);
        Assert.NotEqual(StencilConnectionPort.None, selfLoop.PortTarget);
        foreach (var (connector, connectorSource, connectorTarget) in new[]
                 {
                     (sourceToObstacle, source, obstacle),
                     (obstacleToTarget, obstacle, target),
                     (directOne, source, target),
                     (directTwo, source, target),
                     (reverse, target, source),
                 })
        {
            AssertEndpointFacesHandle(
                connectorSource,
                connector.X0,
                connector.Y0,
                connector.MPX,
                connector.MPY,
                connector.PortSource);
            AssertEndpointFacesHandle(
                connectorTarget,
                connector.X1,
                connector.Y1,
                connector.MPX,
                connector.MPY,
                connector.PortTarget);
        }
        var sourceEndpoints = new[] { sourceToObstacle, directOne, directTwo }
            .Select(connector => (connector.X0, connector.Y0))
            .ToList();
        Assert.Equal(sourceEndpoints.Count, sourceEndpoints.Distinct().Count());

        var render = Tm7Renderer.Render(model, 140, 35, true, routes);
        Assert.Contains("Obstacle", render);
        Assert.Contains('►', render);
    }

    [Fact]
    public void Apply_SpreadsHubLabelsAwayFromNodesAndOtherLabels()
    {
        var template = Tm7File.LoadDefaultTemplate();
        var hub = CreateEntity("Workflow hub", "GE.P", 100, 400);
        var targets = Enumerable.Range(0, 6)
            .Select(index => CreateEntity($"Dependency {index}", "GE.DS", 700, 100 + index * 130))
            .ToList();
        var connectors = targets
            .Select((target, index) => CreateConnector(
                hub,
                target,
                $"Long workflow dependency label {index}"))
            .ToList();
        var surface = new SerializableDrawingSurfaceModel(
            Guid.NewGuid(),
            "",
            "",
            Array.Empty<SerializableDisplayAttribute>(),
            [hub, .. targets],
            connectors,
            80,
            "Hub routing");
        var model = new SerializableModelData(
            [surface],
            new SerializableMetaInformation("Hub routing", "", "", "", "", "", ""),
            Array.Empty<SerializableNote>(),
            new Dictionary<string, SerializableThreat>(),
            true,
            Array.Empty<SerializableValidation>(),
            template.Version,
            template.KnowledgeBase,
            template.Profile);

        Tm7GraphvizLayout.Apply(model);

        var endpointCoordinates = connectors
            .Select(connector => (connector.X0, connector.Y0))
            .ToList();
        Assert.Equal(endpointCoordinates.Count, endpointCoordinates.Distinct().Count());
        var labelRectangles = connectors
            .Select(ConnectorLabelRectangle)
            .ToList();
        for (var firstIndex = 0; firstIndex < labelRectangles.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < labelRectangles.Count; secondIndex++)
                Assert.False(Overlaps(labelRectangles[firstIndex], labelRectangles[secondIndex]));
        }
        Assert.All(labelRectangles, label =>
        {
            Assert.False(Overlaps(label, BorderRectangle(hub)));
            Assert.All(targets, target => Assert.False(Overlaps(label, BorderRectangle(target))));
        });
    }

    [Fact]
    public void Apply_GloballyRanksConnectedRootBoundariesWithoutOverlap()
    {
        var template = Tm7File.LoadDefaultTemplate();
        var source = CreateEntity("Source service", "GE.P", 150, 150);
        var target = CreateEntity("Target store", "GE.DS", 850, 150);
        var sourceBoundary = CreateBoundary("Source boundary", 100, 100, 300, 250);
        var targetBoundary = CreateBoundary("Target boundary", 800, 100, 300, 250);
        var connector = CreateConnector(source, target);
        var surface = new SerializableDrawingSurfaceModel(
            Guid.NewGuid(),
            "",
            "",
            Array.Empty<SerializableDisplayAttribute>(),
            [source, target, sourceBoundary, targetBoundary],
            [connector],
            80,
            "Boundary routing");
        var model = new SerializableModelData(
            [surface],
            new SerializableMetaInformation("Boundary routing", "", "", "", "", "", ""),
            Array.Empty<SerializableNote>(),
            new Dictionary<string, SerializableThreat>(),
            true,
            Array.Empty<SerializableValidation>(),
            template.Version,
            template.KnowledgeBase,
            template.Profile);

        Tm7GraphvizLayout.Apply(model);

        Assert.True(source.Left < target.Left);
        Assert.False(Overlaps(sourceBoundary, targetBoundary));
        AssertContains(sourceBoundary, source);
        AssertContains(targetBoundary, target);
    }

    [Fact]
    public void Apply_ScalesWideLayoutsIntoTheTm7CoordinateRange()
    {
        var template = Tm7File.LoadDefaultTemplate();
        var entities = Enumerable.Range(0, 12)
            .Select(index => CreateEntity($"Long service name {index}", "GE.P", 100 + index * 250, 150))
            .ToList();
        var connectors = entities
            .Zip(entities.Skip(1), (source, target) => CreateConnector(source, target))
            .ToList();
        var boundary = CreateBoundary("Wide boundary", 50, 50, 3300, 300);
        var surface = new SerializableDrawingSurfaceModel(
            Guid.NewGuid(),
            "",
            "",
            Array.Empty<SerializableDisplayAttribute>(),
            [.. entities, boundary],
            connectors,
            80,
            "Wide layout");
        var model = new SerializableModelData(
            [surface],
            new SerializableMetaInformation("Wide layout", "", "", "", "", "", ""),
            Array.Empty<SerializableNote>(),
            new Dictionary<string, SerializableThreat>(),
            true,
            Array.Empty<SerializableValidation>(),
            template.Version,
            template.KnowledgeBase,
            template.Profile);

        Tm7GraphvizLayout.Apply(model);

        Assert.All(surface.Borders.Values.OfType<SerializableBorder>(), border =>
        {
            Assert.InRange(border.Left, 1, 1450);
            Assert.InRange(border.Top, 1, 1450);
            Assert.InRange(border.Left + border.Width, 1, 1450);
            Assert.InRange(border.Top + border.Height, 1, 1450);
        });
        Assert.All(surface.Lines.Values.OfType<SerializableLine>(), line =>
        {
            Assert.InRange(line.X0, 1, 1450);
            Assert.InRange(line.Y0, 1, 1450);
            Assert.InRange(line.X1, 1, 1450);
            Assert.InRange(line.Y1, 1, 1450);
            Assert.InRange(line.MPX, 1, 1450);
            Assert.InRange(line.MPY, 1, 1450);
        });
    }

    [Fact]
    public void Apply_FitsDenseRuntimeTopologyWithoutDroppingFlowsOrOverlaps()
    {
        var template = Tm7File.LoadDefaultTemplate();
        var boundaries = Enumerable.Range(0, 4)
            .Select(index => CreateBoundary($"Runtime boundary {index}", 20 + index % 3 * 460, 20 + index / 3 * 390, 420, 340))
            .ToList();
        var entities = Enumerable.Range(0, 18)
            .Select(index =>
            {
                var entity = CreateEntity($"Runtime service {index}", "GE.P", 50 + index % 3 * 85, 75 + index / 3 * 65);
                CommandHelpers.SetLayoutParent(entity, boundaries[index % boundaries.Count].Guid);
                return entity;
            })
            .ToList();
        var connectors = new List<SerializableConnector>();
        for (var index = 0; index < entities.Count - 1; index++)
            connectors.Add(CreateConnector(entities[index], entities[index + 1], $"HTTPS / category {index}"));
        for (var index = 1; connectors.Count < 32; index++)
            connectors.Add(CreateConnector(entities[0], entities[index % entities.Count], $"Service Bus / category {index}"));
        var surface = new SerializableDrawingSurfaceModel(
            Guid.NewGuid(),
            "",
            "",
            Array.Empty<SerializableDisplayAttribute>(),
            [.. entities, .. boundaries],
            connectors,
            80,
            "Runtime");
        var model = new SerializableModelData(
            [surface],
            new SerializableMetaInformation("Dense runtime", "", "", "", "", "", ""),
            Array.Empty<SerializableNote>(),
            new Dictionary<string, SerializableThreat>(),
            true,
            Array.Empty<SerializableValidation>(),
            template.Version,
            template.KnowledgeBase,
            template.Profile);

        Tm7GraphvizLayout.Apply(model);

        Assert.Equal(32, surface.Lines.Count);
        AssertNoOverlap(entities);
        AssertNoOverlap(boundaries.Cast<SerializableBorder>().ToList());
        var entityRectangles = entities.Select(BorderRectangle).ToList();
        var labelRectangles = connectors.Select(ConnectorLabelRectangle).ToList();
        for (var firstIndex = 0; firstIndex < labelRectangles.Count; firstIndex++)
        {
            Assert.All(entityRectangles, entity =>
                Assert.False(Overlaps(labelRectangles[firstIndex], entity)));
            for (var secondIndex = firstIndex + 1; secondIndex < labelRectangles.Count; secondIndex++)
            {
                Assert.False(
                    Overlaps(labelRectangles[firstIndex], labelRectangles[secondIndex]),
                    $"Flow labels {firstIndex} ({CommandHelpers.GetEntityName(connectors[firstIndex])}, {labelRectangles[firstIndex]}) " +
                    $"and {secondIndex} ({CommandHelpers.GetEntityName(connectors[secondIndex])}, {labelRectangles[secondIndex]}) overlap.");
            }
        }
        Assert.All(surface.Borders.Values.OfType<SerializableBorder>(), border =>
        {
            Assert.InRange(border.Left, 1, 1450);
            Assert.InRange(border.Top, 1, 1450);
            Assert.InRange(border.Left + border.Width, 1, 1450);
            Assert.InRange(border.Top + border.Height, 1, 1450);
            Assert.Null(CommandHelpers.GetLayoutParent(border));
        });
    }

    [Fact]
    public void Apply_RemapsLineBoundaryWithTheEntityCoordinateFrame()
    {
        var template = Tm7File.LoadDefaultTemplate();
        var source = CreateEntity("Source", "GE.P", 100, 100);
        var target = CreateEntity("Target", "GE.P", 900, 500);
        var connector = CreateConnector(source, target);
        var lineBoundary = new SerializableLineBoundary(
            Guid.NewGuid(),
            "GE.TB.L",
            "GE.TB.L",
            CommandHelpers.CreateEntityProperties("Internet boundary"),
            Guid.Empty,
            Guid.Empty,
            StencilConnectionPort.None,
            StencilConnectionPort.None,
            500,
            50,
            500,
            700,
            500,
            375,
            1,
            "");
        var surface = new SerializableDrawingSurfaceModel(
            Guid.NewGuid(),
            "",
            "",
            Array.Empty<SerializableDisplayAttribute>(),
            [source, target],
            [connector, lineBoundary],
            80,
            "Line boundary");
        var model = new SerializableModelData(
            [surface],
            new SerializableMetaInformation("Line boundary", "", "", "", "", "", ""),
            Array.Empty<SerializableNote>(),
            new Dictionary<string, SerializableThreat>(),
            true,
            Array.Empty<SerializableValidation>(),
            template.Version,
            template.KnowledgeBase,
            template.Profile);

        Tm7GraphvizLayout.Apply(model);

        Assert.NotEqual((500, 50, 500, 700), (lineBoundary.X0, lineBoundary.Y0, lineBoundary.X1, lineBoundary.Y1));
        Assert.Equal(StencilConnectionPort.None, lineBoundary.PortSource);
        Assert.Equal(StencilConnectionPort.None, lineBoundary.PortTarget);
        Assert.InRange(lineBoundary.X0, 1, 1450);
        Assert.InRange(lineBoundary.Y0, 1, 1450);
        Assert.InRange(lineBoundary.X1, 1, 1450);
        Assert.InRange(lineBoundary.Y1, 1, 1450);
        Assert.InRange(lineBoundary.MPX, 1, 1450);
        Assert.InRange(lineBoundary.MPY, 1, 1450);
        Assert.InRange(Math.Abs(lineBoundary.X0 - lineBoundary.X1), 0, 1);
    }

    [Fact]
    public async Task ListEntities_DoesNotInvokeGraphvizLayout()
    {
        var modelPath = Path.Combine(Path.GetTempPath(), $"tm7-list-{Guid.NewGuid():N}.tm7");
        var model = CreateNestedBoundaryModel();
        var entity = model.DrawingSurfaceList[0].Borders.Values
            .OfType<SerializableBorder>()
            .First(border => border is not SerializableBorderBoundary);
        entity.SetBounds(1234, 1111, entity.Width, entity.Height);
        Tm7File.Save(model, modelPath);

        var originalGraphvizPath = Environment.GetEnvironmentVariable(
            GraphvizLayoutEngine.ExecutableEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                GraphvizLayoutEngine.ExecutableEnvironmentVariable,
                Path.Combine(Path.GetTempPath(), $"missing-dot-{Guid.NewGuid():N}"));
            var exitCode = await Invoke("list", "entities", modelPath);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                GraphvizLayoutEngine.ExecutableEnvironmentVariable,
                originalGraphvizPath);
            if (File.Exists(modelPath)) File.Delete(modelPath);
        }
    }

    private static async Task<int> Invoke(params string[] arguments)
    {
        return await CommandFactory.CreateRootCommand()
            .Parse(arguments)
            .InvokeAsync(null, TestContext.Current.CancellationToken);
    }

    private static SerializableModelData CreateNestedBoundaryModel()
    {
        var template = Tm7File.LoadDefaultTemplate();
        var user = CreateEntity("User", "GE.EI", 900, 100);
        var api = CreateEntity("API", "GE.P", 200, 150);
        var database = CreateEntity("Database", "GE.DS", 500, 200);
        var outer = CreateBoundary("Application", 100, 80, 700, 500);
        var inner = CreateBoundary("Data tier", 400, 150, 250, 250);
        var userToApi = CreateConnector(user, api);
        var apiToDatabase = CreateConnector(api, database);
        var surface = new SerializableDrawingSurfaceModel(
            Guid.NewGuid(),
            "",
            "",
            Array.Empty<SerializableDisplayAttribute>(),
            [user, api, database, outer, inner],
            [userToApi, apiToDatabase],
            80,
            "Diagram 1");

        return new SerializableModelData(
            [surface],
            new SerializableMetaInformation("Nested model", "", "", "", "", "", ""),
            Array.Empty<SerializableNote>(),
            new Dictionary<string, SerializableThreat>(),
            true,
            Array.Empty<SerializableValidation>(),
            template.Version,
            template.KnowledgeBase,
            template.Profile);
    }

    private static SerializableBorder CreateEntity(string name, string genericTypeId, int left, int top)
    {
        return CommandHelpers.CreateStencil(
            genericTypeId,
            Guid.NewGuid(),
            genericTypeId,
            CommandHelpers.CreateEntityProperties(name),
            left,
            top,
            150,
            80);
    }

    private static SerializableBorderBoundary CreateBoundary(
        string name,
        int left,
        int top,
        int width,
        int height)
    {
        return (SerializableBorderBoundary)CommandHelpers.CreateStencil(
            "GE.TB.B",
            Guid.NewGuid(),
            "GE.TB.B",
            CommandHelpers.CreateEntityProperties(name),
            left,
            top,
            width,
            height);
    }

    private static SerializableConnector CreateConnector(
        SerializableBorder source,
        SerializableBorder target,
        string name = "Flow")
    {
        return new SerializableConnector(
            Guid.NewGuid(),
            "SE.DF.TMCore.Request",
            "GE.DF",
            CommandHelpers.CreateFlowProperties(name),
            target.Guid,
            source.Guid,
            StencilConnectionPort.None,
            StencilConnectionPort.None,
            0,
            0,
            0,
            0,
            0,
            0,
            1,
            "");
    }

    private static string EntityName(SerializableBorder border)
    {
        return CommandHelpers.GetEntityName(border) ?? "";
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

    private static bool Overlaps(SerializableBorder first, SerializableBorder second)
    {
        return
            first.Left < second.Left + second.Width &&
            first.Left + first.Width > second.Left &&
            first.Top < second.Top + second.Height &&
            first.Top + first.Height > second.Top;
    }

    private static TestRectangle ConnectorLabelRectangle(SerializableConnector connector)
    {
        var name = CommandHelpers.GetEntityName(connector) ?? "Flow";
        var width = Math.Clamp(name.Length * 6 + 24, 80, 320);
        return new TestRectangle(
            connector.MPX - width / 2.0,
            connector.MPY - 17,
            width,
            34);
    }

    private static TestRectangle BorderRectangle(SerializableBorder border)
    {
        return new TestRectangle(border.Left, border.Top, border.Width, border.Height);
    }

    private static bool Overlaps(TestRectangle first, TestRectangle second)
    {
        return
            first.Left < second.Left + second.Width &&
            first.Left + first.Width > second.Left &&
            first.Top < second.Top + second.Height &&
            first.Top + first.Height > second.Top;
    }

    private static void AssertContains(SerializableBorder outer, SerializableBorder inner)
    {
        Assert.True(inner.Left >= outer.Left);
        Assert.True(inner.Top >= outer.Top);
        Assert.True(inner.Left + inner.Width <= outer.Left + outer.Width);
        Assert.True(inner.Top + inner.Height <= outer.Top + outer.Height);
    }

    private static void AssertRouteAvoids(
        IReadOnlyList<ModelRoutePoint> route,
        SerializableBorder obstacle)
    {
        var left = obstacle.Left + 1;
        var right = obstacle.Left + obstacle.Width - 1;
        var top = obstacle.Top + 1;
        var bottom = obstacle.Top + obstacle.Height - 1;
        for (var index = 0; index < route.Count - 1; index++)
        {
            var start = route[index];
            var end = route[index + 1];
            var steps = Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
            for (var step = 0; step <= steps; step++)
            {
                var fraction = steps == 0 ? 0 : (double)step / steps;
                var x = (int)Math.Round(start.X + (end.X - start.X) * fraction);
                var y = (int)Math.Round(start.Y + (end.Y - start.Y) * fraction);
                Assert.False(
                    x > left && x < right && y > top && y < bottom,
                    $"Route segment intersects the obstacle at ({x}, {y}).");
            }
        }
    }

    private static double HandleOffset(SerializableConnector connector)
    {
        var midpointX = (connector.X0 + connector.X1) / 2.0;
        var midpointY = (connector.Y0 + connector.Y1) / 2.0;
        return Math.Sqrt(
            Math.Pow(connector.MPX - midpointX, 2) +
            Math.Pow(connector.MPY - midpointY, 2));
    }

    private static void AssertEndpointFacesHandle(
        SerializableBorder entity,
        int endpointX,
        int endpointY,
        int handleX,
        int handleY,
        StencilConnectionPort port)
    {
        var centerX = entity.Left + entity.Width / 2.0;
        var centerY = entity.Top + entity.Height / 2.0;
        var normalizedX = (handleX - centerX) / Math.Max(1.0, entity.Width / 2.0);
        var normalizedY = (handleY - centerY) / Math.Max(1.0, entity.Height / 2.0);
        var angle = Math.Atan2(normalizedY, normalizedX) * 180.0 / Math.PI;
        if (angle < 0)
            angle += 360;
        var expectedPort = ((int)Math.Round(angle / 45.0) % 8) switch
        {
            0 => StencilConnectionPort.East,
            1 => StencilConnectionPort.SouthEast,
            2 => StencilConnectionPort.South,
            3 => StencilConnectionPort.SouthWest,
            4 => StencilConnectionPort.West,
            5 => StencilConnectionPort.NorthWest,
            6 => StencilConnectionPort.North,
            _ => StencilConnectionPort.NorthEast,
        };
        Assert.Equal(expectedPort, port);

        var endpointVectorX = endpointX - centerX;
        var endpointVectorY = endpointY - centerY;
        var handleVectorX = handleX - centerX;
        var handleVectorY = handleY - centerY;
        var crossProduct = Math.Abs(
            endpointVectorX * handleVectorY -
            endpointVectorY * handleVectorX);
        Assert.InRange(
            crossProduct,
            0,
            Math.Max(Math.Abs(handleVectorX), Math.Abs(handleVectorY)) * 2);
        Assert.True(
            endpointVectorX * handleVectorX + endpointVectorY * handleVectorY > 0);
    }

    private readonly record struct TestRectangle(
        double Left,
        double Top,
        double Width,
        double Height);
}
