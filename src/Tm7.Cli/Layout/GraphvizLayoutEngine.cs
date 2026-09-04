using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tm7.Cli.Layout;

internal readonly record struct GraphvizNodeLayout(
    string Id,
    double CenterX,
    double CenterY,
    double Width,
    double Height);

internal readonly record struct GraphvizPoint(double X, double Y);

internal sealed record GraphvizEdgeLayout(
    string TailId,
    string HeadId,
    IReadOnlyList<GraphvizPoint> Points,
    string Color);

internal readonly record struct ModelNodeBounds(int Left, int Top, int Width, int Height);
internal readonly record struct ModelRoutePoint(int X, int Y);

internal sealed class GraphvizLayoutResult
{
    private const int Margin = 120;
    private const int MaximumGraphContentCoordinate = 1300;

    public GraphvizLayoutResult(
        double scale,
        double width,
        double height,
        IReadOnlyDictionary<string, GraphvizNodeLayout> nodes,
        IReadOnlyList<GraphvizEdgeLayout> edges,
        bool orthogonalRoutes,
        string warnings)
    {
        Scale = scale;
        Width = width;
        Height = height;
        Nodes = nodes;
        Edges = edges;
        OrthogonalRoutes = orthogonalRoutes;
        Warnings = warnings;
        var realNodeCount = nodes.Keys.Count(key => key.StartsWith("n_", StringComparison.Ordinal));
        if (realNodeCount == 0)
            realNodeCount = nodes.Count;
        RealNodeCount = realNodeCount;
        MinimumNodeWidth = realNodeCount > 18 ? 50 : 100;
        MinimumNodeHeight = realNodeCount > 18 ? 30 : 40;
        ModelUnitsPerGraphUnit = Math.Min(
            scale * 100.0,
            Math.Min(
                (double)(MaximumGraphContentCoordinate - 2 * Margin) / width,
                (double)(MaximumGraphContentCoordinate - 2 * Margin) / height));
    }

    public double Scale { get; }
    public double Width { get; }
    public double Height { get; }
    public IReadOnlyDictionary<string, GraphvizNodeLayout> Nodes { get; }
    public IReadOnlyList<GraphvizEdgeLayout> Edges { get; }
    public bool OrthogonalRoutes { get; }
    public string Warnings { get; }
    public double ReadableScale => ModelUnitsPerGraphUnit / 100.0;
    private double ModelUnitsPerGraphUnit { get; }
    private int RealNodeCount { get; }
    private int MinimumNodeWidth { get; }
    private int MinimumNodeHeight { get; }

    public ModelNodeBounds GetModelBounds(string nodeId)
    {
        if (!Nodes.TryGetValue(nodeId, out var node))
        {
            throw new InvalidDataException(
                $"Graphviz did not return layout coordinates for DOT node '{nodeId}'.");
        }

        var units = ModelUnitsPerGraphUnit;
        var minimumWidth = nodeId.StartsWith("n_", StringComparison.Ordinal) && RealNodeCount <= 18
            ? Math.Clamp((int)Math.Round(node.Width * 100), MinimumNodeWidth, 240)
            : MinimumNodeWidth;
        var width = Math.Max(minimumWidth, (int)Math.Round(node.Width * units));
        var height = Math.Max(MinimumNodeHeight, (int)Math.Round(node.Height * units));
        var centerX = Margin + (int)Math.Round(node.CenterX * units);
        var centerY = Margin + (int)Math.Round((Height - node.CenterY) * units);

        return new ModelNodeBounds(
            centerX - width / 2,
            centerY - height / 2,
            width,
            height);
    }

    public IReadOnlyList<ModelRoutePoint> GetModelRoute(string color)
    {
        var edge = Edges.SingleOrDefault(
            candidate => string.Equals(candidate.Color, color, StringComparison.OrdinalIgnoreCase));
        if (edge is null)
            throw new InvalidDataException($"Graphviz did not return the expected edge route '{color}'.");

        var units = ModelUnitsPerGraphUnit;
        var splinePoints = OrthogonalRoutes
            ? edge.Points
            : SampleSpline(edge.Points);
        var points = splinePoints
            .Select(point => new ModelRoutePoint(
                Margin + (int)Math.Round(point.X * units),
                Margin + (int)Math.Round((Height - point.Y) * units)))
            .ToList();
        var result = new List<ModelRoutePoint>(points.Count);
        foreach (var point in points)
        {
            if (result.Count == 0 || result[^1] != point)
                result.Add(point);
        }
        return result;
    }

