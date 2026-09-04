using System.CommandLine;
using Tm7.Cli.Commands;

namespace Tm7.Cli;

/// <summary>
/// Builds the System.CommandLine command tree for the tm7 CLI application.
/// </summary>
public static class CommandFactory
{
    /// <summary>
    /// Creates the root command with all subcommands registered.
    /// </summary>
    public static RootCommand CreateRootCommand()
    {
        var rootCommand = new RootCommand("A CLI tool for constructing, interrogating, and modifying .tm7 threat model files.");
        rootCommand.Add(OpenCommand.Create());
        rootCommand.Add(ListCommand.Create());
        rootCommand.Add(AddCommand.Create());
        rootCommand.Add(RemoveCommand.Create());
        rootCommand.Add(NewCommand.Create());
        rootCommand.Add(ImportCommand.Create());
        rootCommand.Add(LayoutCommand.Create());
        rootCommand.Add(RenderCommand.Create());
        rootCommand.Add(ExamplesCommand.Create());
        return rootCommand;
    }
}
