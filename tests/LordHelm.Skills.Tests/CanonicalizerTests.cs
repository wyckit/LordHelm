using LordHelm.Skills;

namespace LordHelm.Skills.Tests;

public class CanonicalizerTests
{
    private const string Minimal = """
        <?xml version="1.0" encoding="utf-8"?>
        <Skill xmlns="https://lordhelm.dev/schemas/skill-manifest/v1">
          <Id>sample-skill</Id>
          <Version>1.0.0</Version>
          <ExecutionEnvironment>Host</ExecutionEnvironment>
          <RequiresApproval>false</RequiresApproval>
          <RiskTier>Read</RiskTier>
          <Timeout>PT10S</Timeout>
          <MinTrust>Low</MinTrust>
          <ParameterSchema><![CDATA[{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object"}]]></ParameterSchema>
        </Skill>
        """;

    [Fact]
    public void Canonicalize_Produces_Deterministic_Hash()
    {
        var a = SkillCanonicalizer.Canonicalize(Minimal);
        var b = SkillCanonicalizer.Canonicalize(Minimal);
        Assert.Equal(a.Sha256Hex, b.Sha256Hex);
        Assert.Equal(64, a.Sha256Hex.Length);
    }

    [Fact]
    public void Hash_Invariant_Under_Whitespace_Changes()
    {
        var compact = Minimal.Replace("\r\n", "\n").Replace("  ", "");
        var spaced = Minimal.Replace("<Skill", "<Skill \n  ");
        var h1 = SkillCanonicalizer.Canonicalize(Minimal).Sha256Hex;
        var h2 = SkillCanonicalizer.Canonicalize(compact).Sha256Hex;
        var h3 = SkillCanonicalizer.Canonicalize(spaced).Sha256Hex;
        Assert.Equal(h1, h2);
        Assert.Equal(h1, h3);
    }

    [Fact]
    public void Hash_Invariant_Under_Json_Whitespace_Inside_CDATA()
    {
        var pretty = Minimal.Replace(
            "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\"}",
            """
            {
                "$schema": "https://json-schema.org/draft/2020-12/schema",
                "type":   "object"
            }
            """);
        var h1 = SkillCanonicalizer.Canonicalize(Minimal).Sha256Hex;
        var h2 = SkillCanonicalizer.Canonicalize(pretty).Sha256Hex;
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void Hash_Changes_When_Content_Changes()
    {
        var other = Minimal.Replace("<RiskTier>Read</RiskTier>", "<RiskTier>Write</RiskTier>");
        var h1 = SkillCanonicalizer.Canonicalize(Minimal).Sha256Hex;
        var h2 = SkillCanonicalizer.Canonicalize(other).Sha256Hex;
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void Invalid_Json_Cdata_Throws()
    {
        var bad = Minimal.Replace(
            "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\"}",
            "not-json{{{");
        Assert.Throws<InvalidDataException>(() => SkillCanonicalizer.Canonicalize(bad));
    }
}
