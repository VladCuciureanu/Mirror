using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;

namespace Mirror.Cloud.Controllers;

/* A cooked package and the editor's copy of it (its optional segment, "Foo.o.uasset").
 *
 * CUE4Parse merges an IoStore package's optional segment into the package itself: the segment's exports follow the
 * cooked ones in one export table, and nothing is listed under "Foo.o.uasset" any more. Fortnite 43.00 ships those
 * segments only with UEFN, whose ".o.utoc" containers mount beside the game's (IEditorSegmentsPlugin). Packages from
 * containers that list the segment as a file of its own are still found by that name. */
public static class EditorSegment
{
    /* How many of a package's exports are its own: with a segment merged in, the cooked package's export map is
     * the first part of the table */
    public static int CookedCount(IPackage package) => package is IoPackage io ? io.ExportMap.Length : package.ExportMapLength;

    /* The cooked exports alone. Enumerating the whole table would read the segment's exports too, and they are only
     * readable against the editor's schema, which the caller hasn't put in place */
    public static IEnumerable<UObject> CookedExports(IPackage package) => package.GetExports().Take(CookedCount(package));

    /* The editor copy's exports, or null when there is none. Read as they are enumerated, so the caller puts the
     * editor's schema in place first; the cooked exports are best read before that. */
    public static IReadOnlyList<UObject>? EditorExports(IFileProvider provider, IPackage cooked, string path)
    {
        if (cooked is IoPackage { HasOptionalSegment: true } merged)
        {
            return ((IPackage) merged).GetExports().Skip(merged.ExportMap.Length).ToList();
        }

        var segmentPath = $"{path.SubstringBeforeDot()}.o.uasset";

        return provider.TryLoadPackage(segmentPath, out var segment) ? segment.GetExports().ToList() : null;
    }

    private static string SubstringBeforeDot(this string path)
    {
        var dot = path.IndexOf('.');

        return dot < 0 ? path : path[..dot];
    }
}
