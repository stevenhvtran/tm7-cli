using System.CommandLine;
using Spectre.Console;
using Tm7.Cli.Layout;
using Tm7.Cli.Model;
using Tm7.Cli.Parsers;

namespace Tm7.Cli.Commands;

internal static class ImportCommand
{
    // Flow TypeIds emitted by `import dot`. Exposed so tests can assert the
    // bundled default template defines them (avoiding string drift between
    // production code and the mapper⇄KB invariant test).
    internal const string ForwardFlowTypeId = "SE.DF.TMCore.Request";
    internal const string ReverseFlowTypeId = "SE.DF.TMCore.Response";
    internal const string GenericFlowTypeId = "GE.DF";

    internal static Command Create()
    {
        var importCmd = new Command("import", "Import from external formats.");
        importCmd.Add(CreateImportDotCommand());
        return importCmd;
    }

    static Command CreateImportDotCommand()
    {
        var dotFileArg = new Argument<FileInfo>("dotfile") { Description = "Path to the .dot file." };
        var outputOpt = new Option<FileInfo>("--output") { Description = "Output .tm7 file path.", Required = true };
        var templateOpt = new Option<FileInfo?>("--template") { Description = "Template .tm7 file for KB. If omitted, the bundled Azure Threat Model template is used." };
        var graphvizDotOpt = new Option<string?>("--graphviz-dot")
        {
            Description = $"Path to the Graphviz dot executable. Defaults to {GraphvizLayoutEngine.ExecutableEnvironmentVariable} or PATH."
        };

        var cmd = new Command("dot", "Import a Graphviz DOT file into a TM7 model.")
        {
            dotFileArg,
            outputOpt,
            templateOpt,
            graphvizDotOpt
        };

        cmd.SetAction(parseResult =>
        {
            var dotFile = parseResult.GetValue(dotFileArg)!;
            var outputFile = parseResult.GetValue(outputOpt)!;
            var templateFile = parseResult.GetValue(templateOpt);
            var graphvizDot = parseResult.GetValue(graphvizDotOpt);

            // 1. Parse DOT
            var dotGraph = DotParser.Parse(dotFile.FullName);
            AnsiConsole.MarkupLine($"[blue]Parsed DOT:[/] {dotGraph.Entities.Count} entities, {dotGraph.Edges.Count} edges, {dotGraph.Boundaries.Count} boundaries");

            // 2. Compute a topology-aware Graphviz layout
            var layout = GraphvizLayoutEngine.Layout(dotFile.FullName, graphvizDot);
            if (layout.Warnings.Length > 0)
                AnsiConsole.MarkupLine($"[yellow]Graphviz:[/] {Markup.Escape(layout.Warnings)}");

            // 3. Load template for KB
            var template = templateFile is null
                ? Tm7File.LoadDefaultTemplate()
                : Tm7File.Load(templateFile.FullName);

            // 4. Build entity GUID map
            var entityGuids = new Dictionary<string, Guid>();
            foreach (var entity in dotGraph.Entities)
                entityGuids[entity.Id] = Guid.NewGuid();
            var boundaryGuids = dotGraph.Boundaries.ToDictionary(
                boundary => boundary.Id,
                _ => Guid.NewGuid(),
                StringComparer.Ordinal);

            // 5. Create entity stencils at Graphviz-computed coordinates
            var borders = new List<SerializableBorder>();
            var entityBorders = new Dictionary<string, SerializableBorder>(StringComparer.Ordinal);
            foreach (var entity in dotGraph.Entities)
            {
                var mapping = DotToTm7Mapper.MapEntityType(entity.Id, entity.Label, entity.IsInsideBoundary);
                var bounds = layout.GetModelBounds(entity.Id);
                var border = CommandHelpers.CreateStencil(
                    mapping.GenericTypeId,
                    entityGuids[entity.Id],
                    mapping.TypeId,
                    CommandHelpers.CreateEntityProperties(entity.Label),
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height);
                if (entity.BoundaryId is not null &&
                    boundaryGuids.TryGetValue(entity.BoundaryId, out var parentBoundaryGuid))
                {
                    CommandHelpers.SetLayoutParent(border, parentBoundaryGuid);
                }
                borders.Add(border);
                entityBorders.Add(entity.Id, border);
            }

            // Graphviz plain output omits clusters, so derive each TM7 boundary from
            // the Graphviz-positioned nodes that the DOT parser associated with it.
            const int boundaryPadding = 50;
            foreach (var boundary in dotGraph.Boundaries)
            {
                var contained = boundary.ContainedEntityIds
                    .Distinct(StringComparer.Ordinal)
                    .Where(entityBorders.ContainsKey)
                    .Select(id => entityBorders[id])
                    .ToList();
                var bMapping = DotToTm7Mapper.MapBoundaryType(boundary.Label);
                var bGuid = boundaryGuids[boundary.Id];
                var bProps = CommandHelpers.CreateEntityProperties(boundary.Label);
                int boundaryLeft = contained.Count == 0
                    ? 60
                    : contained.Min(e => e.Left) - boundaryPadding;
                int boundaryTop = contained.Count == 0
                    ? 60
                    : contained.Min(e => e.Top) - boundaryPadding;
                int boundaryRight = contained.Count == 0
                    ? 360
                    : contained.Max(e => e.Left + e.Width) + boundaryPadding;
                int boundaryBottom = contained.Count == 0
                    ? 260
                    : contained.Max(e => e.Top + e.Height) + boundaryPadding;
                int boundaryWidth = boundaryRight - boundaryLeft;
                int boundaryHeight = boundaryBottom - boundaryTop;
                var boundaryStencil = CommandHelpers.CreateStencil(
                    bMapping.GenericTypeId,
                    bGuid,
                    bMapping.TypeId,
                    bProps,
                    boundaryLeft,
                    boundaryTop,
                    boundaryWidth,
                    boundaryHeight);
                if (boundary.ParentBoundaryId is not null &&
                    boundaryGuids.TryGetValue(boundary.ParentBoundaryId, out var parentBoundaryGuid))
                {
                    CommandHelpers.SetLayoutParent(boundaryStencil, parentBoundaryGuid);
                }
                borders.Add(boundaryStencil);
            }

            // 6. Create flows with smart port routing
            var flowLines = new List<SerializableLine>();
            foreach (var edge in dotGraph.Edges)
            {
                if (!entityGuids.TryGetValue(edge.SourceId, out var srcGuid) ||
                    !entityGuids.TryGetValue(edge.TargetId, out var tgtGuid))
                    continue;

                string flowLabel = edge.Label.Length > 0 ? edge.Label : $"{GetEntityLabelShort(dotGraph, edge.SourceId)} -> {GetEntityLabelShort(dotGraph, edge.TargetId)}";

                // Forward flow
                var fwdGuid = Guid.NewGuid();
                var fwdProps = CommandHelpers.CreateFlowProperties(flowLabel);
                flowLines.Add(new SerializableConnector(
                    fwdGuid, ForwardFlowTypeId, GenericFlowTypeId, fwdProps,
                    tgtGuid, srcGuid,
                    StencilConnectionPort.None, StencilConnectionPort.None,
                    0, 0, 0, 0, 0, 0,
                    1.0, ""));

                // Reverse flow for bidirectional
                if (edge.Bidirectional)
                {
                    string revLabel = edge.Label.Length > 0
                        ? $"{edge.Label} (response)"
                        : $"{GetEntityLabelShort(dotGraph, edge.TargetId)} -> {GetEntityLabelShort(dotGraph, edge.SourceId)}";
                    var revGuid = Guid.NewGuid();
                    var revProps = CommandHelpers.CreateFlowProperties(revLabel);
                    flowLines.Add(new SerializableConnector(
                        revGuid, ReverseFlowTypeId, GenericFlowTypeId, revProps,
                        srcGuid, tgtGuid,
                        StencilConnectionPort.None, StencilConnectionPort.None,
                        0, 0, 0, 0, 0, 0,
                        1.0, ""));
                }
            }

            // 7. Build the model
            string modelName = "OSMP Threat Model";
            if (dotGraph.Label != null)
            {
                // Use first sentence of graph label
                var firstLine = dotGraph.Label.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (firstLine != null && firstLine.Length > 10)
                    modelName = firstLine.Length > 100 ? firstLine[..100] : firstLine;
            }

            var surface = new SerializableDrawingSurfaceModel(
                Guid.NewGuid(), "", "", Array.Empty<SerializableDisplayAttribute>(),
                borders.ToArray(), flowLines.ToArray(),
                80.0, "Diagram 1");

            var description = dotGraph.Label ?? "";

            var meta = new SerializableMetaInformation(modelName, "", "", "", description, "", "");

            var newModel = new SerializableModelData(
                new[] { surface },
                meta,
                Array.Empty<SerializableNote>(),
                new Dictionary<string, SerializableThreat>(),
                true,
                Array.Empty<SerializableValidation>(),
                template.Version,
                template.KnowledgeBase,
                template.Profile ?? new SerializableProfile());

            Tm7GraphvizLayout.Apply(
                newModel,
                graphvizDot,
                dotGraph.RankDirection);
            Tm7File.Save(newModel, outputFile.FullName);

            var kbName = template.KnowledgeBase.Manifest?.Name;
            var templateLabel = templateFile is null
                ? (string.IsNullOrEmpty(kbName) ? "bundled default template" : $"bundled default template: {kbName}")
                : templateFile.Name;
            AnsiConsole.MarkupLine($"[green]Imported[/] {Markup.Escape(outputFile.FullName)} [dim](KB: {Markup.Escape(templateLabel)})[/]");
            AnsiConsole.MarkupLine($"  Entities: {borders.Count} ({dotGraph.Entities.Count(e => !e.IsInsideBoundary)} external, {dotGraph.Entities.Count(e => e.IsInsideBoundary)} internal, {dotGraph.Boundaries.Count} boundaries)");
            AnsiConsole.MarkupLine($"  Flows: {flowLines.Count}");
        });
        return cmd;
    }

    static string GetEntityLabelShort(DotGraph graph, string entityId)
    {
        var entity = graph.Entities.FirstOrDefault(e => e.Id == entityId);
        if (entity == null) return entityId;
        var label = entity.Label.Replace("\\n", " ");
        // Trim multi-line labels to first line
        var newline = label.IndexOf('\n');
        if (newline > 0) label = label[..newline];
        return label.Length > 30 ? label[..30] : label;
    }

}
