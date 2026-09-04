using Hex1b.Surfaces;
using Hex1b.Theming;
using Tm7.Cli.Layout;
using Tm7.Cli.Model;

namespace Tm7.Cli;

/// <summary>
/// Renders a .tm7 threat model diagram to terminal using Hex1b Surface and Unicode box-drawing characters.
/// Produces AI-readable text output showing entity positions, types, and flow connections.
/// </summary>
public sealed class Tm7Renderer
{
    private readonly Surface _surface;
    private readonly int _width;
    private readonly int _height;

    // Coordinate mapping from model space to terminal cells
    private readonly double _scaleX;
    private readonly double _scaleY;
    private readonly int _offsetX;
    private readonly int _offsetY;

    // Track entity boxes in terminal space to avoid drawing lines through them
    private readonly List<(int left, int top, int right, int bottom)> _entityRects = [];
    private readonly List<(int x, int y, char arrow)> _flowArrows = [];

    // Colors
    private static readonly Hex1bColor ProcessColor = Hex1bColor.Cyan;
    private static readonly Hex1bColor ExternalColor = Hex1bColor.Yellow;
    private static readonly Hex1bColor DataStoreColor = Hex1bColor.Green;
    private static readonly Hex1bColor BoundaryColor = Hex1bColor.Red;
    private static readonly Hex1bColor FlowColor = Hex1bColor.DarkGray;
    private static readonly Hex1bColor ArrowColor = Hex1bColor.FromRgb(220, 160, 40);
    private static readonly Hex1bColor LabelColor = Hex1bColor.White;
    private static readonly Hex1bColor DimColor = Hex1bColor.Gray;

    private Tm7Renderer(int width, int height, double scaleX, double scaleY, int offsetX, int offsetY)
    {
        _width = width;
        _height = height;
        _scaleX = scaleX;
        _scaleY = scaleY;
        _offsetX = offsetX;
        _offsetY = offsetY;
        _surface = new Surface(width, height);
    }

    /// <summary>
    /// Render a tm7 model's first drawing surface to a terminal string.
    /// </summary>
    public static string Render(SerializableModelData model, int termWidth = 200, int termHeight = 55, bool plain = false)
    {
        return Render(model, termWidth, termHeight, plain, null);
    }

