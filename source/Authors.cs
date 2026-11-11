// Authors: clicking an author's name (a search result, or a mod's details) shows everything of theirs in Add mods,
// with switches for their mods, resource packs, data packs and shader packs. The searches take an author filter:
// Modrinth's by username (the "author" its search results carry), CurseForge's by author id.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MinecraftModManager
{
    static partial class Program
    {
        // A Modrinth search facet for one author's projects, or null when there's no author.
        static string AuthorFacet(string author)
        {
            if (string.IsNullOrWhiteSpace(author)) return null;
            author = author.Trim();
            if (!Regex.IsMatch(author, @"^[\w.\- ]{1,64}$")) throw new UserError("That isn't an author's name.");
            return "author:" + author;
        }

        // An author typed into the search bar ("@name"). Modrinth: a search for their projects, which also gives the
        // name as Modrinth spells it (users and organisations alike). CurseForge has no author lookup, but its search
        // matches author names, so an author of one of the results with exactly that name gives their id.
        static Dictionary<string, object> FindAuthor(string typed)
        {
            string name = (typed ?? "").Trim().TrimStart('@').Trim();
            if (name.Length == 0) throw new UserError("Type an author's name after the @.");
            AuthorFacet(name); // throws for a name no site could have
            string mrName = null, cfName = null;
            long cfId = 0;
            var problems = new List<string>();
            try
            {
                // Modrinth's author filter wants the name spelled exactly, capitals too: as typed, else the spelling
                // of the user (or organisation) with that name, which Modrinth looks up whatever the capitals.
                mrName = ModrinthAuthorProjects(name);
                if (mrName == null)
                {
                    string user = S(D(ModrinthGet("/user/" + Uri.EscapeDataString(name))), "username");
                    if (user != null && user != name) mrName = ModrinthAuthorProjects(user);
                }
                if (mrName == null)
                {
                    string org = S(D(HttpJson("GET", "https://api.modrinth.com/v3/organization/" + Uri.EscapeDataString(name), null)), "name");
                    if (org != null && org != name) mrName = ModrinthAuthorProjects(org);
                }
            }
            catch (UserError ex) { problems.Add("Modrinth: " + ex.Message); }
            if (CfReady)
            {
                try
                {
                    Dictionary<string, object> result = D(CfHttp("GET", "/mods/search?gameId=" + CfMinecraft + "&sortField=2&sortOrder=desc&pageSize=50&searchFilter=" + Uri.EscapeDataString(name), null));
                    foreach (object mod in A(Get(result, "data")))
                    {
                        Dictionary<string, object> author = D(A(Get(D(mod), "authors")).Select(D).FirstOrDefault(a => a != null && string.Equals((S(a, "name") ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase)));
                        if (author == null) continue;
                        cfId = CfId(Get(author, "id"));
                        cfName = S(author, "name");
                        break;
                    }
                }
                catch (UserError ex) { problems.Add("CurseForge: " + ex.Message); }
            }
            if (mrName == null && cfId == 0)
                throw new UserError(problems.Count > 0 ? "Couldn't look " + name + " up. " + string.Join(" ", problems)
                    : "No author called " + name + " has anything on Modrinth" + (CfReady ? " or CurseForge" : "") + ".");
            return Obj("ok", true, "name", mrName ?? cfName, "mr", mrName != null, "cfId", cfId);
        }

        // The author's name as Modrinth's search gives it, when it finds any project of theirs; else null.
        static string ModrinthAuthorProjects(string author)
        {
            if (AuthorFacet(author) == null) return null;
            string facets = new JavaScriptSerializer().Serialize(new object[] { new[] { "author:" + author.Trim() } });
            Dictionary<string, object> result = D(ModrinthGet("/search?limit=1&facets=" + Uri.EscapeDataString(facets)));
            return S(D(A(Get(result, "hits")).FirstOrDefault()), "author");
        }

        static readonly Dictionary<string, string> AuthorByProject = new Dictionary<string, string>();

        // A Modrinth project's author as its search results name them (an organisation's projects carry the
        // organisation), so the author filter finds the same person. Asked once per project, then kept.
        static string ModrinthAuthor(string projectId)
        {
            if (string.IsNullOrEmpty(projectId)) return null;
            lock (AuthorByProject)
            {
                string known;
                if (AuthorByProject.TryGetValue(projectId, out known)) return known;
            }
            string author = null;
            try
            {
                string facets = new JavaScriptSerializer().Serialize(new object[] { new[] { "project_id:" + projectId } });
                Dictionary<string, object> result = D(ModrinthGet("/search?limit=1&facets=" + Uri.EscapeDataString(facets)));
                author = S(D(A(Get(result, "hits")).FirstOrDefault()), "author");
            }
            catch (UserError) { return null; }   // not kept: asked again next time
            lock (AuthorByProject) AuthorByProject[projectId] = author;
            return author;
        }
    }
}
