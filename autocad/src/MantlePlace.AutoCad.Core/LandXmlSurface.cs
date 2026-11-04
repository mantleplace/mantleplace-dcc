using System.Globalization;
using System.Xml;

namespace MantlePlace.AutoCad;

/// <summary>
/// Reads which surface the block's LandXML holds, so Civil 3D can be asked to build that one by its own
/// name. Pure: text in, a name or the reason there is none out. Nothing about the surface's points,
/// units or coordinate system is read here; Civil 3D reads the file.
/// </summary>
public static class LandXmlSurface
{
    /// <summary>
    /// The name of the file's one surface, or <c>null</c> and the sentence that says why there is none
    /// to build: no surface, more than one (the plugin does not choose), a surface with no name, or text
    /// that is not LandXML. Never throws on the file's content.
    /// </summary>
    public static string? TryReadName(TextReader reader, out string? name)
    {
        ArgumentNullException.ThrowIfNull(reader);
        name = null;
        List<string?> names = [];

        try
        {
            using XmlReader xml = XmlReader.Create(reader, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            int depth = -1;
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (xml.LocalName == "Surfaces" && depth < 0)
                {
                    depth = xml.Depth;
                }
                else if (xml.LocalName == "Surface" && depth >= 0 && xml.Depth == depth + 1)
                {
                    names.Add(xml.GetAttribute("name"));
                }
            }
        }
        catch (XmlException ex)
        {
            return $"its file is not LandXML Civil 3D can be asked to read ({ex.Message})";
        }

        if (names.Count == 0)
        {
            return "its file holds no surface";
        }

        if (names.Count > 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"its file holds {names.Count} surfaces, and the plugin does not choose one");
        }

        if (string.IsNullOrWhiteSpace(names[0]))
        {
            return "its file's surface has no name to ask Civil 3D for";
        }

        name = names[0];
        return null;
    }
}
