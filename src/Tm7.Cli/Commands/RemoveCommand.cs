using System.CommandLine;
using Spectre.Console;
using Tm7.Cli.Layout;
using Tm7.Cli.Model;

namespace Tm7.Cli.Commands;

internal static class RemoveCommand
{
    internal static Command Create()
    {
        var removeCmd = new Command("remove", "Remove elements from the model.");
        removeCmd.Add(CreateRemoveEntityCommand());
        removeCmd.Add(CreateRemoveFlowCommand());
        return removeCmd;
    }

    static Command CreateRemoveEntityCommand()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "Path to a .tm7 file." };
        var guidOpt = new Option<string>("--guid") { Description = "GUID of the entity to remove.", Required = true };

        var cmd = new Command("entity", "Remove an entity and its connected flows.") { fileArg, guidOpt };

        cmd.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var guidStr = parseResult.GetValue(guidOpt)!;

            if (!Guid.TryParse(guidStr, out var entityGuid))
            {
                AnsiConsole.MarkupLine("[red]Invalid GUID format.[/]");
                return;
            }

            var model = Tm7File.Load(file.FullName);
            bool found = false;

            if (model.DrawingSurfaceList is not null)
            {
                foreach (var surface in model.DrawingSurfaceList)
                {
                    if (surface.Borders is not null && surface.Borders.Remove(entityGuid))
                    {
                        found = true;
                        // Remove flows referencing this entity
                        if (surface.Lines is not null)
                        {
                            var toRemove = surface.Lines
                                .Where(kvp => kvp.Value is SerializableConnector c && (c.SourceGuid == entityGuid || c.TargetGuid == entityGuid))
                                .Select(kvp => kvp.Key)
                                .ToList();
                            foreach (var key in toRemove)
                                surface.Lines.Remove(key);
                            if (toRemove.Count > 0)
                                AnsiConsole.MarkupLine($"[yellow]Also removed {toRemove.Count} connected flow(s).[/]");
                        }
                    }
                }
            }

            if (found)
            {
                Tm7GraphvizLayout.Apply(model);
                Tm7File.Save(model, file.FullName);
                AnsiConsole.MarkupLine($"[green]Removed entity[/] [dim]{entityGuid}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Entity {entityGuid} not found.[/]");
            }
        });
        return cmd;
    }

    static Command CreateRemoveFlowCommand()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "Path to a .tm7 file." };
        var guidOpt = new Option<string>("--guid") { Description = "GUID of the flow to remove.", Required = true };

        var cmd = new Command("flow", "Remove a data flow.") { fileArg, guidOpt };

        cmd.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var guidStr = parseResult.GetValue(guidOpt)!;

            if (!Guid.TryParse(guidStr, out var flowGuid))
            {
                AnsiConsole.MarkupLine("[red]Invalid GUID format.[/]");
                return;
            }

            var model = Tm7File.Load(file.FullName);
            bool found = false;

            if (model.DrawingSurfaceList is not null)
            {
                foreach (var surface in model.DrawingSurfaceList)
                {
                    if (surface.Lines is not null && surface.Lines.Remove(flowGuid))
                    {
                        found = true;
                        break;
                    }
                }
            }

            if (found)
            {
                Tm7GraphvizLayout.Apply(model);
                Tm7File.Save(model, file.FullName);
                AnsiConsole.MarkupLine($"[green]Removed flow[/] [dim]{flowGuid}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Flow {flowGuid} not found.[/]");
            }
        });
        return cmd;
    }
}
