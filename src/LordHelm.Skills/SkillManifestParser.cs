using System.Xml;
using System.Xml.Linq;
using LordHelm.Core;

namespace LordHelm.Skills;

public static class SkillManifestParser
{
    private static readonly XNamespace Ns = SkillManifestSchema.TargetNamespace;

    public static SkillManifest Parse(string rawXml)
    {
        var canon = SkillCanonicalizer.Canonicalize(rawXml);
        var doc = XDocument.Parse(canon.CanonicalXml, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidDataException("Manifest has no root element.");

        string Req(string name) =>
            root.Element(Ns + name)?.Value
            ?? throw new InvalidDataException($"Required element <{name}> missing.");

        var id = Req("Id");
        var version = SemVer.Parse(Req("Version"));
        var execEnv = Enum.Parse<ExecutionEnvironment>(Req("ExecutionEnvironment"), ignoreCase: false);
        var requiresApproval = XmlConvert.ToBoolean(Req("RequiresApproval"));
        var riskTier = Enum.Parse<RiskTier>(Req("RiskTier"), ignoreCase: false);
        var timeout = XmlConvert.ToTimeSpan(Req("Timeout"));
        var minTrust = Enum.Parse<TrustLevel>(Req("MinTrust"), ignoreCase: false);
        var parameterSchema = Req("ParameterSchema");

        return new SkillManifest(
            Id: id,
            Version: version,
            ContentHashSha256: canon.Sha256Hex,
            ExecEnv: execEnv,
            RequiresApproval: requiresApproval,
            RiskTier: riskTier,
            Timeout: timeout,
            MinTrust: minTrust,
            ParameterSchemaJson: parameterSchema,
            CanonicalXml: canon.CanonicalXml);
    }
}
