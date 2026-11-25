namespace JsonStudio.Core.Model;

/// <summary>A JSON document: the root node and its indent.</summary>
public sealed class JsonDocument
{
    public JsonDocument(JsonNode root, string indent = "  ")
    {
        Root = root;
        Indent = indent;
    }

    public JsonNode Root { get; set; }

    /// <summary>Two spaces, four spaces or a tab.</summary>
    public string Indent { get; set; }

    public static JsonDocument Sample() => new(Serialization.JsonParser.Parse(SampleText).Root!, "  ");

    public const string SampleText = """
{
  "plan": "FY27 Relationship Manager Incentive",
  "version": 3,
  "effective": "2027-01-01",
  "active": true,
  "currency": "USD",
  "targetIncentive": 42000,
  "measures": [
    { "id": "growth", "label": "New revenue growth", "weight": 0.5, "quota": 1250000,
      "accelerators": [ { "from": 1.0, "rate": 1.5 }, { "from": 1.2, "rate": 2.0 } ] },
    { "id": "renewal", "label": "Renewal rate", "weight": 0.3, "threshold": 0.85, "cap": 1.5 },
    { "id": "revenue", "label": "Total book revenue", "weight": 0.2, "quota": 8400000 }
  ],
  "renewalBonus": { "enabled": true, "amount": 2500, "note": "Paid when the renewal rate is 95% or higher" },
  "approvedBy": null
}
""";
}
