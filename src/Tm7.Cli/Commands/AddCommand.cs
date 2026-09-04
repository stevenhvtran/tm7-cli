using System.CommandLine;
using Spectre.Console;
using Tm7.Cli.Layout;
using Tm7.Cli.Model;

namespace Tm7.Cli.Commands;

internal static class AddCommand
{
    internal static Command Create()
    {
        var addCmd = new Command("add", "Add elements to the model.");
        addCmd.Add(CreateAddSurfaceCommand());
        addCmd.Add(CreateAddEntityCommand());
        addCmd.Add(CreateAddFlowCommand());
        return addCmd;
    }

    static Command CreateAddSurfaceCommand()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "Path to a .tm7 file." };
        var nameOpt = new Option<string>("--name") { Description = "Drawing surface name.", Required = true };
        var cmd = new Command("surface", "Add an empty drawing surface.") { fileArg, nameOpt };

        cmd.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var name = parseResult.GetValue(nameOpt)!;
            var model = Tm7File.Load(file.FullName);
            var surface = new SerializableDrawingSurfaceModel(
                Guid.NewGuid(),
                "",
                "",
                [
                    new SerializableHeaderDisplayAttribute("Diagram"),
                    new SerializableStringDisplayAttribute("Name", "", name),
                ],
                Array.Empty<SerializableBorder>(),
                Array.Empty<SerializableLine>(),
                80,
                name);
            model.DrawingSurfaceList.Add(surface);
            Tm7File.Save(model, file.FullName);
            AnsiConsole.MarkupLine(
                $"[green]Added surface[/] {Markup.Escape(name)} [dim](index {model.DrawingSurfaceList.Count - 1})[/]");
        });

        return cmd;
    }

    static Command CreateAddEntityCommand()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "Path to a .tm7 file." };
        var nameOpt = new Option<string>("--name") { Description = "Entity name.", Required = true };
        var typeIdOpt = new Option<string>("--type-id") { Description = "TypeId for the entity.", Required = true };
        var genericTypeIdOpt = new Option<string>("--generic-type-id") { Description = "GenericTypeId (GE.P, GE.DS, GE.EI, GE.TB.B).", Required = true };
        var surfaceOpt = new Option<int>("--surface") { Description = "Drawing surface index (default 0).", DefaultValueFactory = _ => 0 };
        var boundaryOpt = new Option<string?>("--boundary") { Description = "GUID of the trust boundary that should contain the entity." };
        var noLayoutOpt = new Option<bool>("--no-layout")
        {
            Description = "Defer Graphviz layout until `tm7 layout <file>`; intended for batch construction."
        };

        var cmd = new Command("entity", "Add a new entity and automatically lay out the model.")
        {
            fileArg,
            nameOpt,
            typeIdOpt,
            genericTypeIdOpt,
            surfaceOpt,
            boundaryOpt,
            noLayoutOpt
        };

        cmd.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var name = parseResult.GetValue(nameOpt)!;
            var typeId = parseResult.GetValue(typeIdOpt)!;
            var genericTypeId = parseResult.GetValue(genericTypeIdOpt)!;
            var surfaceIdx = parseResult.GetValue(surfaceOpt);
            var boundaryGuidText = parseResult.GetValue(boundaryOpt);
            var noLayout = parseResult.GetValue(noLayoutOpt);

            var model = Tm7File.Load(file.FullName);
            if (model.DrawingSurfaceList is null || surfaceIdx < 0 || surfaceIdx >= model.DrawingSurfaceList.Count)
            {
                AnsiConsole.MarkupLine("[red]Invalid surface index.[/]");
                return;
            }

            var surface = model.DrawingSurfaceList[surfaceIdx];
            SerializableBorderBoundary? parentBoundary = null;
            if (!string.IsNullOrWhiteSpace(boundaryGuidText))
            {
                if (!Guid.TryParse(boundaryGuidText, out var boundaryGuid) ||
                    !surface.Borders.TryGetValue(boundaryGuid, out var boundaryValue) ||
                    boundaryValue is not SerializableBorderBoundary parsedBoundary)
                {
                    AnsiConsole.MarkupLine("[red]The --boundary value must identify a trust boundary on the selected surface.[/]");
                    return;
                }
                parentBoundary = parsedBoundary;
            }

            var isBoundary = genericTypeId == "GE.TB.B";
            var existingBorders = surface.Borders.Values.OfType<SerializableBorder>().ToList();
            var provisionalIndex = parentBoundary is null
                ? existingBorders.Count(border => border is not SerializableBorderBoundary)
                : existingBorders.Count(border =>
                    border is not SerializableBorderBoundary &&
                    CommandHelpers.GetLayoutParent(border) == parentBoundary.Guid);
            var rootBoundaryIndex = existingBorders.OfType<SerializableBorderBoundary>()
                .Count(boundary => CommandHelpers.GetLayoutParent(boundary) is null);
            var left = parentBoundary is not null
                ? parentBoundary.Left + 35 + provisionalIndex % 3 * 85
                : isBoundary
                    ? 20 + rootBoundaryIndex % 3 * 460
                    : 60 + provisionalIndex % 8 * 150;
            var top = parentBoundary is not null
                ? parentBoundary.Top + 55 + provisionalIndex / 3 * 65
                : isBoundary
                    ? 20 + rootBoundaryIndex / 3 * 390
                    : 60 + provisionalIndex / 8 * 90;
            left = Math.Clamp(left, 10, 1250);
            top = Math.Clamp(top, 10, 1250);
            var width = isBoundary ? 420 : 150;
            var height = isBoundary ? 340 : 80;
            var guid = Guid.NewGuid();
            var props = CommandHelpers.CreateEntityProperties(name);
            var stencil = CommandHelpers.CreateStencil(genericTypeId, guid, typeId, props, left, top, width, height);
            if (parentBoundary is not null)
                CommandHelpers.SetLayoutParent(stencil, parentBoundary.Guid);

            surface.Borders[guid] = stencil;

            if (!noLayout)
                Tm7GraphvizLayout.Apply(model);
            Tm7File.Save(model, file.FullName);
            AnsiConsole.MarkupLine($"[green]Added entity[/] {Markup.Escape(name)} [dim]({guid})[/]");
        });
        return cmd;
    }

    static Command CreateAddFlowCommand()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "Path to a .tm7 file." };
        var nameOpt = new Option<string>("--name") { Description = "Flow name.", Required = true };
        var sourceOpt = new Option<string>("--source") { Description = "Source entity GUID.", Required = true };
        var targetOpt = new Option<string>("--target") { Description = "Target entity GUID.", Required = true };
        var typeIdOpt = new Option<string>("--type-id") { Description = "TypeId (default: SE.DF.TMCore.Request).", DefaultValueFactory = _ => "SE.DF.TMCore.Request" };
        var surfaceOpt = new Option<int>("--surface") { Description = "Drawing surface index (default 0).", DefaultValueFactory = _ => 0 };
        var noLayoutOpt = new Option<bool>("--no-layout")
        {
            Description = "Defer Graphviz layout until `tm7 layout <file>`; intended for batch construction."
        };

        var cmd = new Command("flow", "Add a new data flow.")
        {
            fileArg,
            nameOpt,
            sourceOpt,
            targetOpt,
            typeIdOpt,
            surfaceOpt,
            noLayoutOpt
        };

        cmd.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var name = parseResult.GetValue(nameOpt)!;
            var sourceGuidStr = parseResult.GetValue(sourceOpt)!;
            var targetGuidStr = parseResult.GetValue(targetOpt)!;
            var typeId = parseResult.GetValue(typeIdOpt)!;
            var surfaceIdx = parseResult.GetValue(surfaceOpt);
            var noLayout = parseResult.GetValue(noLayoutOpt);

            if (!Guid.TryParse(sourceGuidStr, out var sourceGuid) || !Guid.TryParse(targetGuidStr, out var targetGuid))
            {
                AnsiConsole.MarkupLine("[red]Invalid GUID format.[/]");
                return;
            }

            var model = Tm7File.Load(file.FullName);
            if (model.DrawingSurfaceList is null || surfaceIdx < 0 || surfaceIdx >= model.DrawingSurfaceList.Count)
            {
                AnsiConsole.MarkupLine("[red]Invalid surface index.[/]");
                return;
            }

            var surface = model.DrawingSurfaceList[surfaceIdx];

            if (!surface.Borders.TryGetValue(sourceGuid, out var srcObj) ||
                srcObj is not SerializableBorder srcBorder ||
                !surface.Borders.TryGetValue(targetGuid, out var tgtObj) ||
                tgtObj is not SerializableBorder tgtBorder)
            {
                AnsiConsole.MarkupLine("[red]Source and target must identify entities on the selected surface.[/]");
                return;
            }

            var srcX = srcBorder.Left + srcBorder.Width / 2;
            var srcY = srcBorder.Top + srcBorder.Height / 2;
            var tgtX = tgtBorder.Left + tgtBorder.Width / 2;
            var tgtY = tgtBorder.Top + tgtBorder.Height / 2;
            int handleX = (srcX + tgtX) / 2;
            int handleY = (srcY + tgtY) / 2;

            var guid = Guid.NewGuid();
            var props = CommandHelpers.CreateFlowProperties(name);
            var connector = new SerializableConnector(
                guid, typeId, "GE.DF", props,
                targetGuid, sourceGuid,
                StencilConnectionPort.None, StencilConnectionPort.None,
                srcX, srcY, tgtX, tgtY, handleX, handleY,
                1.0, "");

            surface.Lines[guid] = connector;

            if (!noLayout)
                Tm7GraphvizLayout.Apply(model);
            Tm7File.Save(model, file.FullName);
            AnsiConsole.MarkupLine($"[green]Added flow[/] {Markup.Escape(name)} [dim]({guid})[/]");
        });
        return cmd;
    }
}
