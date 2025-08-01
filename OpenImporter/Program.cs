using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace OpenImporter
{
    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                if(args.Length == 0)
                {
                    DisplayHelp();
                    return;
                }
                string cmd = args[0];

                Dictionary<string, string> parsedArgs = ParseArgs(args);

                if(!parsedArgs.ContainsKey("user") || !parsedArgs.ContainsKey("password") || !parsedArgs.ContainsKey("bh"))
                {
                    Console.WriteLine("[X] Error: Missing required arguments");
                    DisplayHelp();
                    return;
                }
                //need to auth either way
                BloodhoundAPI.Authenticate(parsedArgs["bh"], parsedArgs["user"], parsedArgs["password"]);

                //import command logic
                if (cmd.Equals("import", StringComparison.OrdinalIgnoreCase))
                {
                    MatchFailBehavior onfail = MatchFailBehavior.Import;

                    if(parsedArgs.ContainsKey("onfail"))
                    {
                        if(Enum.TryParse(parsedArgs["onfail"], true, out onfail))
                        {
                            Console.WriteLine("[X] Error: Failed to parse onfail argument");
                            return;
                        }
                    }

                    if (!parsedArgs.ContainsKey("cache"))
                    {
                        Console.WriteLine("[X] Error: Include path to cache (/cache:)");
                        return;
                    }
                    if (!File.Exists(parsedArgs["cache"]))
                    {
                        Console.Error.WriteLine($"[!] File not found: {parsedArgs["cache"]}");
                        return;
                    }
                    InputParser toInput = new InputParser(parsedArgs["cache"]);
                    await toInput.ParseJson(onfail);
                    toInput.UploadSerializedData();
                }

                //updateicons command logic
                else if (cmd.Equals("updateicons", StringComparison.OrdinalIgnoreCase))
                {
                    if(!parsedArgs.ContainsKey("icons"))
                    {
                        Console.WriteLine("[X] Error: Include icon types to update");
                        return;
                    }
                    BloodhoundAPI.CustomIcons(parsedArgs["icons"]);
                }
                //unknown command
                else
                {
                    DisplayHelp();
                    return;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("[X] Generic Error: " + e.ToString());
                return;
            }
        }

        private static void DisplayHelp()
        {
            Console.WriteLine();
            Console.WriteLine("OpenImporter Supported Functions");
            Console.WriteLine("===========================================");
            Console.WriteLine();

            Console.WriteLine("[*] import");
            Console.WriteLine("    Imports a local JSON cache file into BloodHound.");
            Console.WriteLine("    Required Arguments:");
            Console.WriteLine("      /cache:<path>         Path to input JSON file");
            Console.WriteLine("      /bh:<http://host>     BloodHound API base URL (e.g., http://127.0.0.1:8080)");
            Console.WriteLine("      /user:<username>      Username for authentication");
            Console.WriteLine("      /password:<password>  Password for authentication");
            Console.WriteLine("    Optional Arguments:");
            Console.WriteLine("      /onfail:<mode>        Behavior when a match fails (default: Skip)");
            Console.WriteLine("                            Valid values: Skip, Import");
            Console.WriteLine();

            Console.WriteLine("[*] updateicons");
            Console.WriteLine("    Updates icons for custom node types in BloodHound.");
            Console.WriteLine("    Required Arguments:");
            Console.WriteLine("      /bh:<http://host>     BloodHound API base URL");
            Console.WriteLine("      /user:<username>      Username for authentication");
            Console.WriteLine("      /password:<password>  Password for authentication");
            Console.WriteLine("      /icons:<mapping>      Icon mappings, format: icon1|type1,icon2|type2");
            Console.WriteLine("      Supported Icons:      database, application, server, user, users, user-plus, robot, egg (can I offer you an egg in this trying time?)");
            Console.WriteLine();

            Console.WriteLine("[*] Examples:");
            Console.WriteLine("  OpenImporter.exe import /cache:data.json /bh:http://localhost:8080 /user:admin /password:verysecurepassword");
            Console.WriteLine("  OpenImporter.exe updateicons /bh:http://localhost:8080 /user:admin /password:verysecurepassword /icons:robot|ai_node,egg|TopSecret_App");
            Console.WriteLine();
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            Dictionary<string, string> ParsedArgs = new Dictionary<string, string>();
            foreach (string arg in args)
            {
                if (arg[0] != '/')
                {
                    continue;
                }
                string key = arg.Substring(1);
                string value = "";
                //logic to check for flags (no value appended)
                if (arg.IndexOf(':') > -1)
                {
                    key = arg.Substring(1, arg.IndexOf(':') - 1);
                    value = arg.Substring(arg.IndexOf(':') + 1);
                }

                key = key.ToLower();

                //normalize arg names
                if(key == "u")
                {
                    key = "user";
                }
                if(key == "p")
                {
                    key = "password";
                }


                ParsedArgs.Add(key, value);
            }
            return ParsedArgs;
        }

    }
}
