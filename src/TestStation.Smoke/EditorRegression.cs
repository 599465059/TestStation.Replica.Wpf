using TestStation.App.Services;
using TestStation.Application;
using TestStation.Domain;

namespace TestStation.Smoke;

internal static class EditorRegression
{
    public static void Run()
    {
        var sequence = new SequenceDefinition();
        var editing = new SequenceEditingService();
        var display = new AutoTestDisplayService();
        var targets = new SequenceDropTargetService();
        var conditional = editing.Insert(sequence, "IF", new("Main", null, false, 0));
        var rows = display.BuildRows(sequence, new HashSet<string>(), new Dictionary<string, string>());
        var elseRow = rows.Single(row => row.Branch == "else" && row.RowType == "branch");
        var conditionalRow = rows.Single(row => row.Step == conditional);
        Require(conditionalRow.Depth == elseRow.Depth, "IF and ELSE must be siblings visually");
        var emptyThen = new SequenceInsertTarget("Main", conditional.Id, false, 0);
        Require(targets.CanonicalAnchor(rows, conditionalRow.AfterTarget!) == targets.CanonicalAnchor(rows, emptyThen),
            "Identical empty-branch insertion slots have multiple visual anchors");
        var thenLoop = editing.Insert(sequence, "FOR", targets.Resolve(elseRow, 0.1, false).Target);
        Require(conditional.Children.Single() == thenLoop, "Dropping above ELSE must insert in THEN");
        var elseLoop = editing.Insert(sequence, "FOR", targets.Resolve(elseRow, 0.9, false).Target);
        var nested = editing.Insert(sequence, "IF", new("Main", elseLoop.Id, false, 0));
        editing.Insert(sequence, "WAIT", new("Main", nested.Id, false, 0));
        Require(conditional.ElseChildren.Single() == elseLoop, "Dropping below ELSE must insert in ELSE");
        Require(!editing.CanMove(sequence, conditional.Id, new("Main", nested.Id, false, 0)), "Descendant drop must be rejected");
        Require(!editing.CanMove(sequence, conditional.Id, new("Main", conditional.Id, false, 0)), "Self drop must be rejected");

        foreach (var section in new[] { "Setup", "Main", "Cleanup" })
        {
            rows = display.BuildRows(sequence, new HashSet<string>(), new Dictionary<string, string>());
            var footer = rows.Single(row => row.Section == section && row.RowType == "footer");
            editing.Insert(sequence, "COMMENT", targets.Resolve(footer, 0.5, false).Target);
            var collapsed = display.BuildRows(sequence, new HashSet<string> { section }, new Dictionary<string, string>());
            Require(collapsed.Count(row => row.Section == section) == 1, "Collapsed group leaked rows");
        }
        editing.Copy(sequence, [conditional.Id, nested.Id]);
        var pasted = editing.Paste(sequence, new("Setup", null, false, 0));
        Require(pasted.Count == 1, "Parent plus descendant selection duplicated the descendant");
        Require(pasted[0].ElseChildren[0].Children[0].Children.Count == 1, "Clipboard lost nested subtree");
        Require(pasted[0].Id != conditional.Id && pasted[0].ElseChildren[0].Id != elseLoop.Id, "Copied subtree reused IDs");
        var repeated = editing.Paste(sequence, new("Cleanup", null, false, 0));
        Require(repeated[0].Id != pasted[0].Id, "Repeated paste reused IDs");
        editing.Cut(sequence, [thenLoop.Id]);
        Require(conditional.Children.Count == 0, "Cut did not remove source");
        editing.Paste(sequence, new("Main", conditional.Id, true, 0));
        Require(conditional.ElseChildren[0].Kind == "loop", "Cross-branch paste failed");
        Require(editing.Move(sequence, conditional.Id, new("Cleanup", null, false, 1)), "Container move failed");
        Require(sequence.CleanupSteps.Contains(conditional) && conditional.ElseChildren.Count == 2, "Container move damaged subtree");
        editing.Remove(sequence, [conditional.Id]);
        Require(!sequence.CleanupSteps.Contains(conditional), "Container deletion failed");
        Console.WriteLine("PASS: drop targets, IF/ELSE nesting, groups, subtree clipboard, cross-section moves");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
