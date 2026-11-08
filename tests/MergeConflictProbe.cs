using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SourceGit.Models;

var checks = new List<string>();
var failure = (string)null;
var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
void Check(string name, bool ok)
{
    if (!ok) throw new Exception(name);
    checks.Add(name); Console.WriteLine("PASS " + name);
}
string Block(string ours, string theirs, string eol = "\n", int size = 7, bool diff3 = false)
{
    return new string('<', size) + " HEAD" + eol + ours + eol +
        (diff3 ? new string('|', size) + " base" + eol + "base 1" + eol + "base 2" + eol : "") +
        new string('=', size) + eol + theirs + eol + new string('>', size) + " other" + eol;
}
try
{
    // Git emits LF/CRLF conflict marker lines; a bare CR is file content.
    foreach (var eol in new[] { "\n", "\r\n" })
    {
        var source = "prefix" + eol + Block("local 1", "remote 1", eol) + "middle" + eol + Block("local 2", "remote 2", eol) + "tail" + eol;
        var model = new MergeConflictDocument(source);
        Check(eol.Length + " newline: two actual conflict blocks", model.UnsolvedCount == 2 && model.Regions.Count == 2 && model.UnresolvedOffsets().Count == 2);
        Check("comparison rows align with original lines", model.OursLines.Count == model.SourceLineCount && model.TheirsLines.Count == model.SourceLineCount);
        model.Document.Replace(0, 6, "custom prefix");
        Check("first side action works after editing common content", model.Resolve(0, ConflictResolution.UseOurs));
        var middle = model.Document.Text.IndexOf("middle", StringComparison.Ordinal);
        model.Document.Insert(middle, "manual common" + eol);
        Check("second side action works after an earlier manual edit", model.Resolve(1, ConflictResolution.UseTheirs));
        var expected = "custom prefix" + eol + "local 1" + eol + "manual common" + eol + "middle" + eol + "remote 2" + eol + "tail" + eol;
        Check("only selected blocks change; no comparison padding is saved", model.Document.Text == expected && model.UnsolvedCount == 0);
        model.Document.UndoStack.Undo();
        Check("undo restores only the last block", model.UnsolvedCount == 1 && model.Document.Text.StartsWith("custom prefix" + eol + "local 1" + eol + "manual common", StringComparison.Ordinal));
        model.Document.UndoStack.Redo();
        Check("redo restores the exact mixed manual result", model.Document.Text == expected && model.UnsolvedCount == 0);
    }

    foreach (var choice in new[] { ConflictResolution.UseOurs, ConflictResolution.UseTheirs, ConflictResolution.UseBothMineFirst, ConflictResolution.UseBothTheirsFirst })
    {
        var model = new MergeConflictDocument(Block("mine", "theirs"));
        model.Resolve(0, choice);
        var expected = choice switch
        {
            ConflictResolution.UseOurs => "mine\n", ConflictResolution.UseTheirs => "theirs\n",
            ConflictResolution.UseBothMineFirst => "mine\ntheirs\n", _ => "theirs\nmine\n",
        };
        Check("side choice produces exact content " + choice, model.Document.Text == expected && model.UnsolvedCount == 0);
        model.Document.UndoStack.Undo();
        Check("undo restores markers and source mapping " + choice, model.UnsolvedCount == 1 && model.CanResolve(0));
        model.Document.UndoStack.Redo();
        Check("redo preserves mapping " + choice, model.Document.Text == expected && model.CanResolve(0));
    }

    {
        var source = "head\n" + Block("one", "two") + "between\n" + Block("three", "four") + "tail\n";
        var model = new MergeConflictDocument(source);
        model.Resolve(0, ConflictResolution.UseOurs);
        model.Document.Insert(0, "manual\n");
        model.Resolve(1, ConflictResolution.UseTheirs);
        Check("the original block list remains fixed after all conflicts are resolved", model.Regions.Count == 2 && model.UnsolvedCount == 0);
        Check("resolved blocks retain distinct result locations", model.RegionForOffset(model.GetResultOffset(0)) == 0 && model.RegionForOffset(model.GetResultOffset(1)) == 1);
        var line = model.Document.GetLineByOffset(model.GetResultOffset(1)).LineNumber;
        Check("active result highlighting stops at the last block line", model.ContainsResultLine(1, line) && !model.ContainsResultLine(1, line + 1) && !model.ContainsResultLine(-1, line));
        Check("restore reinstates just one conflict and preserves manual edits and the other choice", model.Resolve(0, ConflictResolution.None) &&
            model.Document.Text == "manual\nhead\n" + Block("one", "two") + "between\nfour\ntail\n" && model.UnsolvedCount == 1 && model.Regions.Count == 2);
        model.Document.UndoStack.Undo();
        Check("restore itself can be undone", model.UnsolvedCount == 0 && model.Regions.Count == 2);
    }

    {
        var source = "head\n" + Block("one", "two") + "between\n" + Block("three", "four") + "tail\n";
        var model = new MergeConflictDocument(source);
        model.Document.Text = "manual replacement without markers\n";
        Check("whole-result editing resolves markers", model.UnsolvedCount == 0 && model.HasChanges);
        Check("old block buttons cannot overwrite a whole-file manual edit", !model.Resolve(0, ConflictResolution.UseOurs) && model.Document.Text == "manual replacement without markers\n");
        for (var i = 0; i < 12; i++)
        {
            model.Document.UndoStack.Undo();
            Check("whole-file undo restores both independent block mappings " + i, model.Document.Text == source && model.CanResolve(0) && model.CanResolve(1));
            model.Document.UndoStack.Redo();
            Check("whole-file redo does not revive destructive block actions " + i, model.Document.Text == "manual replacement without markers\n" && !model.CanResolve(0));
        }
        model.Document.UndoStack.Undo();
        model.Resolve(1, ConflictResolution.UseTheirs);
        Check("choosing a block after undo cannot remove other blocks", model.Document.Text == "head\n" + Block("one", "two") + "between\nfour\ntail\n");
    }

    {
        var model = new MergeConflictDocument("head\n" + Block("mine", "theirs") + "tail\n");
        model.Document.Insert(model.GetResultOffset(0), "before block\n");
        model.Document.Insert(model.Document.Text.IndexOf("tail", StringComparison.Ordinal), "after block\n");
        model.Resolve(0, ConflictResolution.UseOurs);
        Check("inserting at both block boundaries is preserved", model.Document.Text == "head\nbefore block\nmine\nafter block\ntail\n");
        model.Document.UndoStack.Undo();
        var start = model.GetResultOffset(0);
        model.Document.Remove(start, 8);
        Check("structural manual editing disables only the unsafe side replacement", !model.CanResolve(0));
        model.Document.UndoStack.Undo();
        Check("undo recovers the block action after marker editing", model.CanResolve(0) && model.UnsolvedCount == 1);
    }

    foreach (var size in new[] { 7, 9, 32 })
    {
        var body = size == 7 ? "mine" : "mine\n=======\nheading";
        var source = "title\n=======\n" + Block(body, "theirs", "\n", size, true) + "end\n";
        var model = new MergeConflictDocument(source);
        Check("diff3 and custom marker sizes parse " + size, model.Regions.Count == 1 && model.UnsolvedCount == 1 && model.OursLines.Count == model.SourceLineCount);
        model.Resolve(0, ConflictResolution.UseOurs);
        Check("ancestor markers and common Markdown separators are handled " + size, model.Document.Text == "title\n=======\n" + body + "\nend\n" && model.UnsolvedCount == 0);
    }

    {
        var longLine = new string('x', 12000) + "完整中文尾部";
        var model = new MergeConflictDocument("before\n" + Block(longLine, "remote") + "after");
        model.Resolve(0, ConflictResolution.UseOurs);
        Check("long lines are never truncated in the saved result", model.Document.Text == "before\n" + longLine + "\nafter");
        model.Document.Text = "";
        Check("an empty manual result is a valid resolution", model.UnsolvedCount == 0);
        model.Document.UndoStack.Undo();
        Check("undoing an empty result restores long content", model.Document.Text.Contains(longLine, StringComparison.Ordinal));
        model = new MergeConflictDocument(Block("mine", "theirs").TrimEnd('\n'));
        model.Resolve(0, ConflictResolution.UseBothMineFirst);
        Check("a result without a final newline stays that way", model.Document.Text == "mine\ntheirs");
        model = new MergeConflictDocument("<<<<<<< HEAD\npartial");
        Check("malformed markers remain editable and unresolved", model.Regions.Count == 0 && model.UnsolvedCount > 0);
        model.Document.Text = "manual\n";
        Check("manual editing can repair malformed markers", model.UnsolvedCount == 0);
    }

    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true), new UTF32Encoding(false, true, true), Encoding.GetEncoding(936), Encoding.GetEncoding(54936) })
    {
        var text = "中文\r\n" + Block("本地", "远程", "\r\n") + "最后一行";
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        var file = MergeConflictTextFile.Read(bytes);
        Check("encoding and BOM round-trip " + encoding.WebName + " " + encoding.GetPreamble().Length, file.Text == text && file.Encode(file.Text).SequenceEqual(bytes));
        var model = new MergeConflictDocument(file.Text);model.Resolve(0, ConflictResolution.UseOurs);
        var expected = encoding.GetPreamble().Concat(encoding.GetBytes("中文\r\n本地\r\n最后一行")).ToArray();
        Check("editing preserves original bytes outside the block " + encoding.WebName, file.Encode(model.Document.Text).SequenceEqual(expected));
    }
    var legacy = MergeConflictTextFile.Read(Encoding.GetEncoding(936).GetBytes("中文"));
    var failed = false;
    try { legacy.Encode("中文😀"); } catch (EncoderFallbackException) { failed = true; }
    Check("unencodable text is rejected without replacement characters", failed);
    Check("explicit UTF-8 conversion keeps edited characters", Encoding.UTF8.GetString(legacy.ConvertTo(65001).Encode("中文😀")) == "中文😀");
    failed = false;
    try { MergeConflictTextFile.Read([0,1,2,3]); } catch (FormatException) { failed = true; }
    Check("binary data is not treated as editable text", failed);
}
catch (Exception ex) { failure = ex.ToString(); throw; }
finally
{
    File.WriteAllText(Path.Combine(output, "merge-model-results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks, failure }, new JsonSerializerOptions { WriteIndented = true }));
}
