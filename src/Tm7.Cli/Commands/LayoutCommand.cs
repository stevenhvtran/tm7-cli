using System.CommandLine;
using Spectre.Console;
using Tm7.Cli.Layout;

namespace Tm7.Cli.Commands;

internal static class LayoutCommand
{
    internal static Command Create()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "Path to a .tm7 file." };
        var command = new Command("layout", "Apply Graphviz layout to every drawing surface.") { fileArg };

        command.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var model = Tm7File.Load(file.FullName);
            Tm7GraphvizLayout.Apply(model);
            Tm7File.Save(model, file.FullName);
            AnsiConsole.MarkupLine($"[green]Laid out[/] {Markup.Escape(file.FullName)}");
        });

        return command;
    }
}
