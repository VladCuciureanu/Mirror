using Mirror.Resources.Framework.Base;

namespace Mirror.Plugins.Resolvers;

/* Containers holding the editor's copy of the game's packages (IoStore optional segments, "Foo.o.uasset"), shipped
   somewhere other than the game's own folder. The editor copy carries what the cook strips (material graphs), and a
   game can stop shipping it with the client: Fortnite 43.00 ships it only with UEFN. They mount beside the game's
   containers, before the keys, so the encrypted ones mount as their key arrives. */
public interface IEditorSegmentsPlugin : IPlugin
{
    /* The optional-segment containers (.utoc, with their .ucas beside them) for this profile's build, or none */
    IEnumerable<FileInfo> ResolveContainers(BaseProfile profile);
}
