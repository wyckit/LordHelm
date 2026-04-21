using System.Xml;
using System.Xml.Schema;
using NJsonSchema;

namespace LordHelm.Skills;

public sealed class ManifestValidator
{
    public ValidationReport Validate(string rawXml)
    {
        var errors = new List<ValidationError>();

        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = SkillManifestSchema.Schemas,
        };
        settings.ValidationFlags |= XmlSchemaValidationFlags.ReportValidationWarnings;
        settings.ValidationEventHandler += (_, e) =>
            errors.Add(new ValidationError(
                ValidationStage.Xsd,
                e.Message,
                e.Exception?.LineNumber ?? 0,
                e.Exception?.LinePosition ?? 0));

        try
        {
            using var sr = new StringReader(rawXml);
            using var xr = XmlReader.Create(sr, settings);
            while (xr.Read()) { }
        }
        catch (XmlException ex)
        {
            errors.Add(new ValidationError(ValidationStage.Xsd, ex.Message, ex.LineNumber, ex.LinePosition));
            return new ValidationReport(errors);
        }

        if (errors.Count > 0)
        {
            return new ValidationReport(errors);
        }

        var schemaJson = ExtractParameterSchemaRaw(rawXml);
        if (schemaJson is null)
        {
            errors.Add(new ValidationError(ValidationStage.JsonSchema,
                "ParameterSchema element missing after XSD pass.", 0, 0));
            return new ValidationReport(errors);
        }

        try
        {
            var schema = JsonSchema.FromJsonAsync(schemaJson).GetAwaiter().GetResult();
            if (schema.SchemaVersion is null ||
                !schema.SchemaVersion.Contains("2020-12", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ValidationError(ValidationStage.JsonSchema,
                    $"ParameterSchema must declare $schema for Draft 2020-12 (saw '{schema.SchemaVersion}').",
                    0, 0));
            }
        }
        catch (Exception ex)
        {
            errors.Add(new ValidationError(ValidationStage.JsonSchema,
                $"ParameterSchema JSON invalid: {ex.Message}", 0, 0));
        }

        return errors.Count == 0 ? ValidationReport.Valid : new ValidationReport(errors);
    }

    private static string? ExtractParameterSchemaRaw(string rawXml)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(rawXml);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("s", SkillManifestSchema.TargetNamespace);
        return doc.SelectSingleNode("/s:Skill/s:ParameterSchema", ns)?.InnerText;
    }
}