    private static IReadOnlyList<GraphvizPoint> SampleSpline(IReadOnlyList<GraphvizPoint> controlPoints)
    {
        if (controlPoints.Count < 4 || (controlPoints.Count - 1) % 3 != 0)
            return controlPoints;

        const int samplesPerSegment = 12;
        var result = new List<GraphvizPoint> { controlPoints[0] };
        for (var index = 1; index + 2 < controlPoints.Count; index += 3)
        {
            var start = controlPoints[index - 1];
            var control1 = controlPoints[index];
            var control2 = controlPoints[index + 1];
            var end = controlPoints[index + 2];
            for (var sample = 1; sample <= samplesPerSegment; sample++)
            {
                var t = (double)sample / samplesPerSegment;
                var inverse = 1.0 - t;
                var x =
                    inverse * inverse * inverse * start.X +
                    3 * inverse * inverse * t * control1.X +
                    3 * inverse * t * t * control2.X +
                    t * t * t * end.X;
                var y =
                    inverse * inverse * inverse * start.Y +
                    3 * inverse * inverse * t * control1.Y +
                    3 * inverse * t * t * control2.Y +
                    t * t * t * end.Y;
                result.Add(new GraphvizPoint(x, y));
            }
        }

        return result;
    }
}

internal static class GraphvizLayoutEngine
{
    private const int LayoutTimeoutMilliseconds = 30_000;
    internal const string ExecutableEnvironmentVariable = "TM7_GRAPHVIZ_DOT";

    public static GraphvizLayoutResult Layout(string dotFilePath, string? executablePath = null)
    {
        var fullPath = Path.GetFullPath(dotFilePath);
        return LayoutSource(
            File.ReadAllText(fullPath),
            fullPath,
            Path.GetDirectoryName(fullPath)!,
            executablePath);
    }

    public static GraphvizLayoutResult LayoutSource(
        string dotSource,
        string sourceName,
        string? executablePath = null)
    {
        return LayoutSource(dotSource, sourceName, Environment.CurrentDirectory, executablePath);
    }

