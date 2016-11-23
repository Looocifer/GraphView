﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GraphView.GraphViewExecutionRuntime
{
    internal abstract class Decoder
    {
        internal class PathRecord
        {
            public RawRecord PathRec { get; set; }
            public string SinkReferences { get; set; }
        }

        internal abstract List<Tuple<string, Dictionary<string, string>, List<string>>> DecodeJObjects(List<dynamic> items,
            List<string> header, int nodeIdx, int metaHeaderLength);

        /// <summary>
        /// Copy the oldRecord to the newRecord
        /// </summary>
        internal static void ExtendAndCopyRecord(RawRecord oldRecord, ref RawRecord newRecord, int nodeIdx, int metaHeaderLength)
        {
            // if metaHeaderLength == -1, then the newRecord's length is the same as the old one
            if (metaHeaderLength == -1)
                newRecord = new RawRecord(oldRecord);
            // else, the old record needs to be extended first before copy
            // when nodeIdx == 0, this can be skipped because the oldRecord is empty
            else if (nodeIdx != 0)
            {
                // old record's meta info
                for (var i = 0; i < nodeIdx; i++)
                    newRecord.fieldValues[i] = oldRecord.fieldValues[i];
                // old record's select elements
                for (var i = nodeIdx; i < oldRecord.fieldValues.Count; i++)
                    newRecord.fieldValues[i + metaHeaderLength] = oldRecord.fieldValues[i];
            }
        }

        /// <summary>
        /// Cross apply the adj and retrieve all the sink ids
        /// </summary>
        /// <param name="record"></param>
        /// <param name="sinkSet">The set of sink ids</param>
        /// <param name="results">The new table generated by cross applying the decoder</param>
        /// <param name="header"></param>
        /// <param name="adjIdx">The index of the adj to be decoded</param>
        /// <param name="dest">The index of the node's id to be joined with the edge's sink</param>
        /// <param name="metaHeaderLength">The metaheader length of the dest node</param>
        internal virtual HashSet<string> CrossApplyEdge(RawRecord record, ref HashSet<string> sinkSet, ref List<RawRecord> results,
            List<string> header, int adjIdx, int dest, int metaHeaderLength = -1)
        {
            var reservedWords = new HashSet<string> { "_sink", "_ID" };

            // Decode the adj list JSON
            var adj = JArray.Parse(record.fieldValues[adjIdx]);
            foreach (var edge in adj.Children<JObject>())
            {
                var sink = edge["_sink"].ToString();
                // When dest != -1 && metaHeaderLength == -1, the join predicate edge.sink = dest.id must be applied
                if (dest != -1 && metaHeaderLength == -1 && !sink.Equals(record.fieldValues[dest])) continue;
                sinkSet.Add(sink);

                // Construct new record
                var result = new RawRecord(header.Count);
                ExtendAndCopyRecord(record, ref result, dest, metaHeaderLength);
                // Fill field of the edge's SINK
                result.fieldValues[adjIdx + 1] = sink;
                // Fill the field of selected edge's properties
                foreach (var pair in edge)
                {
                    if (!reservedWords.Contains(pair.Key) && header.Contains(pair.Key))
                        result.fieldValues[header.IndexOf(pair.Key)] = pair.Value.ToString();
                }
                results.Add(result);
            }

            return sinkSet;
        }

        /// <summary>
        /// Cross apply the path and retrieve all the sink ids
        /// </summary>
        /// <param name="record"></param>
        /// <param name="pathStepOperator"></param>
        /// <param name="cSrcOperator">The constant source operator</param>
        /// <param name="sinkSet">The set of sink ids</param>
        /// <param name="results">The new table generated by cross applying the decoder</param>
        /// <param name="header"></param>
        /// <param name="adjIdx">The index of the adj to be decoded</param>
        /// <param name="dest">The index of the node's id to be joined with the edge's sink</param>
        /// <param name="metaHeaderLength">The metaheader length of the dest node</param>
        internal virtual HashSet<string> CrossApplyPath(RawRecord record, GraphViewExecutionOperator pathStepOperator, ConstantSourceOperator cSrcOperator, ref HashSet<string> sinkSet, ref List<RawRecord> results, List<string> header, int src, int adjIdx, int dest, int metaHeaderLength)
        {
            var inputRecord = new RawRecord(0);

            // Extracts the metainfo of the starting node from the input record
            inputRecord.fieldValues.Add("");
            inputRecord.fieldValues.Add("");
            inputRecord.fieldValues.Add("");
            inputRecord.fieldValues.Add(record.fieldValues[src]);
            inputRecord.fieldValues.Add(record.fieldValues[adjIdx]);
            inputRecord.fieldValues.Add(record.fieldValues[record.fieldValues.Count - 1]);

            // put it into path function
            var PathResult = PathFunction(inputRecord, pathStepOperator, cSrcOperator);
            foreach (var x in PathResult)
            {
                var adj = JArray.Parse(x.SinkReferences);
                foreach (var edge in adj.Children<JObject>())
                {
                    var sink = edge["_sink"].ToString();

                    sinkSet.Add(sink);
                    var result = new RawRecord(header.Count);
                    ExtendAndCopyRecord(record, ref result, dest, metaHeaderLength);
                    // update the adjList and adjList's sink field
                    result.fieldValues[adjIdx] = x.SinkReferences;
                    result.fieldValues[adjIdx + 1] = sink;
                    // update the path field
                    result.fieldValues[result.fieldValues.Count - 1] =
                        x.PathRec.fieldValues[x.PathRec.fieldValues.Count - 1];
                    results.Add(result);
                }
            }

            return sinkSet;
        }

        /// <summary>
        /// Starting from sourceRecord, run the pathStepOperator to traverse the path
        /// </summary>
        /// <param name="sourceRecord"></param>
        /// <param name="pathStepOperator"></param>
        /// <param name="source">The constant source operator of the pathStepOperator</param>
        protected static Queue<PathRecord> PathFunction(RawRecord sourceRecord, GraphViewExecutionOperator pathStepOperator, ConstantSourceOperator source)
        {
            // A list of paths discovered
            Queue<PathRecord> allPaths = new Queue<PathRecord>();
            // A list of paths discovered in last iteration
            Queue<PathRecord> mostRecentlyDiscoveredPaths = new Queue<PathRecord>();

            mostRecentlyDiscoveredPaths.Enqueue(new PathRecord()
            {
                PathRec = sourceRecord,
                SinkReferences = sourceRecord.fieldValues[sourceRecord.fieldValues.Count - 2]
            });

            allPaths.Enqueue(new PathRecord()
            {
                PathRec = sourceRecord,
                SinkReferences = sourceRecord.fieldValues[sourceRecord.fieldValues.Count - 2]
            });

            pathStepOperator.ResetState();

            while (mostRecentlyDiscoveredPaths.Count > 0)
            {
                PathRecord start = mostRecentlyDiscoveredPaths.Dequeue();
                int lastVertexIndex = start.PathRec.fieldValues.Count - 3;

                // Constant source's format is:
                // | starting node id | starting node's adjList | sink | next node id's adjList | path | 
                var srecord = new RawRecord(0);

                // Put the start node in the Kth queue back to the constant source
                // Here we take info from an intermediate record to generate a new constant source record
                srecord.fieldValues.Add(start.PathRec.fieldValues[lastVertexIndex]);
                srecord.fieldValues.Add(start.PathRec.fieldValues[lastVertexIndex + 1]);
                srecord.fieldValues.Add("");
                srecord.fieldValues.Add("");
                srecord.fieldValues.Add(start.PathRec.fieldValues[lastVertexIndex + 2]);
                source.ConstantSource = srecord;
                // reset state of internal operator
                pathStepOperator.ResetState();
                // Put all the results back into (K+1)th queue.
                while (pathStepOperator.State())
                {
                    // Intermediate record's format is:
                    // | starting node id | starting node's adjList | sink | next node id | next node id's adjList | path | 
                    var EndRecord = pathStepOperator.Next();
                    if (EndRecord != null)
                    {
                        lastVertexIndex = EndRecord.fieldValues.Count - 3;
                        var sink = EndRecord.fieldValues[lastVertexIndex + 1];
                        // Only path with valid references will be added
                        if (!sink.Equals("[]"))
                        {
                            PathRecord newPath = new PathRecord()
                            {
                                PathRec = EndRecord,
                                SinkReferences = sink
                            };
                            mostRecentlyDiscoveredPaths.Enqueue(newPath);
                            allPaths.Enqueue(newPath);
                        }
                    }
                }
            }
            return allPaths;
        }
    }

    internal class DocDbDecoder : Decoder
    {
        // Decode JObject into (id, adjacent lists, selected elements)
        internal override List<Tuple<string, Dictionary<string, string>, List<string>>> DecodeJObjects(List<dynamic> items, List<string> header, int nodeIdx, int metaHeaderLength)
        {
            // <node id, Dict<edgeAlias, set of sink reference>>
            var idtoAdjsDict = new Dictionary<string, Dictionary<string, HashSet<JToken>>>();
            // <node id, selected elements>
            var idtoSelectElementsDict = new Dictionary<string, RawRecord>();
            var startOfResultField = nodeIdx + metaHeaderLength;

            foreach (var dynamicItem in items)
            {
                var item = (JObject)dynamicItem;
                JToken nodeInfo = item["_nodeid"];
                var id = nodeInfo["id"].ToString();
                Dictionary<string, HashSet<JToken>> adjLists;
                RawRecord selectElements;

                if (!idtoAdjsDict.TryGetValue(id, out adjLists))
                {
                    adjLists = new Dictionary<string, HashSet<JToken>>();
                    idtoAdjsDict.Add(id, adjLists);
                }

                // meta header length > 1 means this node has attached edges to be processed
                if (metaHeaderLength > 1)
                {
                    var metaHeader = header.GetRange(nodeIdx + 1, metaHeaderLength - 1);
                    // i += 2 to skip the _SINK field of each edge
                    for (var i = 0; i < metaHeader.Count; i += 2)
                    {
                        var adjName = metaHeader[i];
                        HashSet<JToken> adjList;
                        if (!adjLists.TryGetValue(adjName, out adjList))
                        {
                            adjList = new HashSet<JToken>(new JTokenComparer());
                            adjLists.Add(adjName, adjList);
                        }
                        adjList.Add(item[adjName]);
                    }
                }

                if (!idtoSelectElementsDict.TryGetValue(id, out selectElements))
                {
                    selectElements = new RawRecord(header.Count);
                    idtoSelectElementsDict.Add(id, selectElements);

                    foreach (var fieldName in header.GetRange(startOfResultField, header.Count - startOfResultField))
                    {
                        // Alias with dot and whitespace is illegal in documentDB, so they will be replaced by "_"
                        string alias = fieldName.Replace(".", "_").Replace(" ", "_");
                        if (item[alias] != null)
                            selectElements.fieldValues[header.IndexOf(fieldName)] = item[alias].ToString();
                    }
                }
            }

            // <id, Dict<edgeAlias, adjList>, List<select elements>>
            var result = new List<Tuple<string, Dictionary<string, string>, List<string>>>();
            foreach (var it in idtoSelectElementsDict)
            {
                var id = it.Key;
                var adjLists = idtoAdjsDict[id];
                var adjListsStr = new Dictionary<string, string>();
                // Put all the sink references into an JSON array 
                foreach (var pair in adjLists)
                {
                    var adjName = pair.Key;
                    var adjJTokens = pair.Value;
                    var adjStr = new StringBuilder("");
                    if (adjJTokens.Any())
                    {
                        adjStr.Append("[");
                        foreach (var jToken in adjJTokens)
                        {
                            adjStr.Append(jToken.ToString());
                            adjStr.Append(",");
                        }
                        adjStr.Remove(adjStr.Length - 1, 1);
                        adjStr.Append("]");
                    }
                    adjListsStr.Add(adjName, adjStr.ToString());
                }
                result.Add(new Tuple<string, Dictionary<string, string>, List<string>>(id, adjListsStr, it.Value.fieldValues));
            }
            return result;
        }
    }
}
