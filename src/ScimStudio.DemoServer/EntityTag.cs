using System.Globalization;
using Microsoft.Net.Http.Headers;

namespace ScimStudio.DemoServer;

/// <summary>
/// The entity tags that version resources (RFC 7644 section 3.14): what meta.version and the ETag header carry, and what the
/// conditions of If-Match and If-None-Match name.
/// </summary>
internal static class EntityTag {
    /// <summary>
    /// The tag of a resource at a revision. Weak, since one revision has many representations: <c>attributes</c> trims them and
    /// the attributes derived from other resources, a user's groups, change them.
    /// </summary>
    /// <param name="revision">The revision, 1 for a resource just created.</param>
    public static string Weak(int revision) {
        return string.Create(CultureInfo.InvariantCulture, $"W/\"{revision}\"");
    }

    /// <summary>
    /// Whether a condition names a tag: <c>*</c> names any, a list the tags in it. Tags compare weakly (RFC 7232 section 2.3.2),
    /// as the If-Match examples of RFC 7644 section 3.14 need, so <c>"3"</c> names <c>W/"3"</c>. A condition that does not
    /// parse names none.
    /// </summary>
    /// <param name="condition">An If-Match or If-None-Match header, or the version of a bulk operation.</param>
    /// <param name="tag">The tag of the resource.</param>
    public static bool Matches(string condition, string tag) {
        if (!EntityTagHeaderValue.TryParseList([condition], out var candidates)) {
            return false;
        }
        var current = EntityTagHeaderValue.Parse(tag);
        return candidates.Any(candidate => candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(current, useStrongComparison: false));
    }
}
