using LordHelm.Skills;

namespace LordHelm.Skills.Tests;

public class ValidatorTests
{
    private readonly ManifestValidator _validator = new();

    private const string Valid = """
        <?xml version="1.0" encoding="utf-8"?>
        <Skill xmlns="https://lordhelm.dev/schemas/skill-manifest/v1">
          <Id>ok-skill</Id>
          <Version>0.1.0</Version>
          <ExecutionEnvironment>Host</ExecutionEnvironment>
          <RequiresApproval>false</RequiresApproval>
          <RiskTier>Read</RiskTier>
          <Timeout>PT5S</Timeout>
          <MinTrust>Low</MinTrust>
          <ParameterSchema><![CDATA[{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object"}]]></ParameterSchema>
        </Skill>
        """;

    [Fact]
    public void Valid_Manifest_Passes_Both_Stages()
    {
        var r = _validator.Validate(Valid);
        Assert.True(r.IsValid, string.Join("; ", r.Errors.Select(e => $"[{e.Stage}] {e.Message}")));
    }

    [Fact]
    public void Invalid_Enum_Fails_Xsd()
    {
        var bad = Valid.Replace("<RiskTier>Read</RiskTier>", "<RiskTier>Nope</RiskTier>");
        var r = _validator.Validate(bad);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Stage == ValidationStage.Xsd);
    }

    [Fact]
    public void Missing_Required_Element_Fails_Xsd()
    {
        var bad = Valid.Replace("<MinTrust>Low</MinTrust>", "");
        var r = _validator.Validate(bad);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Stage == ValidationStage.Xsd);
    }

    [Fact]
    public void Wrong_JsonSchema_Draft_Fails_Stage2()
    {
        var bad = Valid.Replace(
            "\"$schema\":\"https://json-schema.org/draft/2020-12/schema\"",
            "\"$schema\":\"http://json-schema.org/draft-07/schema#\"");
        var r = _validator.Validate(bad);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Stage == ValidationStage.JsonSchema);
    }

    [Fact]
    public void Malformed_Json_Cdata_Fails_Stage2()
    {
        var bad = Valid.Replace(
            "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\"}",
            "{ not json }");
        var r = _validator.Validate(bad);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Stage == ValidationStage.JsonSchema);
    }

    [Fact]
    public void Bad_SkillId_Pattern_Fails_Xsd()
    {
        var bad = Valid.Replace("<Id>ok-skill</Id>", "<Id>Bad_ID!</Id>");
        var r = _validator.Validate(bad);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Stage == ValidationStage.Xsd);
    }
}
