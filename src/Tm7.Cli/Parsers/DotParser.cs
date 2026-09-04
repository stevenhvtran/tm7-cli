using System.Text.RegularExpressions;

namespace Tm7.Cli.Parsers;

public record DotEntity(string Id, string Label, string? BoundaryId)
{
    public bool IsInsideBoundary => BoundaryId is not null;
}

public record DotEdge(string SourceId, string TargetId, string Label, bool Bidirectional);
public record DotBoundary(
    string Id,
    string Label,
    List<string> ContainedEntityIds,
    string? ParentBoundaryId);
public record DotGraph(
    string? Label,
    string? RankDirection,
    List<DotEntity> Entities,
    List<DotEdge> Edges,
    List<DotBoundary> Boundaries);

public static class DotParser
{
    public static DotGraph Parse(string filePath)
    {
        var lines = File.ReadAllLines(filePath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//") && !l.StartsWith("@"))
            .ToList();

        var entities = new Dictionary<string, string>(); // id -> label
        var edges = new List<DotEdge>();
        var boundaries = new List<DotBoundary>();
        var entityBoundaryIds = new Dictionary<string, BoundaryMembership>(StringComparer.Ordinal);
        string? graphLabel = null;
        string? rankDirection = null;

        // Join multi-line label statement (the graph label spans multiple lines)
        var joined = JoinMultiLineStatements(lines);

        // Track which boundary context we're in
        var boundaryStack = new Stack<BoundaryContext>();

        foreach (var line in joined)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//") || trimmed.StartsWith("@")) continue;

            var rankDirectionMatch = Regex.Match(
                trimmed,
                @"\brankdir\s*=\s*""?(LR|RL|TB|BT)""?",
                RegexOptions.IgnoreCase);
            if (rankDirectionMatch.Success && boundaryStack.Count == 0)
                rankDirection = rankDirectionMatch.Groups[1].Value.ToUpperInvariant();

            // Skip top-level graph declaration
            if (trimmed.StartsWith("digraph ")) continue;

            // Skip global attributes
            if (trimmed.StartsWith("graph [") || trimmed.StartsWith("node [") || trimmed.StartsWith("edge [") ||
                trimmed.StartsWith("fontname=") || trimmed.StartsWith("fontsize=") ||
                trimmed.StartsWith("rankdir=")) continue;
            if (Regex.IsMatch(trimmed, @"^(graph|node|edge)\s+\[")) continue;
            // Skip lines that are just global graph attributes
            if (Regex.IsMatch(trimmed, @"^(fontname|fontsize|rankdir)\s*=")) continue;
            // Combined attribute lines like "fontname=Helvetica fontsize=12 rankdir=LR"
            if (Regex.IsMatch(trimmed, @"^fontname=\S+\s+fontsize=")) continue;

            // Closing brace
            if (trimmed == "}" || trimmed == "};")
            {
                if (boundaryStack.Count > 0)
                {
                    var b = boundaryStack.Pop();
                    if (b.IsNamedCluster)
                    {
                        boundaries.Add(new DotBoundary(
                            b.Id,
                            b.Label.Length == 0 ? b.Id : b.Label,
                            b.EntityIds,
                            b.ParentBoundaryId));
                    }

                    if (boundaryStack.Count > 0)
                    {
                        foreach (var eid in b.EntityIds)
                            boundaryStack.Peek().AddEntity(eid);
                    }

                }
                continue;
            }

            // Subgraph cluster
            var clusterMatch = Regex.Match(trimmed, @"^subgraph\s+cluster_(\w+)\s*\{");
            if (clusterMatch.Success)
            {
                var clusterId = clusterMatch.Groups[1].Value;
                boundaryStack.Push(new BoundaryContext(
                    clusterId,
                    isNamedCluster: true,
                    FindNearestNamedBoundaryId(boundaryStack)));
                // Parse inline attributes for label
                var rest = trimmed[(clusterMatch.Index + clusterMatch.Length)..];
                // Attributes may follow on same or next lines
                continue;
            }

            // Anonymous subgraph
            if (Regex.IsMatch(trimmed, @"^subgraph\s*\{"))
            {
                boundaryStack.Push(new BoundaryContext(
                    "anon_" + boundaries.Count,
                    isNamedCluster: false,
                    FindNearestNamedBoundaryId(boundaryStack)));
                continue;
            }

            // Cluster attributes (inside subgraph block)
            if (boundaryStack.Count > 0 && IsAttributeLine(trimmed))
            {
                // Check if line contains a label attribute (standalone attribute line, not a node/edge)
                var labelInLine = Regex.Match(trimmed, @"label\s*=\s*""([^""]+)""");
                if (labelInLine.Success)
                    boundaryStack.Peek().Label = labelInLine.Groups[1].Value;
                continue;
            }

            // Graph-level label (multi-line)
            var graphLabelMatch = Regex.Match(trimmed, @"^label\s*=\s*""(.+?)""", RegexOptions.Singleline);
            if (graphLabelMatch.Success && boundaryStack.Count == 0)
            {
                graphLabel = graphLabelMatch.Groups[1].Value
                    .Replace("\\l", "\n").Replace("\\n", "\n").Trim();
                continue;
            }
            // Unquoted graph label that might start with label="
            if (trimmed.StartsWith("label=") && boundaryStack.Count == 0)
            {
                graphLabel = ExtractQuotedString(trimmed, "label=");
                continue;
            }

            // Multi-source edge: { a b } -> target [attrs]
            var multiSrcMatch = Regex.Match(trimmed, @"^\{\s*(.+?)\s*\}\s*->\s*(\w+)\s*(\[.*?\])?\s*;?\s*$");
            if (multiSrcMatch.Success)
            {
                var sources = multiSrcMatch.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var target = multiSrcMatch.Groups[2].Value;
                var attrs = multiSrcMatch.Groups[3].Success ? multiSrcMatch.Groups[3].Value : "";
                var edgeLabel = ExtractAttr(attrs, "label") ?? "";
                var bidir = ExtractAttr(attrs, "dir") == "both";
                foreach (var src in sources)
                {
                    EnsureEntity(entities, src);
                    EnsureEntity(entities, target);
                    edges.Add(new DotEdge(src, target, edgeLabel, bidir));
                    if (boundaryStack.Count > 0)
                    {
                        AddToBoundary(boundaryStack, entityBoundaryIds, src);
                        AddToBoundary(boundaryStack, entityBoundaryIds, target);
                    }
                }
                continue;
            }

            // Multi-target edge: source -> { a b } [attrs]
            var multiTgtMatch = Regex.Match(trimmed, @"^(\w+)\s*->\s*\{\s*(.+?)\s*\}\s*(\[.*?\])?\s*;?\s*$");
            if (multiTgtMatch.Success)
            {
                var source = multiTgtMatch.Groups[1].Value;
                var targets = multiTgtMatch.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var attrs = multiTgtMatch.Groups[3].Success ? multiTgtMatch.Groups[3].Value : "";
                var edgeLabel = ExtractAttr(attrs, "label") ?? "";
                var bidir = ExtractAttr(attrs, "dir") == "both";
                foreach (var tgt in targets)
                {
                    EnsureEntity(entities, source);
                    EnsureEntity(entities, tgt);
                    edges.Add(new DotEdge(source, tgt, edgeLabel, bidir));
                    if (boundaryStack.Count > 0)
                    {
                        AddToBoundary(boundaryStack, entityBoundaryIds, source);
                        AddToBoundary(boundaryStack, entityBoundaryIds, tgt);
                    }
                }
                continue;
            }

            // Simple edge: a -> b [attrs]
            var edgeMatch = Regex.Match(trimmed, @"^(\w+)\s*->\s*(\w+)\s*(\[.*?\])?\s*;?\s*$");
            if (edgeMatch.Success)
            {
                var src = edgeMatch.Groups[1].Value;
                var tgt = edgeMatch.Groups[2].Value;
                var attrs = edgeMatch.Groups[3].Success ? edgeMatch.Groups[3].Value : "";
                var edgeLabel = ExtractAttr(attrs, "label") ?? "";
                var bidir = ExtractAttr(attrs, "dir") == "both";
                EnsureEntity(entities, src);
                EnsureEntity(entities, tgt);
                edges.Add(new DotEdge(src, tgt, edgeLabel, bidir));
                if (boundaryStack.Count > 0)
                {
                    AddToBoundary(boundaryStack, entityBoundaryIds, src);
                    AddToBoundary(boundaryStack, entityBoundaryIds, tgt);
                }
                continue;
            }

            // Node declaration: id [label="...", ...]
            var nodeMatch = Regex.Match(trimmed, @"^(\w+)\s*\[(.+?)\]\s*;?\s*$");
            if (nodeMatch.Success)
            {
                var nodeId = nodeMatch.Groups[1].Value;
                var attrs = nodeMatch.Groups[2].Value;
                var label = ExtractAttr("[" + attrs + "]", "label") ?? nodeId;
                entities[nodeId] = label;
                if (boundaryStack.Count > 0)
                    AddToBoundary(boundaryStack, entityBoundaryIds, nodeId);
                continue;
            }

            // Bare node declaration, commonly used inside a cluster after the
            // node's attributes were declared at graph scope.
            var bareNodeMatch = Regex.Match(trimmed, @"^(\w+)\s*;?\s*$");
            if (bareNodeMatch.Success)
            {
                var nodeId = bareNodeMatch.Groups[1].Value;
                EnsureEntity(entities, nodeId);
                if (boundaryStack.Count > 0)
                    AddToBoundary(boundaryStack, entityBoundaryIds, nodeId);
            }
        }

        // Build final entity list
        var entityList = entities.Select(kvp =>
            new DotEntity(
                kvp.Key,
                kvp.Value,
                entityBoundaryIds.GetValueOrDefault(kvp.Key)?.BoundaryId))
            .ToList();

        return new DotGraph(graphLabel, rankDirection, entityList, edges, boundaries);
    }