    internal static string Render(
        SerializableModelData model,
        int termWidth,
        int termHeight,
        bool plain,
        IReadOnlyDictionary<Guid, IReadOnlyList<ModelRoutePoint>>? routes)
    {
        if (model.DrawingSurfaceList is null || model.DrawingSurfaceList.Count == 0)
            return "(no drawing surfaces)";

        var drawSurface = model.DrawingSurfaceList[0];
        if (drawSurface.Borders is null || drawSurface.Borders.Count == 0)
            return "(no entities)";

        // Compute bounding box of all entities (including boundaries)
        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;

        foreach (var kvp in drawSurface.Borders)
        {
            if (kvp.Value is SerializableBorder border)
            {
                minX = Math.Min(minX, border.Left);
                minY = Math.Min(minY, border.Top);
                maxX = Math.Max(maxX, border.Left + border.Width);
                maxY = Math.Max(maxY, border.Top + border.Height);
            }
        }

        if (minX == int.MaxValue) return "(no entities)";

        // Add padding
        minX -= 30; minY -= 20;
        maxX += 30; maxY += 20;

        int modelW = maxX - minX;
        int modelH = maxY - minY;

        // Reserve rows: 1 for title, 1 for blank, 1 for legend at bottom
        int drawHeight = termHeight - 3;

        double scaleX = (double)(termWidth - 2) / modelW;
        double scaleY = (double)(drawHeight - 1) / modelH;

        var renderer = new Tm7Renderer(termWidth, termHeight, scaleX, scaleY, minX, minY);

        // Draw title
        var title = model.MetaInformation?.ThreatModelName ?? "Threat Model";
        renderer._surface.WriteText(Math.Max(0, termWidth / 2 - title.Length / 2), 0, title, LabelColor);

        // Draw boundary first (background layer)
        foreach (var kvp in drawSurface.Borders)
        {
            if (kvp.Value is SerializableBorderBoundary boundary)
                renderer.DrawBoundary(boundary);
        }

        // Pre-compute entity positions (needed for flow routing)
        var entityPositions = new Dictionary<Guid, EntityRect>();
        var entityInfo = new List<(SerializableBorder border, string name, string type)>();
        foreach (var kvp in drawSurface.Borders)
        {
            if (kvp.Value is SerializableBorder border && border is not SerializableBorderBoundary)
            {
                var name = GetEntityName(border) ?? "(unnamed)";
                var type = border.GenericTypeId ?? "GE.P";
                var rect = renderer.ComputeEntityRect(border, name, type);
                entityPositions[border.Guid] = rect;
                renderer._entityRects.Add((rect.Left, rect.Top, rect.Left + rect.Width - 1, rect.Top + rect.Height - 1));
                entityInfo.Add((border, name, type));
            }
        }

        // Draw flows FIRST (background layer, under entities)
        if (drawSurface.Lines is not null)
        {
            foreach (var kvp in drawSurface.Lines)
            {
                if (kvp.Value is SerializableConnector conn)
                {
                    if (entityPositions.TryGetValue(conn.SourceGuid, out var src) &&
                        entityPositions.TryGetValue(conn.TargetGuid, out var tgt))
                    {
                        IReadOnlyList<ModelRoutePoint>? route = null;
                        if (routes is not null)
                            routes.TryGetValue(conn.Guid, out route);
                        renderer.DrawFlow(src, tgt, route);
                    }
                }
            }
        }
        renderer.DrawFlowArrows();

        // Draw entities ON TOP of flows (so labels and borders are never obscured)
        foreach (var (border, name, type) in entityInfo)
            renderer.DrawEntity(border, name, type);

        // Draw legend
        renderer.DrawLegend(termHeight - 1);

        return plain ? renderer.ToPlainString() : renderer.ToAnsiString();
    }

    private record struct EntityRect(int Left, int Top, int Width, int Height, string Type)
    {
        public readonly int CenterX => Left + Width / 2;
        public readonly int CenterY => Top + Height / 2;
        public readonly int Right => Left + Width - 1;
        public readonly int Bottom => Top + Height - 1;
    }

    // ── coordinate mapping ──────────────────────────────────────────────

    private int MapX(int modelX) => Math.Clamp((int)((modelX - _offsetX) * _scaleX), 0, _width - 1);
    private int MapY(int modelY) => Math.Clamp((int)((modelY - _offsetY) * _scaleY) + 2, 0, _height - 1);

    // ── drawing primitives ──────────────────────────────────────────────

    private void WriteChar(int x, int y, char ch, Hex1bColor color)
    {
        if (x >= 0 && x < _width && y >= 0 && y < _height)
            _surface.WriteChar(x, y, ch, color);
    }

    private void WriteText(int x, int y, string text, Hex1bColor color)
    {
        if (y >= 0 && y < _height)
            _surface.WriteText(Math.Max(0, x), y, text, color);
    }

    private void DrawHLine(int x1, int x2, int y, char ch, Hex1bColor color)
    {
        int start = Math.Max(0, Math.Min(x1, x2));
        int end = Math.Min(_width - 1, Math.Max(x1, x2));
        for (int x = start; x <= end; x++)
            _surface.WriteChar(x, y, ch, color);
    }

    private void DrawVLine(int x, int y1, int y2, char ch, Hex1bColor color)
    {
        int start = Math.Max(0, Math.Min(y1, y2));
        int end = Math.Min(_height - 1, Math.Max(y1, y2));
        for (int y = start; y <= end; y++)
            _surface.WriteChar(x, y, ch, color);
    }

    // ── entity drawing ──────────────────────────────────────────────────

