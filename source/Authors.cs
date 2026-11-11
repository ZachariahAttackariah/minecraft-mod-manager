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
