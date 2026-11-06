using Microsoft.AspNetCore.StaticFiles;

namespace TabletDeck;

internal static class NotebookStaticFiles
{
    internal static StaticFileOptions Options
    {
        get
        {
            var provider = new FileExtensionContentTypeProvider();
            provider.Mappings[".mjs"] = "text/javascript";
            provider.Mappings[".bcmap"] = "application/octet-stream";
            provider.Mappings[".pfb"] = "application/x-font-type1";
            return new StaticFileOptions { ContentTypeProvider = provider };
        }
    }
}
