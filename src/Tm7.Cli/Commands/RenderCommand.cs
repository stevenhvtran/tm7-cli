using System.CommandLine;
using Tm7.Cli.Layout;

namespace Tm7.Cli.Commands;

internal static class RenderCommand
{
    internal static Command Create()
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "Path to a .tm7 file." };
        var widthOpt = new Option<int>("--width") { Description = "Terminal width in columns.", DefaultValueFactory = _ => 180 };
        var heightOpt = new Option<int>("--height") { Description = "Terminal height in rows.", DefaultValueFactory = _ => 55 };
        var plainOpt = new Option<bool>("--plain") { Description = "Output plain text without ANSI color codes." };

        var cmd = new Command("render", "Render the threat model diagram to the terminal.") { fileArg, widthOpt, heightOpt, plainOpt };

        cmd.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var width = parseResult.GetValue(widthOpt);
            var height = parseResult.GetValue(heightOpt);
            var plain = parseResult.GetValue(plainOpt);

            var model = Tm7File.Load(file.FullName);
            var layout = Tm7GraphvizLayout.Apply(model);
            var surfaceRoutes = model.DrawingSurfaceList.Count == 0
                ? null
                : layout.GetRoutes(model.DrawingSurfaceList[0].Guid);
            var output = Tm7Renderer.Render(model, width, height, plain, surfaceRoutes);

            Console.Write(output);
        });
        return cmd;
    }
}
