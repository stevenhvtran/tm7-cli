using System.Text;
using Tm7.Cli.Commands;
using Tm7.Cli.Model;

namespace Tm7.Cli.Layout;

internal sealed class Tm7GraphvizLayoutResult
{
    private readonly Dictionary<Guid, IReadOnlyDictionary<Guid, IReadOnlyList<ModelRoutePoint>>> _surfaceRoutes = [];

    public IReadOnlyDictionary<Guid, IReadOnlyList<ModelRoutePoint>> GetRoutes(Guid surfaceGuid)
    {
        return _surfaceRoutes.TryGetValue(surfaceGuid, out var routes)
            ? routes
            : new Dictionary<Guid, IReadOnlyList<ModelRoutePoint>>();
    }

    internal void AddSurface(
        Guid surfaceGuid,
        IReadOnlyDictionary<Guid, IReadOnlyList<ModelRoutePoint>> routes)
    {
        _surfaceRoutes.Add(surfaceGuid, routes);
    }
}

internal static class Tm7GraphvizLayout
{
    private const int BoundaryPadding = 50;
    private const int MinimumModelCoordinate = 1;
    private const int MaximumModelCoordinate = 1450;
    private const double MaximumHandleOffset = 300.0;
    private const double ParallelLaneSpacing = 30.0;

    public static Tm7GraphvizLayoutResult Apply(
        SerializableModelData model,
        string? executablePath = null,
        string? preferredRankDirection = null)
    {
        var result = new Tm7GraphvizLayoutResult();
        foreach (var surface in model.DrawingSurfaceList)
        {
            var routes = Apply(surface, executablePath, preferredRankDirection);
            result.AddSurface(surface.Guid, routes);
        }

        return result;
    }