    private EntityRect ComputeEntityRect(SerializableBorder border, string name, string genericTypeId)
    {
        int tl = MapX(border.Left);
        int tt = MapY(border.Top);
        int tr = MapX(border.Left + border.Width);
        int tb = MapY(border.Top + border.Height);

        int tw = Math.Max(tr - tl, Math.Min(name.Length + 4, 28));
        tw = Math.Max(tw, 14);
        int th = Math.Max(tb - tt, 3);

        return new EntityRect(tl, tt, tw, th, genericTypeId);
    }

    private void DrawEntity(SerializableBorder border, string name, string genericTypeId)
    {
        var rect = ComputeEntityRect(border, name, genericTypeId);

        // Clear entity area first so flow lines underneath don't show through
        for (int r = rect.Top; r <= rect.Bottom; r++)
            for (int c = rect.Left; c <= rect.Right; c++)
                WriteChar(c, r, ' ', LabelColor);

        switch (genericTypeId)
        {
            case "GE.P":
                DrawProcessBox(rect.Left, rect.Top, rect.Width, rect.Height, name);
                break;
            case "GE.EI":
                DrawExternalBox(rect.Left, rect.Top, rect.Width, rect.Height, name);
                break;
            case "GE.DS":
                DrawDataStoreBox(rect.Left, rect.Top, rect.Width, rect.Height, name);
                break;
            case "GE.A":
                // Annotation: just render text, no box
                WriteLabelLines(rect.Left, rect.Top, rect.Width, rect.Height, name, DimColor);
                break;
            default:
                DrawExternalBox(rect.Left, rect.Top, rect.Width, rect.Height, name);
                break;
        }
    }

    /// <summary>Process: rounded box ╭──────╮│ name │╰──────╯</summary>
    private void DrawProcessBox(int x, int y, int w, int h, string name)
    {
        WriteChar(x, y, '╭', ProcessColor);
        DrawHLine(x + 1, x + w - 2, y, '─', ProcessColor);
        WriteChar(x + w - 1, y, '╮', ProcessColor);

        for (int r = y + 1; r < y + h - 1; r++)
        {
            WriteChar(x, r, '│', ProcessColor);
            WriteChar(x + w - 1, r, '│', ProcessColor);
        }

        WriteChar(x, y + h - 1, '╰', ProcessColor);
        DrawHLine(x + 1, x + w - 2, y + h - 1, '─', ProcessColor);
        WriteChar(x + w - 1, y + h - 1, '╯', ProcessColor);

        WriteLabelLines(x, y, w, h, name, LabelColor);
    }

    /// <summary>External Interactor: sharp box ┌──────┐│ name │└──────┘</summary>
    private void DrawExternalBox(int x, int y, int w, int h, string name)
    {
        WriteChar(x, y, '┌', ExternalColor);
        DrawHLine(x + 1, x + w - 2, y, '─', ExternalColor);
        WriteChar(x + w - 1, y, '┐', ExternalColor);

        for (int r = y + 1; r < y + h - 1; r++)
        {
            WriteChar(x, r, '│', ExternalColor);
            WriteChar(x + w - 1, r, '│', ExternalColor);
        }

        WriteChar(x, y + h - 1, '└', ExternalColor);
        DrawHLine(x + 1, x + w - 2, y + h - 1, '─', ExternalColor);
        WriteChar(x + w - 1, y + h - 1, '┘', ExternalColor);

        WriteLabelLines(x, y, w, h, name, ExternalColor);
    }

    /// <summary>Data Store: parallel lines ═══════ name ═══════</summary>
    private void DrawDataStoreBox(int x, int y, int w, int h, string name)
    {
        DrawHLine(x, x + w - 1, y, '═', DataStoreColor);
        DrawHLine(x, x + w - 1, y + h - 1, '═', DataStoreColor);

        WriteLabelLines(x, y, w, h, name, DataStoreColor);
    }

