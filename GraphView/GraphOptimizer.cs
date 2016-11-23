﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace GraphView
{
    public abstract class GraphOptimizer
    {
        protected MatchGraph Graph;

        // Upper Bound of the State number
        protected const int MaxStates = 1000;

        protected GraphOptimizer(MatchGraph graph)
        {
            Graph = graph;
        }

        public abstract List<Tuple<MatchNode, MatchEdge>> GetOptimizedTraversalOrder(ConnectedComponent subGraph, out Dictionary<string, List<MatchEdge>> nodeToMaterializedEdgesDict);
    }
}
