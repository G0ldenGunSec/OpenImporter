using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenImporter
{
    public class InputParser
    {
        private static ConcurrentDictionary<string, string> AttributeCache = new ConcurrentDictionary<string, string>();
        public GraphData GraphData { get; private set; }

        public InputParser(string jsonPath)
        {
            if (!File.Exists(jsonPath))
                throw new FileNotFoundException("Input file not found", jsonPath);

            var json = File.ReadAllText(jsonPath);
            GraphData = JsonConvert.DeserializeObject<GraphData>(json);
        }

        public async Task ParseJson(MatchFailBehavior onFail)
        {
            Console.WriteLine($"[*] Loaded {GraphData.Graph.Nodes.Count} nodes and {GraphData.Graph.Edges.Count} edges");

            var processedEdges = new ConcurrentBag<Edge>();
            var existingNodeIds = new ConcurrentDictionary<string, bool>(GraphData.Graph.Nodes.Select(n => new KeyValuePair<string, bool>(n.Id, true)));

            //limiting to two concurrent requests to avoid 429 errors, idk probably update this if things are too slow, or drop if you're getting 429's (works in my lab :D)
            var semaphore = new SemaphoreSlim(2);
            var tasks = new List<Task>();

            foreach (var edge in GraphData.Graph.Edges)
            {
                await semaphore.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        bool skipEdge = false;

                        if (edge.toValidate != null && edge.toValidate.Count > 0)
                        {
                            foreach (var kvp in edge.toValidate)
                            {
                                string edgeNode = kvp.Key;
                                List<ValidateAttrib> validations = kvp.Value;

                                string matchedId = await MapAttributesAsync(validations);

                                if (!string.IsNullOrEmpty(matchedId))
                                {                                    
                                    if (edgeNode.Equals("start", StringComparison.OrdinalIgnoreCase) && edge.start != null)
                                    {
                                        Console.WriteLine($"[*] Successfully mapped {edge.start.Value} to {matchedId}");
                                        edge.start.Value = matchedId;
                                    }
                                        
                                    else if (edgeNode.Equals("end", StringComparison.OrdinalIgnoreCase) && edge.end != null)
                                    {
                                        Console.WriteLine($"[*] Successfully mapped {edge.end.Value} to {matchedId}");
                                        edge.end.Value = matchedId;
                                    }
                                }
                                else
                                {
                                    Console.WriteLine($"[*] Mapping failed for {edgeNode}: {GetNodeValue(edge, edgeNode)}");

                                    if (onFail == MatchFailBehavior.Skip)
                                    {
                                        skipEdge = true;
                                        break;
                                    }
                                    else if (onFail == MatchFailBehavior.Import)
                                    {
                                        string unknownId = $"{GetNodeValue(edge, edgeNode)}_unknown";

                                        if (!existingNodeIds.ContainsKey(unknownId))
                                        {
                                            var newNode = new Node
                                            {
                                                Id = unknownId,
                                                Kinds = new List<string> { "Generic_OpenGraph" },
                                                Properties = new Dictionary<string, object> { { "name", unknownId } }
                                            };

                                            lock (GraphData.Graph.Nodes)
                                            {
                                                GraphData.Graph.Nodes.Add(newNode);
                                            }

                                            existingNodeIds.TryAdd(unknownId, true);
                                            Console.WriteLine($"[*] Created generic node: {unknownId}");
                                        }

                                        if (edgeNode.Equals("start", StringComparison.OrdinalIgnoreCase) && edge.start != null)
                                            edge.start.Value = unknownId;
                                        else if (edgeNode.Equals("end", StringComparison.OrdinalIgnoreCase) && edge.end != null)
                                            edge.end.Value = unknownId;
                                    }
                                }
                            }
                        }

                        if (!skipEdge)
                        {
                            edge.toValidate = null;
                            processedEdges.Add(edge);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("[!] Error processing edge: " + ex.Message);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);

            GraphData.Graph.Edges = processedEdges.ToList();
        }

        private string GetNodeValue(Edge edge, string edgeNode)
        {
            if (edgeNode.Equals("start", StringComparison.OrdinalIgnoreCase) && edge.start != null)
                return edge.start.Value;
            if (edgeNode.Equals("end", StringComparison.OrdinalIgnoreCase) && edge.end != null)
                return edge.end.Value;
            return "unknown";
        }




        public async Task<string> MapAttributesAsync(List<ValidateAttrib> attribs)
        {
            if (attribs == null || attribs.Count == 0)
                return string.Empty;

            var filters = new List<string>();
            var cacheKeyBuilder = new StringBuilder();

            attribs = attribs.OrderBy(x => x.attribValue).ToList();

            foreach (var attrib in attribs)
            {
                string val = attrib.attribValue.Replace("'", "\\'");
                string expr;
                string matchKey;

                if (attrib.partialMatch)
                {
                    expr = $"toLower(n.{attrib.bhAttrib}) CONTAINS toLower('{val}')";
                    matchKey = $"{attrib.bhAttrib}:*{val}*";
                }
                else
                {
                    expr = $"toLower(n.{attrib.bhAttrib}) = toLower('{val}')";
                    matchKey = $"{attrib.bhAttrib}:{val}";
                }

                filters.Add(expr);
                cacheKeyBuilder.Append(matchKey).Append("&&");
            }

            string cacheKey = cacheKeyBuilder.ToString().TrimEnd('&');

            if (AttributeCache.TryGetValue(cacheKey, out string cached))
                return cached;

            string whereClause = string.Join(" AND ", filters);
            string query = $"MATCH (n) WHERE {whereClause} RETURN n";

            string result = await BloodhoundAPI.CypherQueryAsync(query);
            if (result == null)
            {
                AttributeCache.TryAdd(cacheKey, string.Empty); // cache failure
                return string.Empty;
            }

            JObject res = JObject.Parse(result);
            var nodes = res["data"]?["nodes"] as JObject;

            if (nodes != null)
            {
                var objectIds = new List<string>();

                foreach (var node in nodes.Properties())
                {
                    var obj = node.Value as JObject;
                    var objectId = obj?["objectId"]?.ToString();

                    if (!string.IsNullOrEmpty(objectId))
                        objectIds.Add(objectId);
                }

                if (objectIds.Count == 1)
                {
                    AttributeCache.TryAdd(cacheKey, objectIds[0]);
                    return objectIds[0];
                }
            }

            AttributeCache.TryAdd(cacheKey, string.Empty);
            return string.Empty;
        } 

        public bool UploadSerializedData()
        {
            var settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            };
            string jsonPayload = JsonConvert.SerializeObject(GraphData, Formatting.None, settings);

            //Console.WriteLine(jsonPayload.ToString());
            return BloodhoundAPI.UploadJson(jsonPayload);
        }        
    }

    public enum MatchFailBehavior
    {
        Skip,
        Import
    }


    public class GraphData
    {
        [JsonProperty("metadata")]
        public Metadata Metadata { get; set; }

        [JsonProperty("graph")]
        public Graph Graph { get; set; }
    }

    public class Metadata
    {
        [JsonProperty("source_kind")]
        public string SourceKind { get; set; }
    }

    public class Graph
    {
        [JsonProperty("nodes")]
        public List<Node> Nodes { get; set; }

        [JsonProperty("edges")]
        public List<Edge> Edges { get; set; }
    }

    public class Node
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("kinds")]
        public List<string> Kinds { get; set; }

        [JsonProperty("properties")]
        public Dictionary<string, object> Properties { get; set; }
    }

    public class Edge
    {
        public string kind { get; set; }
        public EdgeNode start { get; set; }
        public EdgeNode end { get; set; }
        public Dictionary<string, List<ValidateAttrib>> toValidate { get; set; }
        public Dictionary<string, object> properties { get; set; }
    }

    public class EdgeNode
    {
        [JsonProperty("match_by")]
        public string MatchBy { get; set; } = "id";

        [JsonProperty("value")]
        public string Value { get; set; }

        [JsonProperty("kind", NullValueHandling = NullValueHandling.Ignore)]
        public string kind { get; set; }
    }

    public class ValidateAttrib
    {
        public string bhAttrib { get; set; }
        public string attribValue { get; set; }
        public bool partialMatch { get; set; }
    }

    public class ValidationAttrib
    {
        [JsonProperty("edgeNode")]
        public string EdgeNode { get; set; }

        [JsonProperty("bhAttrib")]
        public string BhAttrib { get; set; }

        [JsonProperty("attribValue")]
        public string AttribValue { get; set; }

        [JsonProperty("partialMatch")]
        public bool PartialMatch { get; set; }
    }

}