    private static GraphvizLayoutResult LayoutSource(
        string dotSource,
        string sourceName,
        string workingDirectory,
        string? executablePath)
    {
        var executable = ResolveExecutable(executablePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        startInfo.ArgumentList.Add("-Tplain");
        if (!DefinesRankDirection(dotSource))
            startInfo.ArgumentList.Add("-Grankdir=LR");
        if (!DefinesGraphAttribute(dotSource, "nodesep"))
            startInfo.ArgumentList.Add("-Gnodesep=0.6");
        if (!DefinesGraphAttribute(dotSource, "ranksep"))
            startInfo.ArgumentList.Add("-Granksep=1.0");
        startInfo.ArgumentList.Add("-Nshape=box");
        startInfo.ArgumentList.Add("-Nwidth=1.5");
        startInfo.ArgumentList.Add("-Nheight=0.8");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Unable to start Graphviz executable '{executable}'. Install Graphviz and ensure 'dot' is on PATH, " +
                $"set {ExecutableEnvironmentVariable}, or pass --graphviz-dot <path>.",
                ex);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(dotSource);
        process.StandardInput.Close();

        if (!process.WaitForExit(LayoutTimeoutMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the timeout check and termination.
            }
            throw new TimeoutException(
                $"Graphviz layout exceeded {LayoutTimeoutMilliseconds / 1000} seconds for '{sourceName}'.");
        }

        var output = standardOutput.GetAwaiter().GetResult();
        var error = standardError.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"Graphviz failed to lay out '{sourceName}' (exit {process.ExitCode}): " +
                (error.Length == 0 ? "no diagnostic output" : error));
        }

        var orthogonalRoutes = Regex.IsMatch(
            dotSource,
            @"\bsplines\s*=\s*ortho\b",
            RegexOptions.IgnoreCase);
        return ParsePlainOutput(output, error, orthogonalRoutes);
    }

    internal static GraphvizLayoutResult ParsePlainOutput(
        string output,
        string warnings = "",
        bool orthogonalRoutes = false)
    {
        double? scale = null;
        double? width = null;
        double? height = null;
        var nodes = new Dictionary<string, GraphvizNodeLayout>(StringComparer.Ordinal);
        var edges = new List<GraphvizEdgeLayout>();

        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            var tokens = Tokenize(line);
            if (tokens.Count == 0)
                continue;

            switch (tokens[0])
            {
                case "graph" when tokens.Count >= 4:
                    scale = ParseNumber(tokens[1], "graph scale");
                    width = ParseNumber(tokens[2], "graph width");
                    height = ParseNumber(tokens[3], "graph height");
                    break;

                case "node" when tokens.Count >= 6:
                    var node = new GraphvizNodeLayout(
                        tokens[1],
                        ParseNumber(tokens[2], $"node '{tokens[1]}' x"),
                        ParseNumber(tokens[3], $"node '{tokens[1]}' y"),
                        ParseNumber(tokens[4], $"node '{tokens[1]}' width"),
                        ParseNumber(tokens[5], $"node '{tokens[1]}' height"));
                    if (!nodes.TryAdd(node.Id, node))
                    {
                        throw new InvalidDataException(
                            $"Graphviz returned duplicate layout records for DOT node '{node.Id}'.");
                    }
                    break;

                case "edge" when tokens.Count >= 7:
                    if (!int.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pointCount) ||
                        pointCount < 2 ||
                        tokens.Count < 4 + pointCount * 2 + 2)
                    {
                        throw new InvalidDataException("Graphviz returned an invalid edge record.");
                    }

                    var points = new List<GraphvizPoint>(pointCount);
                    for (var pointIndex = 0; pointIndex < pointCount; pointIndex++)
                    {
                        var tokenIndex = 4 + pointIndex * 2;
                        points.Add(new GraphvizPoint(
                            ParseNumber(tokens[tokenIndex], $"edge '{tokens[1]}' x"),
                            ParseNumber(tokens[tokenIndex + 1], $"edge '{tokens[1]}' y")));
                    }

                    edges.Add(new GraphvizEdgeLayout(
                        tokens[1],
                        tokens[2],
                        points,
                        tokens[^1]));
                    break;
            }
        }

        if (scale is null || width is null || height is null)
            throw new InvalidDataException("Graphviz plain output did not contain a valid graph record.");
        if (nodes.Count == 0)
            throw new InvalidDataException("Graphviz plain output did not contain any node records.");
        if (scale <= 0 || width <= 0 || height <= 0)
            throw new InvalidDataException("Graphviz returned non-positive graph dimensions.");

        return new GraphvizLayoutResult(
            scale.Value,
            width.Value,
            height.Value,
            nodes,
            edges,
            orthogonalRoutes,
            warnings);
    }

    private static string ResolveExecutable(string? executablePath)
    {
        var executable = executablePath;
        if (string.IsNullOrWhiteSpace(executable))
            executable = Environment.GetEnvironmentVariable(ExecutableEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(executable))
        {
            if (OperatingSystem.IsWindows())
            {
                var installRoots = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                };
                foreach (var installRoot in installRoots.Where(path => path.Length > 0).Distinct())
                {
                    var installedDot = Path.Combine(installRoot, "Graphviz", "bin", "dot.exe");
                    if (File.Exists(installedDot))
                        return installedDot;
                }
            }

            return "dot";
        }

        executable = executable.Trim();
        if ((Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar)) &&
            !File.Exists(executable))
        {
            throw new FileNotFoundException(
                $"Graphviz executable '{executable}' does not exist.",
                executable);
        }

        return executable;
    }

    private static bool DefinesRankDirection(string dotSource)
    {
        return DefinesGraphAttribute(dotSource, "rankdir");
    }

    private static bool DefinesGraphAttribute(string dotSource, string attribute)
    {
        return Regex.IsMatch(
            dotSource,
            $@"\b{Regex.Escape(attribute)}\s*=",
            RegexOptions.IgnoreCase);
    }

    private static double ParseNumber(string value, string description)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            throw new InvalidDataException($"Graphviz returned invalid {description} value '{value}'.");
        return result;
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var index = 0;

        while (index < line.Length)
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;
            if (index >= line.Length)
                break;

            if (line[index] != '"')
            {
                var start = index;
                while (index < line.Length && !char.IsWhiteSpace(line[index]))
                    index++;
                tokens.Add(line[start..index]);
                continue;
            }

            index++;
            var token = new StringBuilder();
            while (index < line.Length)
            {
                var current = line[index++];
                if (current == '"')
                    break;

                if (current == '\\' && index < line.Length)
                {
                    var escaped = line[index++];
                    if (escaped is '"' or '\\')
                    {
                        token.Append(escaped);
                    }
                    else
                    {
                        token.Append('\\');
                        token.Append(escaped);
                    }
                    continue;
                }

                token.Append(current);
            }

            tokens.Add(token.ToString());
        }

        return tokens;
    }
}