    /// <summary>Trust Boundary: bold box ┏━━━━━━━━━━━━━━━━label━┓</summary>
    private void DrawBoundary(SerializableBorderBoundary boundary)
    {
        var name = GetEntityName(boundary) ?? "Trust Boundary";
        // Use first line only for boundary label on top border
        var firstName = name.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? name;
        int tl = MapX(boundary.Left);
        int tt = MapY(boundary.Top);
        int tr = MapX(boundary.Left + boundary.Width);
        int tb = MapY(boundary.Top + boundary.Height);

        int tw = Math.Max(tr - tl, firstName.Length + 6);
        int th = Math.Max(tb - tt, 5);

        WriteChar(tl, tt, '┏', BoundaryColor);
        DrawHLine(tl + 1, tl + tw - 2, tt, '━', BoundaryColor);
        WriteChar(tl + tw - 1, tt, '┓', BoundaryColor);

        for (int r = tt + 1; r < tt + th - 1; r++)
        {
            WriteChar(tl, r, '┃', BoundaryColor);
            WriteChar(tl + tw - 1, r, '┃', BoundaryColor);
        }

        WriteChar(tl, tt + th - 1, '┗', BoundaryColor);
        DrawHLine(tl + 1, tl + tw - 2, tt + th - 1, '━', BoundaryColor);
        WriteChar(tl + tw - 1, tt + th - 1, '┛', BoundaryColor);

        // Label on top border, right-aligned
        var label = TruncateLabel(firstName, tw - 4);
        WriteText(tl + tw - label.Length - 2, tt, label, BoundaryColor);
    }

    // ── flow drawing ────────────────────────────────────────────────────

    private void DrawFlow(
        EntityRect src,
        EntityRect tgt,
        IReadOnlyList<ModelRoutePoint>? modelRoute)
    {
        if (modelRoute is { Count: >= 2 })
        {
            DrawRoutedFlow(src, tgt, modelRoute);
            return;
        }

        DrawFallbackFlow(src, tgt);
    }

