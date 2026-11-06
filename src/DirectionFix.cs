using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Conveyors;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.ResourceDistribution.Conveyors;
using Keen.VRage.DCS.Definitions;

namespace SorterUnlock;

/// <summary>
/// Vanilla gap #2: ConveyorGraphPostProcessor builds the auto-generated subgraph node with
/// FilterOutputEdge = null, so the filter has no "output side" and Drain All / directed flow never
/// find a push target. We give the sorter node an output edge (one of its two conveyor ports).
/// </summary>
internal static class DirectionFix
{
    private static FieldInfo? _dict;

    public static void Apply(ConveyorGraphPostProcessor pp, EntityCompositeDefinition composition)
    {
        string? name = composition.DebugName;
        if (name == null || !name.Contains("ConveyorSorter", StringComparison.OrdinalIgnoreCase) || name.Contains("Client", StringComparison.OrdinalIgnoreCase))
            return;

        _dict ??= typeof(ConveyorGraphPostProcessor).GetField("_conveyorSubgraphs", BindingFlags.Instance | BindingFlags.NonPublic);
        if (_dict?.GetValue(pp) is not Dictionary<EntityCompositeDefinition, ConveyorSubgraph> map || !map.TryGetValue(composition, out var sg))
        {
            Log.Write($"DIRFIX {name}: no generated subgraph (definition has its own?), skipped");
            return;
        }

        var nodes = new List<ConveyorSubgraphNode>(); foreach (var n in sg.Nodes) nodes.Add(n);
        int idx = nodes.FindIndex(n => n.FilterTag.HasValue);
        if (idx < 0) { Log.Write($"DIRFIX {name}: no filter node, skipped"); return; }
        var node = nodes[idx];
        var edges = new List<ConveyorSubgraphNode.Edge>(); foreach (var e in node.ConnectedEdges) edges.Add(e);
        var invTags = new List<Keen.VRage.Library.Utils.StringId>(); foreach (var t in node.InventoryTags) invTags.Add(t);
        string desc = string.Join(", ", edges.Select(e => $"#{e.Id}:{(e as ConveyorSubgraphNode.ExternalEdge)?.DummyName}"));
        if (node.FilterOutputEdge.HasValue) { Log.Write($"DIRFIX {name}: output edge already set ({node.FilterOutputEdge}), edges [{desc}]"); return; }
        if (edges.Count < 2) { Log.Write($"DIRFIX {name}: only {edges.Count} edge(s) [{desc}], skipped"); return; }

        // Prefer a port whose dummy name looks like an output/front; otherwise the second port.
        var pick = edges.FirstOrDefault(e => (e as ConveyorSubgraphNode.ExternalEdge)?.DummyName is string d &&
                       (d.Contains("out", StringComparison.OrdinalIgnoreCase) || d.Contains("front", StringComparison.OrdinalIgnoreCase)))
                   ?? edges[edges.Count - 1];

        nodes[idx] = new ConveyorSubgraphNode(edges.ToImmutableArray(), invTags.ToImmutableArray(), node.FilterTag, pick.Id);
        map[composition] = new ConveyorSubgraph(nodes.ToImmutableArray()).GenerateWithEdgeNodePairing();
        Log.Write($"DIRFIX {name}: filter output edge set to #{pick.Id} ({(pick as ConveyorSubgraphNode.ExternalEdge)?.DummyName}); edges [{desc}]");
    }
}
