using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace LordHelm.Skills;

/// <summary>
/// Produces a canonical byte form of a skill manifest XML document and its SHA-256 identity.
/// Pipeline: parse (PreserveWhitespace=false) -> compact the ParameterSchema JSON CDATA ->
/// apply W3C Exclusive Canonical XML (C14N 1.0) -> SHA-256 -> lowercase hex.
/// This invariant is load-bearing: changing it invalidates every stored hash.
/// </summary>
public static class SkillCanonicalizer
{
    public sealed record Canonical(byte[] Bytes, string Sha256Hex, string CanonicalXml);

    public static Canonical Canonicalize(string rawXml)
    {
        ArgumentException.ThrowIfNullOrEmpty(rawXml);

        var doc = new XmlDocument { PreserveWhitespace = false };
        using (var sr = new StringReader(rawXml))
        using (var xr = XmlReader.Create(sr, new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        }))
        {
            doc.Load(xr);
        }

        CompactParameterSchema(doc);

        var transform = new XmlDsigExcC14NTransform();
        transform.LoadInput(doc);
        using var ms = (Stream)transform.GetOutput(typeof(Stream));
        using var copy = new MemoryStream();
        ms.CopyTo(copy);
        var bytes = copy.ToArray();

        var hash = SHA256.HashData(bytes);
        var hex = Convert.ToHexStringLower(hash);
        var canonXml = Encoding.UTF8.GetString(bytes);

        return new Canonical(bytes, hex, canonXml);
    }

    private static void CompactParameterSchema(XmlDocument doc)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("s", SkillManifestSchema.TargetNamespace);
        var node = doc.SelectSingleNode("/s:Skill/s:ParameterSchema", ns);
        if (node is null) return;

        var raw = node.InnerText;
        if (string.IsNullOrWhiteSpace(raw))
        {
            node.InnerText = string.Empty;
            return;
        }

        try
        {
            using var jdoc = JsonDocument.Parse(raw);
            using var outBuf = new MemoryStream();
            using (var writer = new Utf8JsonWriter(outBuf, new JsonWriterOptions { Indented = false }))
            {
                jdoc.WriteTo(writer);
            }
            node.InnerText = Encoding.UTF8.GetString(outBuf.ToArray());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "ParameterSchema CDATA is not valid JSON; cannot canonicalize.", ex);
        }
    }
}
