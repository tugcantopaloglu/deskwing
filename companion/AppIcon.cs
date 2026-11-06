using System.Drawing;
using System.Reflection;

namespace TabletDeck;
internal static class AppIcon
{
    public static Icon Load(){using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("DeskWing.AppIcon.ico")??throw new InvalidOperationException("Application icon is missing.");using var icon=new Icon(stream);return (Icon)icon.Clone();}
}
