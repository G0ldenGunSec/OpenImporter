using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Policy;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenImporter
{
    internal class BloodhoundAPI
    {
        private static HttpClient bhClient = null;
        private static string bhUrl = "";
        public static bool Authenticate(string baseUrl, string username, string password)
        {
            //normalize URL           
            bhUrl = baseUrl.TrimEnd('/');  
            if (!bhUrl.StartsWith("http://") && !bhUrl.StartsWith("https://"))
            {
                Console.Error.WriteLine("[!] Invalid Bloodhound URL. It should start with http:// or https://");
                return false;
            }
            if(bhUrl.EndsWith("/api/v2"))
            {
                bhUrl = bhUrl.Substring(0, bhUrl.Length - 8);
            }

            string authUrl = bhUrl + "/api/v2/login"; 

            Console.WriteLine("[*] Attempting to authenticate to Bloodhound API at " + authUrl);

            try
            {
                var loginPayload = new
                {
                    login_method = "secret",
                    username = username,
                    secret = password
                };

                var jsonLogin = JsonConvert.SerializeObject(loginPayload);
                var content = new StringContent(jsonLogin, Encoding.UTF8, "application/json");

                bhClient = new HttpClient();
                var loginResp = bhClient.PostAsync(authUrl, content).Result;

                if (!loginResp.IsSuccessStatusCode)
                {
                    Console.Error.WriteLine($"[!] Login failed: {loginResp.StatusCode}");
                    return false;
                }

                var respBody = loginResp.Content.ReadAsStringAsync().Result;
                var token = JObject.Parse(respBody)["data"]["session_token"].ToString();
                bhClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            catch(Exception e)
            {
                Console.WriteLine("[X] Error: Failed to auth to Bloodhound API");
                Console.WriteLine(" |-> Details: " + e.Message);
                return false;
            }
            bhClient.DefaultRequestHeaders.Add("Prefer", "0");
            Console.WriteLine("[*] Successfully authenticated to Bloodhound API");
            return true;
        }

        public static bool UploadJson(string jsonData)
        {
            try
            {
                Console.WriteLine("[*] Uploading JSON data to Bloodhound");

                //Start a file upload job
                var startResp = bhClient.PostAsync(bhUrl + "/api/v2/file-upload/start", null).Result;
                if (!startResp.IsSuccessStatusCode)
                {
                    Console.Error.WriteLine($"[!] Failed to start upload job: {startResp.StatusCode}");
                    return false;
                }

                var startJson = startResp.Content.ReadAsStringAsync().Result;
                var startParsed = JObject.Parse(startJson);
                int jobId = startParsed["data"]?["id"]?.ToObject<int>() ?? -1;

                if (jobId < 0)
                {
                    Console.Error.WriteLine("[!] Could not extract upload job ID.");
                    return false;
                }

                //Upload JSON data to the new job
                var uploadContent = new StringContent(jsonData, Encoding.UTF8, "application/json");

                var uploadUri = $"{bhUrl}/api/v2/file-upload/{jobId}?file_upload_job_id={jobId}";
                var uploadResp = bhClient.PostAsync(uploadUri, uploadContent).Result;

                if (uploadResp.StatusCode != System.Net.HttpStatusCode.Accepted)
                {
                    Console.Error.WriteLine($"[!] Upload failed: {uploadResp.StatusCode}");
                    var errBody = uploadResp.Content.ReadAsStringAsync().Result;
                    Console.Error.WriteLine(errBody);
                    //we dont want to return false here, as we still want to stop the job, otherwise you have a running job in Bloodhound forever
                }

                //Stop the upload job to trigger processing of data

                var endUri = $"{bhUrl}/api/v2/file-upload/{jobId}/end";
                var endResp = bhClient.PostAsync(endUri, new StringContent(string.Empty)).Result;

                if (endResp.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    Console.Error.WriteLine($"[!] Finalization failed: {endResp.StatusCode}");
                    var endError = endResp.Content.ReadAsStringAsync().Result;
                    Console.Error.WriteLine(endError);
                    return false;
                }
            }
            catch(Exception e)
            {
                Console.WriteLine("[X] Error: Failed to upload JSON data to Bloodhound");
                Console.WriteLine(" |-> Details: " + e.Message);
                return false;
            }

            Console.WriteLine("[+] Successfully uploaded and triggered data ingestion");
            return true;
        }

        private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(10);

        
        public static async Task<string> CypherQueryAsync(string query)
        {
            string queryUrl = bhUrl + "/api/v2/graphs/cypher";
            await _semaphore.WaitAsync();
            try
            {
                var payload = new
                {
                    query = query,
                    include_properties = true
                };

                var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                try
                {
                    var resp = await bhClient.PostAsync(queryUrl, content);
                    if (!resp.IsSuccessStatusCode)
                    {
                        Console.Error.WriteLine($"[!] Query failed: {resp.StatusCode}");
                        return null;
                    }
                    return await resp.Content.ReadAsStringAsync();
                }
                catch (Exception e)
                {
                    Console.WriteLine("[X] Error running cypher query");
                    Console.WriteLine(" |-> Details: " + e.Message);
                    return null;
                }
                
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public static string CypherQuery(string query)
        {
            if (bhClient == null)
            {
                Console.Error.WriteLine("[!] Bloodhound client not initialized. Authenticate first.");
                return null;
            }

            try
            {
                var payload = new
                {
                    query = query,
                    include_properties = true
                };

                var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
               
                var response = bhClient.PostAsync($"{bhUrl}/api/v2/graphs/cypher", content).Result;

                if (!response.IsSuccessStatusCode)
                {
                    Console.Error.WriteLine($"[!] Cypher query failed: {response.StatusCode}");
                    return null;
                }

                var responseBody = response.Content.ReadAsStringAsync().Result;
                return responseBody;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[!] Exception while running Cypher query: " + ex.Message);
                return null;
            }
        }



        public static bool CustomIcons(string toUpdate)
        {
            var predefinedIcons = new Dictionary<string, IconDefinition>
    {
        { "database",   new IconDefinition { Type = "font-awesome", Name = "database",   Color = "#f54242" } },
        { "server",     new IconDefinition { Type = "font-awesome", Name = "server",     Color = "#4287f5" } },
        { "user",       new IconDefinition { Type = "font-awesome", Name = "user",       Color = "#42f554" } },
        { "users",      new IconDefinition { Type = "font-awesome", Name = "users",      Color = "#42f5e6" } },
        { "robot",      new IconDefinition { Type = "font-awesome", Name = "robot",      Color = "#b542f5" } },
        { "user-plus",  new IconDefinition { Type = "font-awesome", Name = "user-plus",  Color = "#f5a742" } },
        { "egg",        new IconDefinition { Type = "font-awesome", Name = "egg",        Color = "#f54290" } }
    };

            var customTypes = new Dictionary<string, CustomTypeDefinition>();

            var mappings = toUpdate.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var map in mappings)
            {
                var parts = map.Split(new[] { ',' }, 2);
                if (parts.Length != 2)
                {
                    Console.Error.WriteLine($"[!] Invalid mapping format: {map} (expected format: icon,CustomType)");
                    return false;
                }

                string iconKey = parts[0].Trim().ToLower();
                string typeName = parts[1].Trim();

                if (!predefinedIcons.TryGetValue(iconKey, out var icon))
                {
                    Console.Error.WriteLine($"[!] Unsupported icon name: {iconKey}");
                    return false;
                }

                customTypes[typeName] = new CustomTypeDefinition
                {
                    Icon = icon
                };
            }

            var payload = new
            {
                custom_types = customTypes
            };

            try
            {
                var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
                Console.WriteLine(json); // For debugging
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = bhClient.PostAsync($"{bhUrl}/api/v2/custom-nodes", content).Result;

                if (!response.IsSuccessStatusCode)
                {
                    Console.Error.WriteLine($"[!] Upload failed: {response.StatusCode}");
                    string respBody = response.Content.ReadAsStringAsync().Result;
                    Console.Error.WriteLine(respBody);
                    return false;
                }

                Console.WriteLine("[+] Successfully uploaded custom node icons.");
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[!] Exception while uploading custom nodes: " + ex.Message);
                return false;
            }
        }
    }

    public class IconDefinition
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("color", NullValueHandling = NullValueHandling.Ignore)]
        public string Color { get; set; }
    }

    public class CustomTypeDefinition
    {
        [JsonProperty("icon")]
        public IconDefinition Icon { get; set; }
    }

}