    static List<string> JoinMultiLineStatements(List<string> lines)
    {
        var result = new List<string>();
        string? pending = null;
        int quoteCount = 0;

        foreach (var line in lines)
        {
            if (pending != null)
            {
                pending += "\n" + line;
                quoteCount += CountUnescapedQuotes(line);
                if (quoteCount % 2 == 0)
                {
                    result.Add(pending);
                    pending = null;
                    quoteCount = 0;
                }
            }
            else
            {
                quoteCount = CountUnescapedQuotes(line);
                if (quoteCount % 2 != 0)
                {
                    pending = line;
                }
                else
                {
                    result.Add(line);
                }
            }
        }
        if (pending != null)
            result.Add(pending);

        return result;
    }

    static int CountUnescapedQuotes(string s)
    {
        int count = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '"' && (i == 0 || s[i - 1] != '\\'))
                count++;
        }
        return count;
    }

    static void EnsureEntity(Dictionary<string, string> entities, string id)
    {
        if (!entities.ContainsKey(id))
            entities[id] = id; // Use id as default label
    }

    static void AddToBoundary(
        Stack<BoundaryContext> stack,
        Dictionary<string, BoundaryMembership> entityBoundaryIds,
        string entityId)
    {
        stack.Peek().AddEntity(entityId);
        var namedBoundaries = stack
            .Where(context => context.IsNamedCluster)
            .ToList();
        if (namedBoundaries.Count == 0)
            return;

        var membership = new BoundaryMembership(
            namedBoundaries[0].Id,
            namedBoundaries.Count);
        if (!entityBoundaryIds.TryGetValue(entityId, out var existing) ||
            membership.Depth >= existing.Depth)
        {
            entityBoundaryIds[entityId] = membership;
        }
    }

    static string? FindNearestNamedBoundaryId(IEnumerable<BoundaryContext> stack)
        => stack.FirstOrDefault(context => context.IsNamedCluster)?.Id;

    sealed class BoundaryContext(
        string id,
        bool isNamedCluster,
        string? parentBoundaryId)
    {
        public string Id { get; } = id;
        public bool IsNamedCluster { get; } = isNamedCluster;
        public string? ParentBoundaryId { get; } = parentBoundaryId;
        public string Label { get; set; } = "";
        public List<string> EntityIds { get; } = [];

        public void AddEntity(string entityId)
        {
            if (!EntityIds.Contains(entityId))
                EntityIds.Add(entityId);
        }
    }

    sealed record BoundaryMembership(string BoundaryId, int Depth);

    static string? ExtractAttr(string attrs, string name)
    {
        // Match: name="value" or name=value
        var match = Regex.Match(attrs, name + @"\s*=\s*""([^""]*?)""");
        if (match.Success) return match.Groups[1].Value;
        match = Regex.Match(attrs, name + @"\s*=\s*(\w+)");
        if (match.Success) return match.Groups[1].Value;
        return null;
    }

    static string? ExtractQuotedString(string line, string prefix)
    {
        var idx = line.IndexOf(prefix);
        if (idx < 0) return null;
        var rest = line[(idx + prefix.Length)..].Trim();
        if (rest.StartsWith("\""))
        {
            // Find closing quote
            var end = rest.LastIndexOf('"');
            if (end > 0)
                return rest[1..end].Replace("\\l", "\n").Replace("\\n", "\n").Trim();
        }
        return rest;
    }

    static bool IsAttributeLine(string trimmed)
    {
        // A line consisting only of key=value attribute pairs (no -> edges, no standalone identifiers with [])
        if (trimmed.Contains("->")) return false;
        // Check if line matches pattern: word=value [word=value ...]
        var stripped = Regex.Replace(trimmed, @"""[^""]*""", ""); // remove quoted strings
        // If what's left is only attribute assignments and whitespace
        return Regex.IsMatch(stripped, @"^(\w+=\S*\s*)+$");
    }
}
