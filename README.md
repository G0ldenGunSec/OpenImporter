# OpenImporter

A middleware utility for enriching and uploading data gathered with arbitrary collectors to more effectively map to existing Bloodhound database objects. It also allows you to update arbitrary types to use one of several canned image types (stole most of these from mssqlhound - tyty)

# What is this?

When working with BH Open Graph, one of the issues I found myself running into was how to effectively map collected data to its paired object within a Bloodhound database. Currently, two options are supported - name and objectID (SID/GUID, depending on object type). In many circumstances it can be difficult to map exactly to a fully qualified name or retrieve the SID for an object given a narrow scope of collection. For example, when collecting data from SCCM it is rather trivial to pull the last logged-on user and their domain from tables, but is a rather big hassle to try and pick out their SID based on doing partial string matches from profile data. This isn't possible at all when looking at other attack paths such as vaulted credentials, which are stored in a domain\user format. It would likely be possible to stitch together some static rules (e.g., append '@domain.local' to every username we collect), but these are rather brittle, and dont scale well in more complex multi-domain environments. 

To address these issues, I figured why not just use the data we already have (if you have performed AD data collection with Sharphound or -REDACTED-) and query this to build out an import that will map to actual nodes, instead of creating secondary orphaned objects. This was all well and good, but then left me with a second problem of how to make this scalable across multiple ingestors, as well as easily indicate what needed to be searched and how it needed to be mapped (e.g., partial match - something like domain vs fqdn, vs exact match for something like samaccountname). The end result is a hopefully fairly easily insertable JSON property that an arbitrary collector utility wanting to use this can insert as they want within their output json. 

Specifically, it adds an optional "toValidate" attribute to edge objects that has optional sub-attribute of "start" and "end". These sub-attribute contain information about which Bloodhound attribute to query against, what value they should use to query, and if the match should be partial or exact. An example of an extended json edge object can be seen below:

```
{
        "kind": "sccm_VaultedCredential",
        "start": {
          "value": "S-1-5-21-776883259-276049105-2214736399-1113"
        },
        "end": {
          "value": "NORTHSTAR\\naa"
        },
        "properties": {},
        "toValidate": {
          "end": [
            {
              "bhAttrib": "samaccountname",
              "attribValue": "naa",
              "partialMatch": false
            },
            {
              "bhAttrib": "domain",
              "attribValue": "NORTHSTAR",
              "partialMatch": true
            }
          ]
        }
}
```

In the above you can see the new toValidate attribute which instructs OpenImporter to perform a lookup that looks roughly like this:

`MATCH (n) WHERE n.name is not NULL and toLower(n.name) CONTAINS toLower('naa') AND toLower(n.domain) CONTAINS toLower('north') RETURN n`

On startup, OpenImporter first connects to an operator-provided Bloodhound instance and then will parse an input json file. When validation blocks are identified, OpenImporter will perform a cypher query using the Bloodhound API that will attempt to map a given set of parameters to a single (==1) value. != results will result in a failure to map (too narrow / too broad of a filter). The nice part about this is that these toValidate properties are completely arbitrary, you can map against any number of any properties that exist within the database. If exactly one match is found OpenImporter will update the appropriate edge value (start/end) with the identified value, allowing it to be successfully mapped to an existing Bloodhound object. This will hopefully be more useful in the future when pathfinding is enabled for OpenGraph edges, although I did find that if you replace the "kind" on your OpenGraph edge with an existing Bloodhound edge type (as defined within ad.cue within BH source) you can get a traversable graph that doesnt require Cypher to browse (e.g., I could replace sccm_Execute with executeDCOM, allowing me to traverse this edge in a normal graph). There are definitely some pros / cons to introducing data like this into your graph, but if you are aware of the limitations of your tooling this can be a very useful technique in the short-term to expand the usefulness of collected data.

## Usage

This is a console app, like all cool hacker tools are. After building you'll have two commands, both of which share some required flags, and some of which are command-specific. If you want to try a collector that's already set up to use this, check out SCCM_SQL_Collector, a PoC tool released alongside this used to grab attackable paths from an SCCM Site DB.

**Root Modules**

`import` :: Imports a local JSON file into BloodHound and queries the DB when necessary to map accounts.
`updateicons` :: Changes arbitrary object types from the default question mark to something easier on the eyes

**Shared Flags**

`/bh:` :: host + port combo to bloodhound install. should be in format of `http://10.0.0.1:8080`
`/user:` :: your bloodhound user id (default `admin`)
`/password:` :: your bloodhound password

**Import Flags**

`/cache:` :: Required arg, path to json you want to import
`/onfail:` :: what to do if OpenImporter cant perform a match. Supported options: Skip, Import   <-- Skip will drop the edge entirely if it cant map it, whereas Import will create a node of type generic pointing to the unmappable object and load the edge. Default behavior is Import.

**UpdateIcons Flags**

`/icons:` :: Allows you to associate one of several pre-built icons with an arbitrary type in your database. Supported pre-built types are as follows: database, application, server, user, users, user-plus, robot, egg (can I offer you an egg in this trying time?). Usage of this one is a bit trick, but in general is in a format of /icons:[pre-built type],[your custom type];[other pre-built type],[other custom type]...  so like `/icons:database,sccm_app;egg,sccm_administrator`

## Example commands

`OpenImporter.exe import /cache:data.json /bh:http://localhost:8080 /user:admin /password:verysecurepassword`

`OpenImporter.exe updateicons /bh:http://localhost:8080 /user:admin /password:verysecurepassword /icons:robot,ai_node;egg,TopSecret_App`