    private void DrawFallbackFlow(EntityRect src, EntityRect tgt)
    {
        // Compute exit/entry points
        var (sx, sy) = GetEdgePoint(src, tgt.CenterX, tgt.CenterY);
        var (tx, ty) = GetEdgePoint(tgt, src.CenterX, src.CenterY);

        int dx = tx - sx;
        int dy = ty - sy;

        // Draw using an elbow route (horizontal-vertical-horizontal or vertical-horizontal-vertical)
        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            // Primarily horizontal movement
            int midX = sx + dx / 2;

            DrawHSegment(sx, midX, sy);
            if (sy != ty)
                DrawVSegment(midX, sy, ty);
            if (midX != tx)
                DrawHSegment(midX, tx, ty);

            // Arrow at target
            _flowArrows.Add((tx, ty, dx > 0 ? '►' : '◄'));
        }
        else
        {
            // Primarily vertical movement
            int midY = sy + dy / 2;

            DrawVSegment(sx, sy, midY);
            if (sx != tx)
                DrawHSegment(sx, tx, midY);
            if (midY != ty)
                DrawVSegment(tx, midY, ty);

            // Arrow at target
            _flowArrows.Add((tx, ty, dy > 0 ? '▼' : '▲'));
        }
    }

    private void DrawRoutedFlow(
        EntityRect src,
        EntityRect tgt,
        IReadOnlyList<ModelRoutePoint> modelRoute)
    {
        var points = modelRoute
            .Select(point => (x: MapX(point.X), y: MapY(point.Y)))
            .ToList();
        points[0] = SnapToTerminalBorder(src, points[0]);
        points[^1] = SnapToTerminalBorder(tgt, points[^1]);
        AddEndpointElbows(points, src, tgt);

        var simplified = new List<(int x, int y)>(points.Count);
        foreach (var point in points)
        {
            if (simplified.Count == 0 || simplified[^1] != point)
                simplified.Add(point);
        }

        if (simplified.Count < 2)
        {
            DrawFallbackFlow(src, tgt);
            return;
        }

        for (var index = 0; index < simplified.Count - 1; index++)
            DrawLineSegment(simplified[index], simplified[index + 1]);

        var beforeTarget = simplified[^2];
        var target = simplified[^1];
        var deltaX = target.x - beforeTarget.x;
        var deltaY = target.y - beforeTarget.y;
        var arrow = Math.Abs(deltaX) >= Math.Abs(deltaY)
            ? (deltaX >= 0 ? '►' : '◄')
            : (deltaY >= 0 ? '▼' : '▲');
        _flowArrows.Add((target.x, target.y, arrow));
    }

    private void DrawFlowArrows()
    {
        foreach (var (x, y, arrow) in _flowArrows)
            WriteChar(x, y, arrow, ArrowColor);
    }

    private static void AddEndpointElbows(
        List<(int x, int y)> points,
        EntityRect source,
        EntityRect target)
    {
        if (points.Count < 2)
            return;

        if (points[0].x != points[1].x && points[0].y != points[1].y)
        {
            var sourceExitsHorizontally =
                points[0].x == source.Left - 1 ||
                points[0].x == source.Right + 1;
            points.Insert(
                1,
                sourceExitsHorizontally
                    ? (points[1].x, points[0].y)
                    : (points[0].x, points[1].y));
        }

        var previousIndex = points.Count - 2;
        if (points[previousIndex].x != points[^1].x &&
            points[previousIndex].y != points[^1].y)
        {
            var targetEntersHorizontally =
                points[^1].x == target.Left - 1 ||
                points[^1].x == target.Right + 1;
            points.Insert(
                points.Count - 1,
                targetEntersHorizontally
                    ? (points[previousIndex].x, points[^1].y)
                    : (points[^1].x, points[previousIndex].y));
        }
    }

    private static (int x, int y) SnapToTerminalBorder(
        EntityRect entity,
        (int x, int y) point)
    {
        var left = entity.Left - 1;
        var right = entity.Right + 1;
        var top = entity.Top - 1;
        var bottom = entity.Bottom + 1;
        var leftDistance = Math.Abs(point.x - left);
        var rightDistance = Math.Abs(point.x - right);
        var topDistance = Math.Abs(point.y - top);
        var bottomDistance = Math.Abs(point.y - bottom);
        var minimum = Math.Min(Math.Min(leftDistance, rightDistance), Math.Min(topDistance, bottomDistance));

        if (minimum == leftDistance)
            return (left, Math.Clamp(point.y, entity.Top, entity.Bottom));
        if (minimum == rightDistance)
            return (right, Math.Clamp(point.y, entity.Top, entity.Bottom));
        if (minimum == topDistance)
            return (Math.Clamp(point.x, entity.Left, entity.Right), top);
        return (Math.Clamp(point.x, entity.Left, entity.Right), bottom);
    }

    private void DrawLineSegment((int x, int y) start, (int x, int y) end)
    {
        var deltaX = Math.Abs(end.x - start.x);
        var stepX = start.x < end.x ? 1 : -1;
        var deltaY = -Math.Abs(end.y - start.y);
        var stepY = start.y < end.y ? 1 : -1;
        var error = deltaX + deltaY;
        var x = start.x;
        var y = start.y;

        var segmentCharacter = start.x == end.x
            ? '│'
            : start.y == end.y
                ? '─'
                : Math.Sign(end.x - start.x) == Math.Sign(end.y - start.y) ? '╲' : '╱';

        while (true)
        {
            if (!IsInsideEntity(x, y))
            {
                var existing = GetCellChar(x, y);
                var character = existing is '─' or '│' or '╱' or '╲' or '┼'
                    ? existing == segmentCharacter ? segmentCharacter : '┼'
                    : segmentCharacter;
                WriteChar(x, y, character, FlowColor);
            }

            if (x == end.x && y == end.y)
                break;

            var twiceError = 2 * error;
            if (twiceError >= deltaY)
            {
                error += deltaY;
                x += stepX;
            }
            if (twiceError <= deltaX)
            {
                error += deltaX;
                y += stepY;
            }
        }
    }

    private void DrawHSegment(int x1, int x2, int y)
    {
        int start = Math.Min(x1, x2);
        int end = Math.Max(x1, x2);
        for (int x = start; x <= end; x++)
        {
            if (IsInsideEntity(x, y)) continue;
            var existing = GetCellChar(x, y);
            char ch = existing switch
            {
                '│' or '┃' => '┼',
                _ => '─'
            };
            WriteChar(x, y, ch, FlowColor);
        }
    }

    private void DrawVSegment(int x, int y1, int y2)
    {
        int start = Math.Min(y1, y2);
        int end = Math.Max(y1, y2);
        for (int y = start; y <= end; y++)
        {
            if (IsInsideEntity(x, y)) continue;
            var existing = GetCellChar(x, y);
            char ch = existing switch
            {
                '─' or '━' or '═' => '┼',
                _ => '│'
            };
            WriteChar(x, y, ch, FlowColor);
        }
    }

    private bool IsInsideEntity(int x, int y)
    {
        foreach (var r in _entityRects)
        {
            if (x > r.left && x < r.right && y > r.top && y < r.bottom)
                return true;
        }
        return false;
    }

    private char GetCellChar(int x, int y)
    {
        if (x >= 0 && x < _width && y >= 0 && y < _height)
        {
            if (_surface.TryGetCell(x, y, out var cell))
            {
                var ch = cell.Character;
                if (ch == UnwrittenMarker || ch.Length == 0) return ' ';
                return ch[0];
            }
        }
        return ' ';
    }

    private static (int x, int y) GetEdgePoint(EntityRect entity, int targetX, int targetY)
    {
        int dx = targetX - entity.CenterX;
        int dy = targetY - entity.CenterY;

        // Use aspect-ratio-aware comparison (terminal cells are ~2:1 width:height in pixels)
        if (Math.Abs(dx) * entity.Height > Math.Abs(dy) * entity.Width)
        {
            // Exit through left or right
            int edgeY = entity.CenterY;
            if (dx > 0)
                return (entity.Right + 1, edgeY);
            else
                return (entity.Left - 1, edgeY);
        }
        else
        {
            // Exit through top or bottom
            int edgeX = entity.CenterX;
            if (dy > 0)
                return (edgeX, entity.Bottom + 1);
            else
                return (edgeX, entity.Top - 1);
        }
    }

    // ── legend ──────────────────────────────────────────────────────────

    private void DrawLegend(int y)
    {
        int x = 1;
        _surface.WriteText(x, y, "╭─╮", ProcessColor); x += 4;
        _surface.WriteText(x, y, "Process  ", DimColor); x += 9;
        _surface.WriteText(x, y, "┌─┐", ExternalColor); x += 4;
        _surface.WriteText(x, y, "External  ", DimColor); x += 10;
        _surface.WriteText(x, y, "═══", DataStoreColor); x += 4;
        _surface.WriteText(x, y, "Data Store  ", DimColor); x += 12;
        _surface.WriteText(x, y, "┏━┓", BoundaryColor); x += 4;
        _surface.WriteText(x, y, "Trust Boundary  ", DimColor); x += 16;
        _surface.WriteText(x, y, "──►", ArrowColor); x += 4;
        _surface.WriteText(x, y, "Data Flow", DimColor);
    }

    // ── output ──────────────────────────────────────────────────────────

    // Hex1b uses \uE000 (Private Use Area) as an unwritten cell marker
    private const string UnwrittenMarker = "\uE000";

    private static bool IsEmptyCell(SurfaceCell cell)
        => cell.Character == UnwrittenMarker || cell.Character == " " || cell.Character.Length == 0;

    private static string CellToChar(SurfaceCell cell)
        => cell.Character == UnwrittenMarker || cell.Character.Length == 0 ? " " : cell.Character;

    /// <summary>
    /// Convert Hex1b Surface to ANSI string for terminal output.
    /// </summary>
    private string ToAnsiString()
    {
        var sb = new System.Text.StringBuilder(_width * _height * 3);

        for (int row = 0; row < _height; row++)
        {
            Hex1bColor? prevFg = null;
            Hex1bColor? prevBg = null;
            int lastContentCol = -1;

            // Find last non-empty column to trim trailing unwritten/space cells
            var rowSpan = _surface.GetRow(row);
            for (int c = rowSpan.Length - 1; c >= 0; c--)
            {
                if (!IsEmptyCell(rowSpan[c]))
                {
                    lastContentCol = c;
                    break;
                }
            }

            for (int c = 0; c <= lastContentCol; c++)
            {
                var cell = rowSpan[c];
                var fg = cell.Foreground;
                var bg = cell.Background;

                // Skip color codes for unwritten cells — just emit a space
                if (cell.Character == UnwrittenMarker && fg is null && bg is null)
                {
                    sb.Append(' ');
                    continue;
                }

                if (!Equals(fg, prevFg) || !Equals(bg, prevBg))
                {
                    if (fg is not null)
                    {
                        sb.Append(fg.Value.ToForegroundAnsi());
                    }
                    else if (prevFg is not null)
                    {
                        sb.Append("\x1b[39m"); // reset fg
                    }

                    if (bg is not null)
                    {
                        sb.Append(bg.Value.ToBackgroundAnsi());
                    }
                    else if (prevBg is not null)
                    {
                        sb.Append("\x1b[49m"); // reset bg
                    }

                    prevFg = fg;
                    prevBg = bg;
                }

                sb.Append(CellToChar(cell));
            }

            if (prevFg is not null || prevBg is not null)
                sb.Append("\x1b[0m");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Convert the surface to plain text without ANSI codes.
    /// </summary>
    private string ToPlainString()
    {
        var sb = new System.Text.StringBuilder(_width * _height);
        for (int row = 0; row < _height; row++)
        {
            var rowSpan = _surface.GetRow(row);
            int lastContent = -1;
            for (int c = rowSpan.Length - 1; c >= 0; c--)
            {
                if (!IsEmptyCell(rowSpan[c]))
                { lastContent = c; break; }
            }
            for (int c = 0; c <= lastContent; c++)
                sb.Append(CellToChar(rowSpan[c]));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Split a potentially multi-line name into lines that fit within maxWidth,
    /// then render them vertically centered within the given box area.
    /// </summary>
    private void WriteLabelLines(int boxX, int boxY, int boxW, int boxH, string name, Hex1bColor color)
    {
        int maxWidth = boxW - 2;
        if (maxWidth <= 0) return;

        // Split on actual newlines in the name
        var rawLines = name.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();

        foreach (var raw in rawLines)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;

            // Word-wrap long lines
            if (trimmed.Length <= maxWidth)
            {
                lines.Add(trimmed);
            }
            else
            {
                // Break at spaces
                int pos = 0;
                while (pos < trimmed.Length)
                {
                    int remaining = trimmed.Length - pos;
                    if (remaining <= maxWidth)
                    {
                        lines.Add(trimmed[pos..]);
                        break;
                    }

                    int breakAt = trimmed.LastIndexOf(' ', pos + maxWidth - 1, Math.Min(maxWidth, remaining));
                    if (breakAt <= pos)
                        breakAt = pos + maxWidth - 1; // force break

                    lines.Add(TruncateLabel(trimmed[pos..breakAt], maxWidth));
                    pos = breakAt + 1;
                }
            }
        }

        // How many lines can we fit inside the box (between borders)?
        int availRows = boxH - 2;
        int showLines = Math.Min(lines.Count, Math.Max(1, availRows));
        int startRow = boxY + 1 + Math.Max(0, (availRows - showLines) / 2);

        for (int i = 0; i < showLines; i++)
        {
            var line = TruncateLabel(lines[i], maxWidth);
            int lx = boxX + 1 + (maxWidth - line.Length) / 2;
            WriteText(lx, startRow + i, line, color);
        }
    }

    private static string TruncateLabel(string name, int maxLen)
    {
        if (maxLen <= 0) return "";
        if (name.Length <= maxLen) return name;
        return maxLen > 3 ? name[..(maxLen - 1)] + "…" : name[..maxLen];
    }

    private static string? GetEntityName(SerializableTaggable entity)
    {
        if (entity.Properties is null) return null;
        foreach (var prop in entity.Properties)
        {
            if (prop is SerializableStringDisplayAttribute sda && sda.DisplayName == "Name")
                return sda.Value?.ToString();
        }
        return null;
    }
}
