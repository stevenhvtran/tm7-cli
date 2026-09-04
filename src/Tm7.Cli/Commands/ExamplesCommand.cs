using System.CommandLine;
using Spectre.Console;

namespace Tm7.Cli.Commands;

internal static class ExamplesCommand
{
    internal static Command Create()
    {
        var cmd = new Command("examples", "Show usage examples.");

        cmd.SetAction(_ =>
        {
            AnsiConsole.Write(new Rule("[bold]tm7 CLI — Usage Examples[/]").LeftJustified());
            AnsiConsole.WriteLine();

            WriteSection("Inspect a model",
                "tm7 open model.tm7",
                "tm7 list entities model.tm7",
                "tm7 list flows model.tm7");

            WriteSection("Create a new model from template",
                "tm7 new empty.tm7 --name \"My Threat Model\"",
                "tm7 new empty.tm7 --template custom-template.tm7 --name \"My Threat Model\"");

            WriteSection("Add entities",
                "tm7 add entity model.tm7 --name \"Web API\" --type-id SE.P.TMCore.AzureAppServiceWebApp --generic-type-id GE.P",
                "tm7 add entity model.tm7 --name \"SQL Database\" --type-id SE.DS.TMCore.AzureSQLDB --generic-type-id GE.DS",
                "tm7 add entity model.tm7 --name \"User\" --type-id SE.EI.TMCore.Browser --generic-type-id GE.EI",
                "tm7 add entity model.tm7 --name \"Azure\" --type-id SE.TB.TMCore.AzureTrustBoundary --generic-type-id GE.TB.B",
                "tm7 add entity model.tm7 --name \"API\" --type-id GE.P --generic-type-id GE.P --boundary <boundary-guid>");

            WriteSection("Batch construction (defer layout until complete)",
                "tm7 add surface model.tm7 --name \"Runtime\"",
                "tm7 add entity model.tm7 --surface 1 --name \"API\" --type-id GE.P --generic-type-id GE.P --no-layout",
                "tm7 add flow model.tm7 --surface 1 --name \"HTTPS\" --source <source-guid> --target <target-guid> --no-layout",
                "tm7 layout model.tm7");

            WriteSection("Add data flows (use GUIDs from 'list entities')",
                "tm7 add flow model.tm7 --name \"HTTPS Request\" --source <source-guid> --target <target-guid>",
                "tm7 add flow model.tm7 --name \"SQL Query\" --source <source-guid> --target <target-guid> --type-id SE.DF.TMCore.Request");

            WriteSection("Remove elements",
                "tm7 remove entity model.tm7 --guid <entity-guid>",
                "tm7 remove flow model.tm7 --guid <flow-guid>");

            WriteSection("Import from Graphviz DOT",
                "tm7 import dot architecture.dot --output model.tm7",
                "tm7 import dot architecture.dot --output model.tm7 --template custom-template.tm7");

            WriteSection("Re-layout an existing model",
                "tm7 layout model.tm7");

            WriteSection("Render diagram in the terminal",
                "tm7 render model.tm7",
                "tm7 render model.tm7 --width 200 --height 60",
                "tm7 render model.tm7 --plain > diagram.txt");

            AnsiConsole.Write(new Rule("[dim]Entity Types[/]").LeftJustified());
            AnsiConsole.WriteLine();

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[bold]GenericTypeId[/]")
                .AddColumn("[bold]Shape[/]")
                .AddColumn("[bold]Example TypeIds[/]");

            table.AddRow("[cyan]GE.P[/]", "[cyan]╭─╮[/] Process (ellipse)", "GE.P, SE.P.TMCore.AzureAppServiceWebApp, SE.P.TMCore.AzureAD, SE.P.TMCore.Host");
            table.AddRow("[yellow]GE.EI[/]", "[yellow]┌─┐[/] External Interactor (rect)", "GE.EI, SE.EI.TMCore.Browser");
            table.AddRow("[green]GE.DS[/]", "[green]═══[/] Data Store (parallel lines)", "GE.DS, SE.DS.TMCore.AzureStorage, SE.DS.TMCore.AzureSQLDB, SE.DS.TMCore.AzureKeyVault");
            table.AddRow("[red]GE.TB.B[/]", "[red]┏━┓[/] Trust Boundary (bold border)", "GE.TB.B, SE.TB.TMCore.AzureTrustBoundary");

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            AnsiConsole.Write(new Rule("[dim]Workflow: build a model from scratch[/]").LeftJustified());
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]1.[/] tm7 new model.tm7 --name \"My App\"");
            AnsiConsole.MarkupLine("[dim]2.[/] tm7 add entity model.tm7 --name \"User\" --type-id GE.EI --generic-type-id GE.EI");
            AnsiConsole.MarkupLine("[dim]3.[/] tm7 add entity model.tm7 --name \"API\" --type-id GE.P --generic-type-id GE.P");
            AnsiConsole.MarkupLine("[dim]4.[/] tm7 list entities model.tm7  [dim]# get GUIDs[/]");
            AnsiConsole.MarkupLine("[dim]5.[/] tm7 add flow model.tm7 --name \"HTTPS\" --source <user-guid> --target <api-guid>");
            AnsiConsole.MarkupLine("[dim]6.[/] tm7 render model.tm7  [dim]# preview in terminal[/]");
            AnsiConsole.MarkupLine("[dim]7.[/] [dim italic]Open model.tm7 in Microsoft Threat Modeling Tool[/]");
            AnsiConsole.WriteLine();
        });

        return cmd;
    }

    static void WriteSection(string title, params string[] commands)
    {
        AnsiConsole.MarkupLine($"[bold underline]{Markup.Escape(title)}[/]");
        foreach (var cmd in commands)
            AnsiConsole.MarkupLine($"  [green]{Markup.Escape(cmd)}[/]");
        AnsiConsole.WriteLine();
    }
}
