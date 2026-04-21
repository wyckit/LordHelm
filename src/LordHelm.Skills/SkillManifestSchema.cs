using System.Reflection;
using System.Xml;
using System.Xml.Schema;

namespace LordHelm.Skills;

public static class SkillManifestSchema
{
    public const string TargetNamespace = "https://lordhelm.dev/schemas/skill-manifest/v1";

    private static readonly Lazy<XmlSchemaSet> _schemaSet = new(LoadSchema);

    public static XmlSchemaSet Schemas => _schemaSet.Value;

    private static XmlSchemaSet LoadSchema()
    {
        var asm = typeof(SkillManifestSchema).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("skill-manifest.xsd", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("skill-manifest.xsd not embedded in assembly.");

        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Could not open embedded resource {name}.");
        using var reader = XmlReader.Create(stream);

        var set = new XmlSchemaSet();
        set.Add(TargetNamespace, reader);
        set.Compile();
        return set;
    }
}
