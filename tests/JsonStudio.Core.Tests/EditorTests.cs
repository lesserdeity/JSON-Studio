using JsonStudio.Core.Editing;
using JsonStudio.Core.Model;
using JsonStudio.Core.Serialization;

namespace JsonStudio.Core.Tests;

public class EditorTests
{
    private static DocumentEditor Sample() => new(JsonParser.ParseOrThrow(JsonDocument.SampleText));

    private static string Text(DocumentEditor e) => JsonWriter.Serialize(e.Root, "  ");

    private static JsonNode At(DocumentEditor e, string path)
    {
        // Small path helper: "measures/0/accelerators"
        var n = e.Root;
        foreach (var p in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            n = int.TryParse(p, out var i) && n.IsArray ? n.Children[i] : n.Get(p)!;
        return n;
    }

    /// <summary>Runs a command, then checks that one Undo restores the exact earlier document (and Redo re-applies it).</summary>
    private static void AssertUndoable(DocumentEditor e, Action command)
    {
        var before = Text(e);
        var beforeIds = e.Root.Descendants().Select(n => n.Id).ToList();
        command();
        var after = Text(e);
        Assert.True(e.Undo());
        Assert.Equal(before, Text(e));
        Assert.Equal(beforeIds, e.Root.Descendants().Select(n => n.Id).ToList());
        Assert.True(e.Redo());
        Assert.Equal(after, Text(e));
        Assert.True(e.Undo());
    }

    [Fact]
    public void PeerGroups_AllAcceleratorsAreOneGroup()
    {
        var e = new DocumentEditor(JsonParser.ParseOrThrow(
            "{\"measures\":[{\"accelerators\":[1]},{\"x\":1},{\"accelerators\":[]},{\"accelerators\":[2,3]}]}"));
        var a0 = At(e, "measures/0/accelerators");
        var a2 = At(e, "measures/2/accelerators");
        var a3 = At(e, "measures/3/accelerators");
        Assert.Equal("$.measures[*].accelerators", Schema.PeerKey(a0));
        Assert.Equal(Schema.PeerKey(a0), Schema.PeerKey(a2));
        var peers = Schema.GetPeers(e.Root, a2);
        Assert.Equal(new[] { a0, a2, a3 }, peers);
    }

    [Fact]
    public void PlainToObjectConversion()
    {
        var e = new DocumentEditor(JsonParser.ParseOrThrow(
            "{\"reps\":[{\"territoryIDs\":[\"T1\",\"T2\"]},{\"territoryIDs\":[\"T3\"]}]}"));
        var before = Text(e);
        var first = At(e, "reps/0/territoryIDs");
        var r = e.AddColumn(first, "primary", ColumnKind.Boolean);
        Assert.True(r.Success);
        Assert.Equal(3, r.Converted);
        Assert.Equal("territoryID", r.FieldName);
        Assert.Equal("Turned 3 plain values into objects. Each value is now in the \"territoryID\" column.", r.Message);
        Assert.Equal(
            "{\"reps\":[{\"territoryIDs\":[{\"territoryID\":\"T1\",\"primary\":false},{\"territoryID\":\"T2\",\"primary\":false}]}," +
            "{\"territoryIDs\":[{\"territoryID\":\"T3\"}]}]}",
            JsonWriter.Compact(e.Root));
        Assert.True(e.Undo());
        Assert.Equal(before, Text(e));
    }

    [Fact]
    public void PeerColumns_EmptyArrayShowsPeerColumns_AndAddRowIsBlank()
    {
        var e = Sample();
        var acc = At(e, "measures/0/accelerators");
        e.AddColumn(acc, "tier", ColumnKind.String);
        var renewal = At(e, "measures/1");
        var cols = e.Columns(At(e, "measures"));
        var accCol = cols.Single(c => c.Name == "accelerators");
        var empty = e.CreateContainerCell(At(e, "measures"), renewal, "accelerators", accCol.ContainerType!.Value);
        Assert.Equal(new[] { "from", "rate", "tier" }, e.Columns(empty).Select(c => c.Name));
        var row = e.AddRow(empty);
        Assert.Equal("{\"from\":0,\"rate\":0,\"tier\":\"\"}", JsonWriter.Compact(row));
        // accelerators is inserted at its column position (after cap, which comes later in column order? no: before threshold)
        Assert.Equal(new[] { "id", "label", "weight", "accelerators", "threshold", "cap" }, renewal.Children.Select(c => c.Key));
    }

    [Fact]
    public void MeasuresColumns_AreTheUnion()
    {
        var e = Sample();
        var cols = e.Columns(At(e, "measures"));
        Assert.Equal(new[] { "id", "label", "weight", "quota", "accelerators", "threshold", "cap" }, cols.Select(c => c.Name));
        Assert.Equal("array of objects", cols.Single(c => c.Name == "accelerators").TypeLabel);
        Assert.Equal("number", cols.Single(c => c.Name == "quota").TypeLabel);
    }

    [Fact]
    public void ArrayOfStringsHint_AddRowAddsEmptyString()
    {
        var e = Sample();
        var measures = At(e, "measures");
        e.AddColumn(measures, "regions", ColumnKind.ArrayOfStrings);
        var regions = At(e, "measures/1/regions");
        Assert.Empty(regions.Children);
        var cols = e.Columns(regions);
        Assert.Single(cols);
        Assert.True(cols[0].IsValueColumn);
        Assert.Equal("string", cols[0].TypeLabel);
        var row = e.AddRow(regions);
        Assert.Equal(NodeType.String, row.Type);
        Assert.Equal("", row.StringValue);
    }

    [Fact]
    public void ChangeColumnType_ToString_AndUndo()
    {
        var e = Sample();
        var measures = At(e, "measures");
        AssertUndoable(e, () => e.ChangeColumnType(measures, "quota", ColumnKind.String));
        e.ChangeColumnType(measures, "quota", ColumnKind.String);
        Assert.Equal("\"1250000\"", JsonWriter.Compact(At(e, "measures/0/quota")));
        Assert.Equal("\"8400000\"", JsonWriter.Compact(At(e, "measures/2/quota")));
    }

    [Fact]
    public void ChangeColumnType_ArrayOfObjects_WrapsPlainItems()
    {
        var e = new DocumentEditor(JsonParser.ParseOrThrow("[{\"tags\":[\"a\",\"b\"]},{\"tags\":\"x\"}]"));
        e.ChangeColumnType(e.Root, "tags", ColumnKind.ArrayOfObjects);
        Assert.Equal("[{\"tags\":[{\"tag\":\"a\"},{\"tag\":\"b\"}]},{\"tags\":[]}]", JsonWriter.Compact(e.Root));
        Assert.Equal("object", e.Hints["$[*].tags"]);
    }

    [Fact]
    public void RenameColumn_AppliesToPeers_AndRefusesExisting()
    {
        var e = Sample();
        var acc = At(e, "measures/0/accelerators");
        Assert.False(e.RenameColumn(acc, "rate", "from", out var err));
        Assert.Contains("already exists", err);
        AssertUndoable(e, () => e.RenameColumn(At(e, "measures"), "quota", "target", out _));
        e.RenameColumn(At(e, "measures"), "quota", "target", out _);
        Assert.NotNull(At(e, "measures/0").Get("target"));
        Assert.NotNull(At(e, "measures/2").Get("target"));
        Assert.Equal(3, At(e, "measures/0").Children.FindIndex(c => c.Key == "target"));
    }

    [Fact]
    public void DeleteColumn_AppliesToPeers()
    {
        var e = new DocumentEditor(JsonParser.ParseOrThrow("{\"m\":[{\"a\":[{\"x\":1,\"y\":2}]},{\"a\":[{\"x\":3,\"y\":4}]}]}"));
        AssertUndoable(e, () => e.DeleteColumn(At(e, "m/0/a"), "y"));
        e.DeleteColumn(At(e, "m/0/a"), "y");
        Assert.Equal("{\"m\":[{\"a\":[{\"x\":1}]},{\"a\":[{\"x\":3}]}]}", JsonWriter.Compact(e.Root));
    }

    [Fact]
    public void AddColumn_OnlyCurrentTable()
    {
        var e = new DocumentEditor(JsonParser.ParseOrThrow("{\"m\":[{\"a\":[{\"x\":1}]},{\"a\":[{\"x\":3}]}]}"));
        e.AddColumn(At(e, "m/0/a"), "z", ColumnKind.Number);
        Assert.Equal("{\"m\":[{\"a\":[{\"x\":1,\"z\":0}]},{\"a\":[{\"x\":3}]}]}", JsonWriter.Compact(e.Root));
        Assert.Equal(new[] { "x", "z" }, e.Columns(At(e, "m/1/a")).Select(c => c.Name));
        Assert.Equal("newField", e.NewColumnName(At(e, "m/1/a")));
    }

    [Fact]
    public void SetCellLiteral_MissingKeyInsertedAtColumnPosition()
    {
        var e = new DocumentEditor(JsonParser.ParseOrThrow("[{\"a\":1,\"b\":2,\"c\":3},{\"a\":1,\"c\":3}]"));
        var cols = e.Columns(e.Root);
        var b = cols.Single(c => c.Name == "b");
        AssertUndoable(e, () => e.SetCellLiteral(e.Root, e.Root.Children[1], b, "true"));
        e.SetCellLiteral(e.Root, e.Root.Children[1], b, "true");
        Assert.Equal("{\"a\":1,\"b\":true,\"c\":3}", JsonWriter.Compact(e.Root.Children[1]));
    }

    [Fact]
    public void SetCellLiteral_StringColumnKeepsText()
    {
        var e = new DocumentEditor(JsonParser.ParseOrThrow("[{\"a\":\"x\"},{}]"));
        var a = e.Columns(e.Root).Single();
        e.SetCellLiteral(e.Root, e.Root.Children[1], a, "42");
        Assert.Equal("\"42\"", JsonWriter.Compact(e.Root.Children[1].Get("a")!));
    }

    [Fact]
    public void TypingBurst_IsOneUndoStep()
    {
        var e = Sample();
        var plan = At(e, "plan");
        var before = Text(e);
        e.SetString(plan, "a");
        e.SetString(plan, "ab");
        e.SetString(plan, "abc");
        Assert.Equal(1, e.History.UndoCount);
        e.EndBurst();
        e.SetString(plan, "abcd");
        Assert.Equal(2, e.History.UndoCount);
        e.Undo();
        Assert.Equal("abc", At(e, "plan").StringValue);
        e.Undo();
        Assert.Equal(before, Text(e));
    }

    [Fact]
    public void TypingInDifferentField_StartsNewBurst()
    {
        var e = Sample();
        e.SetString(At(e, "plan"), "x");
        e.SetString(At(e, "currency"), "EUR");
        Assert.Equal(2, e.History.UndoCount);
    }

    [Fact]
    public void StructuralCommands_AreUndoable()
    {
        var e = Sample();
        AssertUndoable(e, () => e.AddChild(e.Root));
        AssertUndoable(e, () => e.Delete(At(e, "measures/1")));
        AssertUndoable(e, () => e.Duplicate(At(e, "measures/0")));
        AssertUndoable(e, () => e.Move(At(e, "measures/2"), At(e, "measures"), 0));
        AssertUndoable(e, () => e.Move(At(e, "plan"), At(e, "renewalBonus"), 0));
        AssertUndoable(e, () => e.Rename(At(e, "plan"), "planName"));
        AssertUndoable(e, () => e.ChangeType(At(e, "measures"), NodeType.Object));
        AssertUndoable(e, () => e.SortKeys());
        AssertUndoable(e, () => e.AddRow(At(e, "measures")));
        AssertUndoable(e, () => e.AddColumn(At(e, "measures"), "owner", ColumnKind.String));
        AssertUndoable(e, () => e.SetBoolean(At(e, "active"), false));
        AssertUndoable(e, () => e.Paste(e.Root, "{\"x\":[1,2]}"));
        AssertUndoable(e, () => e.ReplaceRoot(JsonParser.ParseOrThrow("[1,2,3]")));
    }

    [Fact]
    public void RedoIsClearedByNewChange()
    {
        var e = Sample();
        e.Delete(At(e, "plan"));
        e.Undo();
        Assert.True(e.History.CanRedo);
        e.Delete(At(e, "version"));
        Assert.False(e.History.CanRedo);
    }

    [Fact]
    public void UndoStack_KeepsAtMost200()
    {
        var e = Sample();
        for (int i = 0; i < 250; i++) e.SetBoolean(At(e, "active"), i % 2 == 0);
        Assert.Equal(200, e.History.UndoCount);
    }

    [Fact]
    public void SortKeys_NaturalOrder()
    {
        var e = new DocumentEditor(JsonParser.ParseOrThrow("{\"item10\":1,\"item2\":2,\"b\":{\"z\":1,\"a\":2},\"A\":3}"));
        e.SortKeys();
        Assert.Equal("{\"A\":3,\"b\":{\"a\":2,\"z\":1},\"item2\":2,\"item10\":1}", JsonWriter.Compact(e.Root));
    }

    [Fact]
    public void Move_IntoOwnChildIsRefused()
    {
        var e = Sample();
        Assert.False(e.Move(At(e, "measures"), At(e, "measures/0"), 0));
    }

    [Fact]
    public void RowLabels()
    {
        var e = Sample();
        Assert.Equal("[0] growth", Schema.RowLabel(At(e, "measures/0")));
        Assert.Equal("[1] renewal", Schema.RowLabel(At(e, "measures/1")));
    }
}