    private static IReadOnlyDictionary<Guid, IReadOnlyList<ModelRoutePoint>> Apply(
        SerializableDrawingSurfaceModel surface,
        string? executablePath,
        string? preferredRankDirection)
    {
        var borders = surface.Borders.Values.OfType<SerializableBorder>().ToList();
        var entities = borders.Where(border => border is not SerializableBorderBoundary).ToList();
        if (entities.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<ModelRoutePoint>>();
        var lineBoundaries = surface.Lines.Values.OfType<SerializableLineBoundary>().ToList();
        var originalEntityFrame = GeometryFrame.From(entities);

        var boundaries = borders.OfType<SerializableBorderBoundary>().ToList();
        var boundariesById = boundaries.ToDictionary(boundary => boundary.Guid);
        var entityParents = entities.ToDictionary(
            entity => entity.Guid,
            entity => ResolveParentBoundary(entity, boundaries, boundariesById));
        var boundaryParents = boundaries.ToDictionary(
            boundary => boundary.Guid,
            boundary => ResolveParentBoundary(
                boundary,
                boundaries.Where(candidate =>
                    candidate.Guid != boundary.Guid &&
                    (long)candidate.Width * candidate.Height >
                    (long)boundary.Width * boundary.Height).ToList(),
                boundariesById));

        var nodeIds = entities.ToDictionary(entity => entity.Guid, entity => $"n_{entity.Guid:N}");
        var dummyIds = boundaries.ToDictionary(boundary => boundary.Guid, boundary => $"d_{boundary.Guid:N}");
        var connectors = surface.Lines.Values
            .OfType<SerializableConnector>()
            .Where(connector =>
                nodeIds.ContainsKey(connector.SourceGuid) &&
                nodeIds.ContainsKey(connector.TargetGuid))
            .ToList();
        var edgeLayoutKeys = connectors
            .Select((connector, index) => new
            {
                connector.Guid,
                Key = new EdgeLayoutKey(
                    $"#{index * 2 + 1:X6}",
                    $"#{index * 2 + 2:X6}",
                    $"label_{connector.Guid:N}"),
            })
            .ToDictionary(item => item.Guid, item => item.Key);
        var rankDirections = preferredRankDirection is null
            ? new[] { "LR", "TB" }
            : new[] { NormalizeRankDirection(preferredRankDirection) };
        var layouts = rankDirections.ToDictionary(
            direction => direction,
            direction =>
            {
                var dot = BuildDot(
                    surface,
                    entities,
                    boundaries,
                    entityParents,
                    boundaryParents,
                    nodeIds,
                    dummyIds,
                    edgeLayoutKeys,
                    direction);
                return GraphvizLayoutEngine.LayoutSource(
                    dot,
                    $"drawing surface '{surface.Header}' ({direction})",
                    executablePath);
            },
            StringComparer.Ordinal);
        var horizontalLayout = layouts.GetValueOrDefault("LR");
        var verticalLayout = layouts.GetValueOrDefault("TB");
        var layout = preferredRankDirection is not null
            ? layouts.Values.Single()
            : SelectAutomaticLayout(
                horizontalLayout!,
                verticalLayout!,
                entities,
                boundaries,
                connectors,
                entityParents,
                boundaryParents,
                nodeIds,
                dummyIds);

        foreach (var entity in entities)
        {
            var bounds = layout.GetModelBounds(nodeIds[entity.Guid]);
            entity.SetBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }
        ResolveEntityOverlaps(entities);

        foreach (var boundary in OrderBoundariesDeepestFirst(boundaries, boundaryParents))
        {
            var members = entities
                .Where(entity => entityParents[entity.Guid]?.Guid == boundary.Guid)
                .Cast<SerializableBorder>()
                .Concat(boundaries.Where(child => boundaryParents[child.Guid]?.Guid == boundary.Guid))
                .ToList();

            if (members.Count == 0)
            {
                var dummyBounds = layout.GetModelBounds(dummyIds[boundary.Guid]);
                boundary.SetBounds(
                    dummyBounds.Left - BoundaryPadding,
                    dummyBounds.Top - BoundaryPadding,
                    dummyBounds.Width + BoundaryPadding * 2,
                    dummyBounds.Height + BoundaryPadding * 2);
                continue;
            }

            var left = members.Min(member => member.Left) - BoundaryPadding;
            var top = members.Min(member => member.Top) - BoundaryPadding;
            var right = members.Max(member => member.Left + member.Width) + BoundaryPadding;
            var bottom = members.Max(member => member.Top + member.Height) + BoundaryPadding;
            boundary.SetBounds(left, top, right - left, bottom - top);
        }
        ResolveRootBoundaryOverlaps(
            entities,
            boundaries,
            entityParents,
            boundaryParents);

        var coordinateTransform = CreateCoordinateTransform(
            borders,
            connectors,
            layout,
            edgeLayoutKeys);
        ApplyCoordinateTransform(borders, coordinateTransform);
        TransformLineBoundaries(
            lineBoundaries,
            originalEntityFrame,
            GeometryFrame.From(entities));
        var routes = UpdateConnectors(
            connectors,
            entities,
            boundaries,
            layout,
            edgeLayoutKeys,
            coordinateTransform);
        ValidateCoordinates(surface);
        foreach (var border in borders)
            CommandHelpers.ClearLayoutParent(border);
        return routes;
    }

    private static void TransformLineBoundaries(
        IReadOnlyList<SerializableLineBoundary> lineBoundaries,
        GeometryFrame originalFrame,
        GeometryFrame finalFrame)
    {
        foreach (var lineBoundary in lineBoundaries)
        {
            var source = MapRelativePoint(
                new ModelRoutePoint(lineBoundary.X0, lineBoundary.Y0),
                originalFrame,
                finalFrame);
            var target = MapRelativePoint(
                new ModelRoutePoint(lineBoundary.X1, lineBoundary.Y1),
                originalFrame,
                finalFrame);
            var handle = MapRelativePoint(
                new ModelRoutePoint(lineBoundary.MPX, lineBoundary.MPY),
                originalFrame,
                finalFrame);
            lineBoundary.SetGeometry(
                source.X,
                source.Y,
                target.X,
                target.Y,
                handle.X,
                handle.Y,
                lineBoundary.PortSource,
                lineBoundary.PortTarget);
        }
    }

    private static ModelRoutePoint MapRelativePoint(
        ModelRoutePoint point,
        GeometryFrame originalFrame,
        GeometryFrame finalFrame)
    {
        var relativeX = (double)(point.X - originalFrame.Left) / originalFrame.Width;
        var relativeY = (double)(point.Y - originalFrame.Top) / originalFrame.Height;
        return new ModelRoutePoint(
            Math.Clamp(
                (int)Math.Round(finalFrame.Left + relativeX * finalFrame.Width),
                MinimumModelCoordinate,
                MaximumModelCoordinate),
            Math.Clamp(
                (int)Math.Round(finalFrame.Top + relativeY * finalFrame.Height),
                MinimumModelCoordinate,
                MaximumModelCoordinate));
    }

    private static GraphvizLayoutResult SelectAutomaticLayout(
        GraphvizLayoutResult horizontalLayout,
        GraphvizLayoutResult verticalLayout,
        IReadOnlyList<SerializableBorder> entities,
        IReadOnlyList<SerializableBorderBoundary> boundaries,
        IReadOnlyList<SerializableConnector> connectors,
        IReadOnlyDictionary<Guid, SerializableBorderBoundary?> entityParents,
        IReadOnlyDictionary<Guid, SerializableBorderBoundary?> boundaryParents,
        IReadOnlyDictionary<Guid, string> nodeIds,
        IReadOnlyDictionary<Guid, string> dummyIds)
    {
        var horizontalOverlaps = CountNodeOverlaps(horizontalLayout, entities, nodeIds);
        var verticalOverlaps = CountNodeOverlaps(verticalLayout, entities, nodeIds);
        var horizontalBoundaryOverlaps = CountRootBoundaryOverlaps(
            horizontalLayout,
            entities,
            boundaries,
            entityParents,
            boundaryParents,
            nodeIds,
            dummyIds);
        var verticalBoundaryOverlaps = CountRootBoundaryOverlaps(
            verticalLayout,
            entities,
            boundaries,
            entityParents,
            boundaryParents,
            nodeIds,
            dummyIds);
        var horizontalScore = horizontalBoundaryOverlaps * 100 + horizontalOverlaps;
        var verticalScore = verticalBoundaryOverlaps * 100 + verticalOverlaps;
        return
            verticalScore < horizontalScore ||
            (verticalScore == horizontalScore &&
             entities.Count >= 15 &&
             connectors.Count >= 25 &&
             horizontalLayout.ReadableScale < 0.95 &&
             verticalLayout.ReadableScale > horizontalLayout.ReadableScale * 1.1)
            ? verticalLayout
            : horizontalLayout;
    }

    private static string NormalizeRankDirection(string rankDirection)
    {
        var normalized = rankDirection.ToUpperInvariant();
        return normalized is "LR" or "RL" or "TB" or "BT"
            ? normalized
            : throw new ArgumentException(
                $"Unsupported Graphviz rank direction '{rankDirection}'.",
                nameof(rankDirection));
    }

    private static void ResolveRootBoundaryOverlaps(
        IReadOnlyList<SerializableBorder> entities,
        IReadOnlyList<SerializableBorderBoundary> boundaries,
        IReadOnlyDictionary<Guid, SerializableBorderBoundary?> entityParents,
        IReadOnlyDictionary<Guid, SerializableBorderBoundary?> boundaryParents)
    {
        var roots = boundaries
            .Where(boundary => boundaryParents[boundary.Guid] is null)
            .OrderBy(boundary => boundary.Top)
            .ThenBy(boundary => boundary.Left)
            .ThenBy(boundary => boundary.Guid)
            .ToList();
        if (roots.Count < 2)
            return;
        var maximumIterations = roots.Count * roots.Count * 2;
        for (var iteration = 0; iteration < maximumIterations; iteration++)
        {
            var resolved = true;
            for (var firstIndex = 0; firstIndex < roots.Count; firstIndex++)
            {
                var first = roots[firstIndex];
                for (var secondIndex = firstIndex + 1; secondIndex < roots.Count; secondIndex++)
                {
                    var second = roots[secondIndex];
                    var overlapX = Math.Min(
                        first.Left + first.Width,
                        second.Left + second.Width) - Math.Max(first.Left, second.Left);
                    var overlapY = Math.Min(
                        first.Top + first.Height,
                        second.Top + second.Height) - Math.Max(first.Top, second.Top);
                    if (overlapX <= 0 || overlapY <= 0)
                        continue;

                    const int separation = 24;
                    var moveRight = overlapX <= overlapY;
                    TranslateRootBoundary(
                        second,
                        moveRight ? overlapX + separation : 0,
                        moveRight ? 0 : overlapY + separation,
                        entities,
                        boundaries,
                        entityParents,
                        boundaryParents);
                    resolved = false;
                    break;
                }
                if (!resolved)
                    break;
            }
            if (resolved)
                return;
        }

        throw new InvalidDataException("Unable to separate Graphviz root boundary rectangles deterministically.");
    }

    private static void TranslateRootBoundary(
        SerializableBorderBoundary root,
        int deltaX,
        int deltaY,
        IReadOnlyList<SerializableBorder> entities,
        IReadOnlyList<SerializableBorderBoundary> boundaries,
        IReadOnlyDictionary<Guid, SerializableBorderBoundary?> entityParents,
        IReadOnlyDictionary<Guid, SerializableBorderBoundary?> boundaryParents)
    {
        foreach (var entity in entities)
        {
            if (FindRootBoundary(entityParents[entity.Guid], boundaryParents)?.Guid == root.Guid)
                Translate(entity, deltaX, deltaY);
        }
        foreach (var boundary in boundaries)
        {
            if (FindRootBoundary(boundary, boundaryParents)?.Guid == root.Guid)
                Translate(boundary, deltaX, deltaY);
        }
    }

    private static SerializableBorderBoundary? FindRootBoundary(
        SerializableBorderBoundary? boundary,
        IReadOnlyDictionary<Guid, SerializableBorderBoundary?> boundaryParents)
    {
        if (boundary is null)
            return null;
        var root = boundary;
        while (boundaryParents[root.Guid] is { } parent)
            root = parent;
        return root;
    }

    private static void Translate(SerializableBorder border, int deltaX, int deltaY)
    {
        border.SetBounds(
            border.Left + deltaX,
            border.Top + deltaY,
            border.Width,
            border.Height);
    }

    private static void ResolveEntityOverlaps(IReadOnlyList<SerializableBorder> entities)
    {
        var ordered = entities
            .OrderBy(entity => entity.Top)
            .ThenBy(entity => entity.Left)
            .ThenBy(entity => entity.Guid)
            .ToList();
        var maximumIterations = ordered.Count * ordered.Count * 2;
        for (var iteration = 0; iteration < maximumIterations; iteration++)
        {
            var resolved = true;
            for (var firstIndex = 0; firstIndex < ordered.Count; firstIndex++)
            {
                var first = ordered[firstIndex];
                for (var secondIndex = firstIndex + 1; secondIndex < ordered.Count; secondIndex++)
                {
                    var second = ordered[secondIndex];
                    var overlapX = Math.Min(
                        first.Left + first.Width,
                        second.Left + second.Width) - Math.Max(first.Left, second.Left);
                    var overlapY = Math.Min(
                        first.Top + first.Height,
                        second.Top + second.Height) - Math.Max(first.Top, second.Top);
                    if (overlapX <= 0 || overlapY <= 0)
                        continue;

                    const int separation = 12;
                    if (overlapY <= overlapX)
                    {
                        second.SetBounds(
                            second.Left,
                            second.Top + overlapY + separation,
                            second.Width,
                            second.Height);
                    }
                    else
                    {
                        second.SetBounds(
                            second.Left + overlapX + separation,
                            second.Top,
                            second.Width,
                            second.Height);
                    }
                    resolved = false;
                    break;
                }
                if (!resolved)
                    break;
            }
            if (resolved)
                return;
        }

        throw new InvalidDataException("Unable to separate Graphviz node rectangles deterministically.");
    }

    private static int CountNodeOverlaps(
        GraphvizLayoutResult layout,
        IReadOnlyList<SerializableBorder> entities,
        IReadOnlyDictionary<Guid, string> nodeIds)
    {
        var rectangles = entities
            .Select(entity => layout.GetModelBounds(nodeIds[entity.Guid]))
            .ToList();
        var overlaps = 0;
        for (var firstIndex = 0; firstIndex < rectangles.Count; firstIndex++)
        {
            var first = rectangles[firstIndex];
            for (var secondIndex = firstIndex + 1; secondIndex < rectangles.Count; secondIndex++)
            {
                var second = rectangles[secondIndex];
                if (first.Left < second.Left + second.Width &&
                    first.Left + first.Width > second.Left &&
                    first.Top < second.Top + second.Height &&
                    first.Top + first.Height > second.Top)
                {
                    overlaps++;
                }
            }
        }
        return overlaps;
    }

    private static int CountRootBoundaryOverlaps(
        GraphvizLayoutResult layout,
        IReadOnlyList<SerializableBorder> entities,
        IReadOnlyList<SerializableBorderBoundary> boundaries,
        IReadOnlyDictionary<Guid, SerializableBorderBoundary?> entityParents,
        IReadOnlyDictionary<Guid, SerializableBorderBoundary?> boundaryParents,
        IReadOnlyDictionary<Guid, string> nodeIds,
        IReadOnlyDictionary<Guid, string> dummyIds)
    {
        var entityBounds = entities.ToDictionary(
            entity => entity.Guid,
            entity => layout.GetModelBounds(nodeIds[entity.Guid]));
        var boundaryBounds = new Dictionary<Guid, ModelNodeBounds>();
        foreach (var boundary in OrderBoundariesDeepestFirst(boundaries, boundaryParents))
        {
            var members = entities
                .Where(entity => entityParents[entity.Guid]?.Guid == boundary.Guid)
                .Select(entity => entityBounds[entity.Guid])
                .Concat(boundaries
                    .Where(child => boundaryParents[child.Guid]?.Guid == boundary.Guid)
                    .Select(child => boundaryBounds[child.Guid]))
                .ToList();
            if (members.Count == 0)
            {
                var dummy = layout.GetModelBounds(dummyIds[boundary.Guid]);
                boundaryBounds.Add(
                    boundary.Guid,
                    new ModelNodeBounds(
                        dummy.Left - BoundaryPadding,
                        dummy.Top - BoundaryPadding,
                        dummy.Width + BoundaryPadding * 2,
                        dummy.Height + BoundaryPadding * 2));
                continue;
            }

            var left = members.Min(member => member.Left) - BoundaryPadding;
            var top = members.Min(member => member.Top) - BoundaryPadding;
            var right = members.Max(member => member.Left + member.Width) + BoundaryPadding;
            var bottom = members.Max(member => member.Top + member.Height) + BoundaryPadding;
            boundaryBounds.Add(
                boundary.Guid,
                new ModelNodeBounds(left, top, right - left, bottom - top));
        }

        var roots = boundaries
            .Where(boundary => boundaryParents[boundary.Guid] is null)
            .Select(boundary => boundaryBounds[boundary.Guid])
            .ToList();
        var overlaps = 0;
        for (var firstIndex = 0; firstIndex < roots.Count; firstIndex++)
        {
            var first = roots[firstIndex];
            for (var secondIndex = firstIndex + 1; secondIndex < roots.Count; secondIndex++)
            {
                var second = roots[secondIndex];
                if (first.Left < second.Left + second.Width &&
                    first.Left + first.Width > second.Left &&
                    first.Top < second.Top + second.Height &&
                    first.Top + first.Height > second.Top)
                {
                    overlaps++;
                }
            }
        }
        return overlaps;
    }

    private static ModelCoordinateTransform CreateCoordinateTransform(
        IReadOnlyList<SerializableBorder> borders,
        IReadOnlyList<SerializableConnector> connectors,
        GraphvizLayoutResult layout,
        IReadOnlyDictionary<Guid, EdgeLayoutKey> edgeLayoutKeys)
    {
        var points = new List<ModelRoutePoint>();
        foreach (var border in borders)
        {
            points.Add(new ModelRoutePoint(border.Left, border.Top));
            points.Add(new ModelRoutePoint(
                border.Left + border.Width,
                border.Top + border.Height));
        }

        foreach (var connector in connectors)
        {
            var key = edgeLayoutKeys[connector.Guid];
            points.AddRange(layout.GetModelRoute(key.SourceColor));
            points.AddRange(layout.GetModelRoute(key.TargetColor));
            var labelBounds = layout.GetModelBounds(key.LabelNodeId);
            points.Add(new ModelRoutePoint(labelBounds.Left, labelBounds.Top));
            points.Add(new ModelRoutePoint(
                labelBounds.Left + labelBounds.Width,
                labelBounds.Top + labelBounds.Height));
        }

        var minimumX = points.Min(point => point.X);
        var minimumY = points.Min(point => point.Y);
        var maximumX = points.Max(point => point.X);
        var maximumY = points.Max(point => point.Y);
        if (minimumX >= MinimumModelCoordinate &&
            minimumY >= MinimumModelCoordinate &&
            maximumX <= MaximumModelCoordinate &&
            maximumY <= MaximumModelCoordinate)
        {
            return ModelCoordinateTransform.Identity;
        }

        const int fittedMinimum = 10;
        const int fittedMaximum = 1440;
        var available = fittedMaximum - fittedMinimum;
        var width = Math.Max(1, maximumX - minimumX);
        var height = Math.Max(1, maximumY - minimumY);
        var scale = Math.Min(
            1.0,
            Math.Min((double)available / width, (double)available / height));
        var fittedWidth = width * scale;
        var fittedHeight = height * scale;
        var offsetX =
            fittedMinimum +
            (available - fittedWidth) / 2.0 -
            minimumX * scale;
        var offsetY =
            fittedMinimum +
            (available - fittedHeight) / 2.0 -
            minimumY * scale;
        return new ModelCoordinateTransform(scale, offsetX, offsetY);
    }

    private static void ApplyCoordinateTransform(
        IReadOnlyList<SerializableBorder> borders,
        ModelCoordinateTransform transform)
    {
        if (transform == ModelCoordinateTransform.Identity)
            return;

        foreach (var border in borders)
        {
            var topLeft = transform.Apply(new ModelRoutePoint(border.Left, border.Top));
            var bottomRight = transform.Apply(new ModelRoutePoint(
                border.Left + border.Width,
                border.Top + border.Height));
            border.SetBounds(
                topLeft.X,
                topLeft.Y,
                Math.Max(1, bottomRight.X - topLeft.X),
                Math.Max(1, bottomRight.Y - topLeft.Y));
        }
    }

    private static string BuildDot(
        SerializableDrawingSurfaceModel surface,
        IReadOnlyList<SerializableBorder> entities,
        IReadOnlyList<SerializableBorderBoundary> boundaries,
        IReadOnlyDictionary<Guid, SerializableBorderBoundary?> entityParents,
        IReadOnlyDictionary<Guid, SerializableBorderBoundary?> boundaryParents,
        IReadOnlyDictionary<Guid, string> nodeIds,
        IReadOnlyDictionary<Guid, string> dummyIds,
        IReadOnlyDictionary<Guid, EdgeLayoutKey> edgeLayoutKeys,
        string rankDirection)
    {
        var dot = new StringBuilder();
        var connectors = surface.Lines.Values
            .OfType<SerializableConnector>()
            .Where(connector =>
                nodeIds.ContainsKey(connector.SourceGuid) &&
                nodeIds.ContainsKey(connector.TargetGuid))
            .ToList();
        var maximumDegree = connectors
            .SelectMany(connector => new[] { connector.SourceGuid, connector.TargetGuid })
            .GroupBy(guid => guid)
            .Select(group => group.Count())
            .DefaultIfEmpty()
            .Max();
        var hubHeavy = maximumDegree >= 4;
        var nodeSeparation = hubHeavy ? 0.85 : 0.6;
        var rankSeparation = hubHeavy ? 1.8 : 1.0;
        dot.AppendLine("digraph tm7 {");
        dot.Append("  graph [rankdir=")
            .Append(rankDirection)
            .Append(", splines=ortho, concentrate=false, compound=true, newrank=true, remincross=true, nodesep=")
            .Append(nodeSeparation.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(", ranksep=")
            .Append(rankSeparation.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .AppendLine("];");

        foreach (var entity in entities.Where(entity => entityParents[entity.Guid] is null))
            WriteNode(dot, entity, nodeIds[entity.Guid], "  ");

        foreach (var boundary in boundaries.Where(boundary => boundaryParents[boundary.Guid] is null))
        {
            WriteBoundary(
                dot,
                boundary,
                entities,
                boundaries,
                entityParents,
                boundaryParents,
                nodeIds,
                dummyIds,
                "  ");
        }

        foreach (var connector in connectors)
        {
            var key = edgeLayoutKeys[connector.Guid];
            var name = CommandHelpers.GetEntityName(connector) ?? "Flow";
            var width = Math.Clamp(name.Length * 0.075, 1.0, 4.0);
            dot.Append("  ")
                .Append(key.LabelNodeId)
                .Append(" [label=\"\", shape=box, fixedsize=true, width=")
                .Append(width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                .AppendLine(", height=0.45, style=invis];");
        }

        foreach (var connector in connectors)
        {
            if (nodeIds.TryGetValue(connector.SourceGuid, out var sourceId) &&
                nodeIds.TryGetValue(connector.TargetGuid, out var targetId))
            {
                var key = edgeLayoutKeys[connector.Guid];
                dot.Append("  ")
                    .Append(sourceId)
                    .Append(" -> ")
                    .Append(key.LabelNodeId)
                    .Append(" [dir=none, color=\"")
                    .Append(key.SourceColor)
                    .AppendLine("\"];");
                dot.Append("  ")
                    .Append(key.LabelNodeId)
                    .Append(" -> ")
                    .Append(targetId)
                    .Append(" [dir=none, color=\"")
                    .Append(key.TargetColor)
                    .AppendLine("\"];");
            }
        }

        dot.AppendLine("}");
        return dot.ToString();
    }

    private static void WriteBoundary(
        StringBuilder dot,
        SerializableBorderBoundary boundary,
        IReadOnlyList<SerializableBorder> entities,
        IReadOnlyList<SerializableBorderBoundary> boundaries,
        IReadOnlyDictionary<Guid, SerializableBorderBoundary?> entityParents,
        IReadOnlyDictionary<Guid, SerializableBorderBoundary?> boundaryParents,
        IReadOnlyDictionary<Guid, string> nodeIds,
        IReadOnlyDictionary<Guid, string> dummyIds,
        string indent)
    {
        dot.Append(indent)
            .Append("subgraph cluster_")
            .Append(boundary.Guid.ToString("N"))
            .AppendLine(" {");
        dot.Append(indent)
            .Append("  label=\"")
            .Append(EscapeLabel(CommandHelpers.GetEntityName(boundary) ?? "Trust Boundary"))
            .AppendLine("\";");
        dot.Append(indent).AppendLine("  margin=50;");

        var directEntities = entities
            .Where(entity => entityParents[entity.Guid]?.Guid == boundary.Guid)
            .ToList();
        var childBoundaries = boundaries
            .Where(child => boundaryParents[child.Guid]?.Guid == boundary.Guid)
            .ToList();

        foreach (var entity in directEntities)
            WriteNode(dot, entity, nodeIds[entity.Guid], indent + "  ");
        foreach (var child in childBoundaries)
        {
            WriteBoundary(
                dot,
                child,
                entities,
                boundaries,
                entityParents,
                boundaryParents,
                nodeIds,
                dummyIds,
                indent + "  ");
        }

        if (directEntities.Count == 0 && childBoundaries.Count == 0)
        {
            dot.Append(indent)
                .Append("  ")
                .Append(dummyIds[boundary.Guid])
                .AppendLine(" [label=\"\", style=invis];");
        }

        dot.Append(indent).AppendLine("}");
    }

    private static void WriteNode(
        StringBuilder dot,
        SerializableBorder entity,
        string nodeId,
        string indent)
    {
        var name = CommandHelpers.GetEntityName(entity) ?? entity.Guid.ToString();
        var width = Math.Clamp(name.Length * 0.065, 1.4, 2.4);
        dot.Append(indent)
            .Append(nodeId)
            .Append(" [label=\"")
            .Append(EscapeLabel(name))
            .Append("\", fixedsize=true, width=")
            .Append(width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
            .AppendLine(", height=0.65];");
    }

    private static string EscapeLabel(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static SerializableBorderBoundary? FindSmallestContainingBoundary(
        SerializableBorder border,
        IEnumerable<SerializableBorderBoundary> boundaries)
    {
        var centerX = border.Left + border.Width / 2;
        var centerY = border.Top + border.Height / 2;
        return boundaries
            .Where(boundary =>
                centerX > boundary.Left &&
                centerX < boundary.Left + boundary.Width &&
                centerY > boundary.Top &&
                centerY < boundary.Top + boundary.Height)
            .OrderBy(boundary => (long)boundary.Width * boundary.Height)
            .FirstOrDefault();
    }

    private static SerializableBorderBoundary? ResolveParentBoundary(
        SerializableBorder border,
        IReadOnlyList<SerializableBorderBoundary> candidates,
        IReadOnlyDictionary<Guid, SerializableBorderBoundary> boundariesById)
    {
        var explicitParent = CommandHelpers.GetLayoutParent(border);
        if (explicitParent is not null &&
            explicitParent != border.Guid &&
            boundariesById.TryGetValue(explicitParent.Value, out var boundary) &&
            candidates.Any(candidate => candidate.Guid == boundary.Guid))
        {
            return boundary;
        }

        return FindSmallestContainingBoundary(border, candidates);
    }

    private static IEnumerable<SerializableBorderBoundary> OrderBoundariesDeepestFirst(
        IReadOnlyList<SerializableBorderBoundary> boundaries,
        IReadOnlyDictionary<Guid, SerializableBorderBoundary?> boundaryParents)
    {
        return boundaries.OrderByDescending(boundary =>
        {
            var depth = 0;
            var parent = boundaryParents[boundary.Guid];
            while (parent is not null)
            {
                depth++;
                parent = boundaryParents[parent.Guid];
            }
            return depth;
        });
    }

    private static IReadOnlyDictionary<Guid, IReadOnlyList<ModelRoutePoint>> UpdateConnectors(
        IReadOnlyList<SerializableConnector> connectors,
        IReadOnlyList<SerializableBorder> entities,
        IReadOnlyList<SerializableBorderBoundary> boundaries,
        GraphvizLayoutResult layout,
        IReadOnlyDictionary<Guid, EdgeLayoutKey> edgeLayoutKeys,
        ModelCoordinateTransform coordinateTransform)
    {
        var entitiesById = entities.ToDictionary(entity => entity.Guid);
        var routes = new Dictionary<Guid, IReadOnlyList<ModelRoutePoint>>();
        var routedConnectors = new List<RoutedConnector>();
        foreach (var connector in connectors)
        {
            if (!entitiesById.TryGetValue(connector.SourceGuid, out var source) ||
                !entitiesById.TryGetValue(connector.TargetGuid, out var target))
            {
                continue;
            }

            var key = edgeLayoutKeys[connector.Guid];
            var sourceRoute = layout.GetModelRoute(key.SourceColor)
                .Select(coordinateTransform.Apply)
                .ToList();
            var targetRoute = layout.GetModelRoute(key.TargetColor)
                .Select(coordinateTransform.Apply)
                .ToList();
            var labelBounds = layout.GetModelBounds(key.LabelNodeId);
            var labelCenter = coordinateTransform.Apply(new ModelRoutePoint(
                labelBounds.Left + labelBounds.Width / 2,
                labelBounds.Top + labelBounds.Height / 2));
            var graphvizRoute = sourceRoute
                .Concat([labelCenter])
                .Concat(targetRoute)
                .ToList();
            if (graphvizRoute.Count < 2)
                throw new InvalidDataException($"Graphviz returned an incomplete route for connector '{connector.Guid}'.");

            var sourceEndpoint = SnapToBorder(source, graphvizRoute[0]);
            var targetEndpoint = SnapToBorder(target, graphvizRoute[^1]);
            graphvizRoute[0] = new ModelRoutePoint(sourceEndpoint.X, sourceEndpoint.Y);
            graphvizRoute[^1] = new ModelRoutePoint(targetEndpoint.X, targetEndpoint.Y);
            var route = OrthogonalizeRoute(RemoveConsecutiveDuplicates(graphvizRoute));
            routedConnectors.Add(new RoutedConnector(
                connector,
                source,
                target,
                route,
                labelCenter,
                sourceEndpoint,
                targetEndpoint));
        }

        var laneOffsets = new Dictionary<Guid, double>();
        foreach (var group in routedConnectors.GroupBy(item =>
                     (item.Connector.SourceGuid, item.Connector.TargetGuid)))
        {
            var ordered = group.ToList();
            for (var index = 0; index < ordered.Count; index++)
                laneOffsets.Add(
                    ordered[index].Connector.Guid,
                    (index - (ordered.Count - 1) / 2.0) * ParallelLaneSpacing);
        }

        var fixedObstacles = CreateLabelObstacles(entities, boundaries);
        var occupiedLabels = new List<LayoutRectangle>();
        foreach (var routed in routedConnectors
                     .OrderByDescending(item => (CommandHelpers.GetEntityName(item.Connector) ?? "").Length))
        {
            var hasReverse = routedConnectors.Any(candidate =>
                candidate.Connector.SourceGuid == routed.Connector.TargetGuid &&
                candidate.Connector.TargetGuid == routed.Connector.SourceGuid);
            var preferredHandle = SelectBoundedHandle(
                routed.Route,
                routed.Source,
                routed.Target,
                laneOffsets[routed.Connector.Guid],
                hasReverse,
                routed.PreferredHandle);
            var handle = PlaceLabelHandle(
                routed,
                preferredHandle,
                fixedObstacles,
                occupiedLabels);
            var sourceEndpoint = routed.Source.Guid == routed.Target.Guid
                ? routed.SourceEndpoint
                : FindClosestPerimeterPoint(routed.Source, handle);
            var targetEndpoint = routed.Source.Guid == routed.Target.Guid
                ? routed.TargetEndpoint
                : FindClosestPerimeterPoint(routed.Target, handle);
            handle = routed.Source.Guid == routed.Target.Guid
                ? handle
                : ClampHandleOffset(handle, sourceEndpoint, targetEndpoint);
            var route = routed.Route.ToList();
            route[0] = new ModelRoutePoint(sourceEndpoint.X, sourceEndpoint.Y);
            route[^1] = new ModelRoutePoint(targetEndpoint.X, targetEndpoint.Y);
            routes.Add(
                routed.Connector.Guid,
                OrthogonalizeRoute(RemoveConsecutiveDuplicates(route)));

            routed.Connector.SetGeometry(
                sourceEndpoint.X,
                sourceEndpoint.Y,
                targetEndpoint.X,
                targetEndpoint.Y,
                Math.Clamp(handle.X, MinimumModelCoordinate, MaximumModelCoordinate),
                Math.Clamp(handle.Y, MinimumModelCoordinate, MaximumModelCoordinate),
                sourceEndpoint.Port,
                targetEndpoint.Port);
            occupiedLabels.Add(EstimateLabelRectangle(routed.Connector, handle));
        }

        return routes;
    }

    private static ModelRoutePoint ClampHandleOffset(
        ModelRoutePoint handle,
        EndpointGeometry source,
        EndpointGeometry target)
    {
        var midpointX = (source.X + target.X) / 2.0;
        var midpointY = (source.Y + target.Y) / 2.0;
        var offsetX = handle.X - midpointX;
        var offsetY = handle.Y - midpointY;
        var length = Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
        if (length <= MaximumHandleOffset)
            return handle;

        var scale = MaximumHandleOffset / length;
        return new ModelRoutePoint(
            (int)Math.Round(midpointX + offsetX * scale),
            (int)Math.Round(midpointY + offsetY * scale));
    }

    private static EndpointGeometry FindClosestPerimeterPoint(
        SerializableBorder entity,
        ModelRoutePoint handle)
    {
        var centerX = entity.Left + entity.Width / 2.0;
        var centerY = entity.Top + entity.Height / 2.0;
        var deltaX = handle.X - centerX;
        var deltaY = handle.Y - centerY;
        if (Math.Abs(deltaX) < 0.001 && Math.Abs(deltaY) < 0.001)
            deltaX = 1;

        var halfWidth = Math.Max(1.0, entity.Width / 2.0);
        var halfHeight = Math.Max(1.0, entity.Height / 2.0);
        double scale;
        if (entity is SerializableStencilEllipse)
        {
            scale = 1.0 / Math.Sqrt(
                deltaX * deltaX / (halfWidth * halfWidth) +
                deltaY * deltaY / (halfHeight * halfHeight));
        }
        else
        {
            scale = Math.Min(
                halfWidth / Math.Max(Math.Abs(deltaX), 0.001),
                halfHeight / Math.Max(Math.Abs(deltaY), 0.001));
        }

        var x = Math.Clamp(
            (int)Math.Round(centerX + deltaX * scale),
            entity.Left,
            entity.Left + entity.Width);
        var y = Math.Clamp(
            (int)Math.Round(centerY + deltaY * scale),
            entity.Top,
            entity.Top + entity.Height);
        var normalizedX = deltaX / halfWidth;
        var normalizedY = deltaY / halfHeight;
        var port = GetNearestPort(normalizedX, normalizedY);
        return new EndpointGeometry(x, y, port);
    }

    private static StencilConnectionPort GetNearestPort(
        double normalizedX,
        double normalizedY)
    {
        var angle = Math.Atan2(normalizedY, normalizedX) * 180.0 / Math.PI;
        if (angle < 0)
            angle += 360;
        var sector = (int)Math.Round(angle / 45.0) % 8;
        return sector switch
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
    }

    private static List<LayoutRectangle> CreateLabelObstacles(
        IReadOnlyList<SerializableBorder> entities,
        IReadOnlyList<SerializableBorderBoundary> boundaries)
    {
        var obstacles = entities
            .Select(entity => new LayoutRectangle(
                entity.Left - 10,
                entity.Top - 10,
                entity.Width + 20,
                entity.Height + 20))
            .ToList();
        foreach (var boundary in boundaries)
        {
            var name = CommandHelpers.GetEntityName(boundary) ?? "Trust Boundary";
            var width = Math.Clamp(name.Length * 7 + 30, 100, Math.Max(100, boundary.Width));
            obstacles.Add(new LayoutRectangle(
                boundary.Left + boundary.Width - width,
                boundary.Top - 10,
                width,
                36));
        }
        return obstacles;
    }

    private static ModelRoutePoint PlaceLabelHandle(
        RoutedConnector routed,
        ModelRoutePoint preferred,
        IReadOnlyList<LayoutRectangle> fixedObstacles,
        IReadOnlyList<LayoutRectangle> occupiedLabels)
    {
        var source = routed.Route[0];
        var target = routed.Route[^1];
        var chordX = target.X - source.X;
        var chordY = target.Y - source.Y;
        var chordLength = Math.Sqrt((double)chordX * chordX + (double)chordY * chordY);
        if (chordLength == 0)
            return preferred;

        var unitX = chordX / chordLength;
        var unitY = chordY / chordLength;
        var perpendicularX = -unitY;
        var perpendicularY = unitX;
        var midpointX = (source.X + target.X) / 2.0;
        var midpointY = (source.Y + target.Y) / 2.0;
        var preferredAlong =
            (preferred.X - midpointX) * unitX +
            (preferred.Y - midpointY) * unitY;
        var preferredPerpendicular =
            (preferred.X - midpointX) * perpendicularX +
            (preferred.Y - midpointY) * perpendicularY;
        var maximumAlong = Math.Min(chordLength * 0.45, 300.0);
        var alongOffsets = new List<double>
        {
            Math.Clamp(preferredAlong, -maximumAlong, maximumAlong),
        };
        for (var step = -10; step <= 10; step++)
            alongOffsets.Add(maximumAlong * step / 10.0);
        alongOffsets = alongOffsets.Distinct().ToList();

        var perpendicularOffsets = new List<double>
        {
            Math.Clamp(preferredPerpendicular, -MaximumHandleOffset, MaximumHandleOffset),
        };
        for (var offset = -MaximumHandleOffset; offset <= MaximumHandleOffset; offset += 20)
            perpendicularOffsets.Add(offset);
        perpendicularOffsets = perpendicularOffsets.Distinct().ToList();

        var candidates = new List<(ModelRoutePoint Point, double Score)>();
        foreach (var along in alongOffsets)
        {
            foreach (var perpendicular in perpendicularOffsets)
            {
                var proposedPoint = new ModelRoutePoint(
                    (int)Math.Round(midpointX + unitX * along + perpendicularX * perpendicular),
                    (int)Math.Round(midpointY + unitY * along + perpendicularY * perpendicular));
                var sourceEndpoint = routed.Source.Guid == routed.Target.Guid
                    ? routed.SourceEndpoint
                    : FindClosestPerimeterPoint(routed.Source, proposedPoint);
                var targetEndpoint = routed.Source.Guid == routed.Target.Guid
                    ? routed.TargetEndpoint
                    : FindClosestPerimeterPoint(routed.Target, proposedPoint);
                var point = routed.Source.Guid == routed.Target.Guid
                    ? proposedPoint
                    : ClampHandleOffset(
                        proposedPoint,
                        sourceEndpoint,
                        targetEndpoint);
                var rectangle = EstimateLabelRectangle(routed.Connector, point);
                var score =
                    DistanceSquared(point, preferred) +
                    Math.Abs(perpendicular) * 2 +
                    Math.Abs(along) * 0.25;
                if (rectangle.Left < MinimumModelCoordinate ||
                    rectangle.Top < MinimumModelCoordinate ||
                    rectangle.Right > MaximumModelCoordinate ||
                    rectangle.Bottom > MaximumModelCoordinate)
                {
                    score += 10_000_000;
                }
                score += fixedObstacles.Count(rectangle.Intersects) * 1_000_000;
                score += occupiedLabels.Count(rectangle.Intersects) * 2_000_000;
                candidates.Add((point, score));
            }
        }

        return candidates
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Point.X)
            .ThenBy(candidate => candidate.Point.Y)
            .First()
            .Point;
    }

    private static LayoutRectangle EstimateLabelRectangle(
        SerializableConnector connector,
        ModelRoutePoint handle)
    {
        var name = CommandHelpers.GetEntityName(connector) ?? "Flow";
        var width = Math.Clamp(name.Length * 6 + 32, 88, 328);
        const int height = 42;
        return new LayoutRectangle(
            handle.X - width / 2.0,
            handle.Y - height / 2.0,
            width,
            height);
    }

    private static double DistanceSquared(ModelRoutePoint first, ModelRoutePoint second)
    {
        var deltaX = first.X - second.X;
        var deltaY = first.Y - second.Y;
        return (double)deltaX * deltaX + (double)deltaY * deltaY;
    }

    private static void ValidateCoordinates(SerializableDrawingSurfaceModel surface)
    {
        foreach (var border in surface.Borders.Values.OfType<SerializableBorder>())
        {
            if (border.Left < MinimumModelCoordinate ||
                border.Top < MinimumModelCoordinate ||
                border.Left + border.Width > MaximumModelCoordinate ||
                border.Top + border.Height > MaximumModelCoordinate)
            {
                throw new InvalidDataException(
                    $"Graphviz placed '{CommandHelpers.GetEntityName(border) ?? border.Guid.ToString()}' " +
                    $"outside the supported TM7 coordinate range {MinimumModelCoordinate}..{MaximumModelCoordinate}.");
            }
        }

        foreach (var line in surface.Lines.Values.OfType<SerializableLine>())
        {
            var coordinates = new[] { line.X0, line.Y0, line.X1, line.Y1, line.MPX, line.MPY };
            if (coordinates.Any(value =>
                    value < MinimumModelCoordinate ||
                    value > MaximumModelCoordinate))
            {
                throw new InvalidDataException(
                    $"Graphviz routed connector '{line.Guid}' outside the supported TM7 coordinate range " +
                    $"{MinimumModelCoordinate}..{MaximumModelCoordinate}.");
            }
        }
    }

    private static EndpointGeometry SnapToBorder(
        SerializableBorder entity,
        ModelRoutePoint point)
    {
        var leftDistance = Math.Abs(point.X - entity.Left);
        var rightDistance = Math.Abs(point.X - (entity.Left + entity.Width));
        var topDistance = Math.Abs(point.Y - entity.Top);
        var bottomDistance = Math.Abs(point.Y - (entity.Top + entity.Height));
        var minimum = Math.Min(Math.Min(leftDistance, rightDistance), Math.Min(topDistance, bottomDistance));

        if (minimum == leftDistance)
        {
            return new EndpointGeometry(
                entity.Left,
                Math.Clamp(point.Y, entity.Top, entity.Top + entity.Height),
                StencilConnectionPort.West);
        }

        if (minimum == rightDistance)
        {
            return new EndpointGeometry(
                entity.Left + entity.Width,
                Math.Clamp(point.Y, entity.Top, entity.Top + entity.Height),
                StencilConnectionPort.East);
        }

        if (minimum == topDistance)
        {
            return new EndpointGeometry(
                Math.Clamp(point.X, entity.Left, entity.Left + entity.Width),
                entity.Top,
                StencilConnectionPort.North);
        }

        return new EndpointGeometry(
            Math.Clamp(point.X, entity.Left, entity.Left + entity.Width),
            entity.Top + entity.Height,
            StencilConnectionPort.South);
    }

    private static IReadOnlyList<ModelRoutePoint> RemoveConsecutiveDuplicates(
        IReadOnlyList<ModelRoutePoint> points)
    {
        var result = new List<ModelRoutePoint>(points.Count);
        foreach (var point in points)
        {
            if (result.Count == 0 || result[^1] != point)
                result.Add(point);
        }
        return result;
    }

    private static IReadOnlyList<ModelRoutePoint> OrthogonalizeRoute(
        IReadOnlyList<ModelRoutePoint> points)
    {
        var result = new List<ModelRoutePoint> { points[0] };
        for (var index = 1; index < points.Count; index++)
        {
            var previous = result[^1];
            var current = points[index];
            if (previous.X != current.X && previous.Y != current.Y)
                result.Add(new ModelRoutePoint(current.X, previous.Y));
            if (result[^1] != current)
                result.Add(current);
        }
        return RemoveConsecutiveDuplicates(result);
    }

    private static ModelRoutePoint SelectBoundedHandle(
        IReadOnlyList<ModelRoutePoint> route,
        SerializableBorder source,
        SerializableBorder target,
        double laneOffset,
        bool hasReverse,
        ModelRoutePoint preferredHandle)
    {
        if (source.Guid == target.Guid)
            return SelectSelfLoopHandle(route, source, laneOffset);

        var sourcePoint = route[0];
        var targetPoint = route[^1];
        var midpointX = (sourcePoint.X + targetPoint.X) / 2.0;
        var midpointY = (sourcePoint.Y + targetPoint.Y) / 2.0;
        var routeMidpoint = preferredHandle;
        var offsetX = routeMidpoint.X - midpointX;
        var offsetY = routeMidpoint.Y - midpointY;
        var chordX = targetPoint.X - sourcePoint.X;
        var chordY = targetPoint.Y - sourcePoint.Y;
        var chordLength = Math.Sqrt((double)chordX * chordX + (double)chordY * chordY);

        if (chordLength > 0)
        {
            var perpendicularX = -chordY / chordLength;
            var perpendicularY = chordX / chordLength;
            var reverseOffset = hasReverse ? ParallelLaneSpacing / 2.0 : 0.0;
            offsetX += perpendicularX * (laneOffset + reverseOffset);
            offsetY += perpendicularY * (laneOffset + reverseOffset);
        }

        var offsetLength = Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
        if (offsetLength > MaximumHandleOffset)
        {
            var scale = MaximumHandleOffset / offsetLength;
            offsetX *= scale;
            offsetY *= scale;
        }

        return new ModelRoutePoint(
            (int)Math.Round(midpointX + offsetX),
            (int)Math.Round(midpointY + offsetY));
    }

    private static ModelRoutePoint PointAtHalfLength(IReadOnlyList<ModelRoutePoint> route)
    {
        var lengths = new double[route.Count - 1];
        var totalLength = 0.0;
        for (var index = 0; index < route.Count - 1; index++)
        {
            var deltaX = route[index + 1].X - route[index].X;
            var deltaY = route[index + 1].Y - route[index].Y;
            lengths[index] = Math.Sqrt((double)deltaX * deltaX + (double)deltaY * deltaY);
            totalLength += lengths[index];
        }

        var targetLength = totalLength / 2.0;
        var traversed = 0.0;
        for (var index = 0; index < lengths.Length; index++)
        {
            if (traversed + lengths[index] >= targetLength)
            {
                var fraction = lengths[index] == 0
                    ? 0
                    : (targetLength - traversed) / lengths[index];
                return new ModelRoutePoint(
                    (int)Math.Round(route[index].X + (route[index + 1].X - route[index].X) * fraction),
                    (int)Math.Round(route[index].Y + (route[index + 1].Y - route[index].Y) * fraction));
            }
            traversed += lengths[index];
        }

        return route[^1];
    }

    private static ModelRoutePoint SelectSelfLoopHandle(
        IReadOnlyList<ModelRoutePoint> route,
        SerializableBorder entity,
        double laneOffset)
    {
        var centerX = entity.Left + entity.Width / 2.0;
        var centerY = entity.Top + entity.Height / 2.0;
        var farthest = route
            .OrderByDescending(point =>
                (point.X - centerX) * (point.X - centerX) +
                (point.Y - centerY) * (point.Y - centerY))
            .First();
        var offsetX = farthest.X - centerX;
        var offsetY = farthest.Y - centerY;
        var offsetLength = Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
        var targetLength = Math.Max(entity.Width, entity.Height) / 2.0 + 70.0 + Math.Abs(laneOffset);
        if (offsetLength == 0)
            return new ModelRoutePoint((int)Math.Round(centerX + targetLength), (int)Math.Round(centerY));

        return new ModelRoutePoint(
            (int)Math.Round(centerX + offsetX / offsetLength * targetLength),
            (int)Math.Round(centerY + offsetY / offsetLength * targetLength));
    }

    private sealed record RoutedConnector(
        SerializableConnector Connector,
        SerializableBorder Source,
        SerializableBorder Target,
        IReadOnlyList<ModelRoutePoint> Route,
        ModelRoutePoint PreferredHandle,
        EndpointGeometry SourceEndpoint,
        EndpointGeometry TargetEndpoint);

    private sealed record EdgeLayoutKey(
        string SourceColor,
        string TargetColor,
        string LabelNodeId);

    private readonly record struct EndpointGeometry(
        int X,
        int Y,
        StencilConnectionPort Port);

    private readonly record struct LayoutRectangle(
        double Left,
        double Top,
        double Width,
        double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;

        public bool Intersects(LayoutRectangle other)
        {
            return
                Left < other.Right &&
                Right > other.Left &&
                Top < other.Bottom &&
                Bottom > other.Top;
        }
    }

    private readonly record struct ModelCoordinateTransform(
        double Scale,
        double OffsetX,
        double OffsetY)
    {
        public static ModelCoordinateTransform Identity { get; } = new(1, 0, 0);

        public ModelRoutePoint Apply(ModelRoutePoint point)
        {
            return new ModelRoutePoint(
                (int)Math.Round(point.X * Scale + OffsetX),
                (int)Math.Round(point.Y * Scale + OffsetY));
        }
    }

    private readonly record struct GeometryFrame(
        int Left,
        int Top,
        int Width,
        int Height)
    {
        public static GeometryFrame From(IReadOnlyList<SerializableBorder> borders)
        {
            var left = borders.Min(border => border.Left);
            var top = borders.Min(border => border.Top);
            var right = borders.Max(border => border.Left + border.Width);
            var bottom = borders.Max(border => border.Top + border.Height);
            return new GeometryFrame(
                left,
                top,
                Math.Max(1, right - left),
                Math.Max(1, bottom - top));
        }
    }
}
