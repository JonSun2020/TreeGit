using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SourceGit.Commands
{
    internal sealed partial class ExportCommitReport
    {
        private void WriteHeading(TextWriter html)
        {
            html.WriteLine($"""<!doctype html><html lang="{Encode(labels.Language)}"><head><meta charset="utf-8">""");
            html.WriteLine($"""<meta name="viewport" content="width=device-width, initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'sha256-{ScriptHash}'">""");
            html.WriteLine($"<title>{Encode(reportName)}</title><style>");
            html.WriteLine(Styles);
            html.WriteLine($"""</style></head><body><main><h1 class="report-title">{Encode(reportName)}</h1><div class="commit-scroll"><table id="commit-picker" aria-label="{Encode(labels.SelectCommit)}">""");
            html.WriteLine("""<colgroup><col class="ordinal"><col class="branch"><col class="description"><col class="date"><col class="author"><col class="revision"></colgroup><thead><tr>""");
            string[] headings = [labels.Ordinal, labels.Branch, labels.Description, labels.CommitDate, labels.Author, labels.CommitColumn];
            for (var i = 0; i < headings.Length; i++)
            {
                html.Write($"""<th scope="col"><span class="column-title">{Encode(headings[i])}</span>""");
                if (i < headings.Length - 1)
                    html.Write($"""<span class="column-handle" data-column="{i}" role="separator" aria-orientation="vertical" aria-label="{Encode(labels.ResizeColumn)} · {Encode(headings[i])}" tabindex="0" hidden></span>""");
                html.WriteLine("</th>");
            }
            html.WriteLine("</tr></thead><tbody>");
            for (var i = 0; i < commits.Count; i++)
            {
                var commit = commits[i];
                var subject = Encode(commit.Subject);
                var date = Encode(commit.Date);
                var author = Encode(commit.Author);
                var branch = commit.Branch;
                var badge = branch == null ? "branch-empty" : "branch-badge";
                html.WriteLine($"""<tr data-commit="commit-{commit.Sha}"><td class="commit-ordinal">{i + 1}</td><td title="{Encode(branch ?? BranchStatus(commit))}"><span class="{badge}">{Encode(branch ?? "—")}</span></td><td><a class="commit-link" href="#commit-{commit.Sha}" title="{subject}">{subject}</a></td><td title="{date}">{date}</td><td title="{author}">{author}</td><td><code title="{commit.Sha}">{commit.Sha[..10]}</code></td></tr>""");
            }
            html.WriteLine("</tbody></table></div>");
            WritePaneSplitter(html, "commits", "horizontal", labels.SelectCommit);
            html.WriteLine("""<div class="workspace">""");
        }

        private static void WriteFooter(TextWriter html)
        {
            html.Write("</div></main><script>");
            html.Write(NormalizedScript);
            html.WriteLine("</script></body></html>");
        }

        private void WriteCommitHeader(TextWriter html, Selection commit, string author, string date, string message, string[] parents)
        {
            html.WriteLine($"""<article class="commit" id="commit-{commit.Sha}"><div class="review"><aside class="inspector">""");
            html.WriteLine($"""<section class="commit-meta" tabindex="0"><dl><dt>{Encode(labels.Branch)}</dt><dd class="commit-branches">""");
            if (commit.Branch is { } branch)
                html.Write($"""<span class="branch-badge">{Encode(branch)}</span>""");
            else
                html.Write(Encode(BranchStatus(commit)));
            html.WriteLine($"""</dd><dt>{Encode(labels.Revision)}</dt><dd><code class="sha" title="SHA">{commit.Sha}</code></dd>""");
            html.WriteLine($"<dt>{Encode(labels.Parents)}</dt><dd>");
            if (parents.Length == 0)
                html.Write(Encode(labels.EmptyTree));
            else
            {
                for (var i = 0; i < parents.Length; i++)
                {
                    var parent = parents[i];
                    var identifier = $"""<code title="{(parents.Length > 1 && i == 0 ? Encode(labels.FirstParent) + " · " : "")}{parent}">{parent[..10]}</code>""";
                    html.Write(_commitIds.Contains(parent) ? $"""<a href="#commit-{parent}">{identifier}</a> """ : $"{identifier} ");
                }
            }

            html.WriteLine($"""</dd><dt>{Encode(labels.Author)}</dt><dd>{Encode(author)}</dd><dt>{Encode(labels.Date)}</dt><dd><time datetime="{Encode(date)}">{Encode(date.Replace('T', ' '))}</time></dd><dt>{Encode(labels.Context)}</dt><dd>{context}</dd></dl>""");
            html.WriteLine($"""<div class="message">{Encode(string.IsNullOrWhiteSpace(message) ? commit.Subject : message.TrimEnd('\r', '\n'))}</div></section>""");
            WritePaneSplitter(html, "metadata", "horizontal", labels.Revision);
        }

        private void WriteDiffLayout(TextWriter html, string sha, List<Change> changes)
        {
            html.WriteLine($"""<section class="files" aria-label="{Encode(labels.Changes)}">""");
            html.WriteLine($"""<div class="filter" hidden><input type="search" placeholder="{Encode(labels.Filter)}" aria-label="{Encode(labels.Filter)}" autocomplete="off" spellcheck="false"></div>""");
            html.WriteLine("<div class=\"tree\">");
            foreach (var group in Enumerable.Range(0, changes.Count).GroupBy(index => DirectoryPart(changes[index].Path)))
            {
                if (group.Key.Length > 0)
                    html.WriteLine($"""<details class="folder" open><summary><bdi>{Encode(group.Key)}</bdi></summary>""");
                html.WriteLine("<ul>");
                foreach (var index in group)
                {
                    var change = changes[index];
                    var name = change.Path[(group.Key.Length == 0 ? 0 : group.Key.Length + 1)..];
                    var path = Encode(change.PreviousPath == null ? change.Path : $"{change.PreviousPath} → {change.Path}");
                    html.WriteLine($"""<li><a href="#file-{sha}-{index}" title="{path}">{FileStatus(change)}<bdi>{Encode(name)}</bdi></a></li>""");
                }
                html.WriteLine("</ul>");
                if (group.Key.Length > 0)
                    html.WriteLine("</details>");
            }
            html.WriteLine("</div>");
            html.WriteLine($"""<footer class="files-count"><strong>{changes.Count}</strong> {Encode(labels.Files)}</footer></section></aside>""");
            WritePaneSplitter(html, "inspector", "vertical", labels.Changes);
            html.WriteLine($"""<section class="diffs" aria-label="{Encode(labels.Changes)}">""");
            if (changes.Count == 0)
                html.WriteLine($"""<p class="empty">{Encode(labels.NoChanges)}</p>""");
            else
                html.WriteLine($"""<p class="empty no-matches" hidden>{Encode(labels.NoMatches)}</p>""");
        }

        private string BranchStatus(Selection commit) => commit.BranchLookupFailed ? labels.BranchLookupFailed : labels.OriginalBranchUnknown;

        private void WritePaneSplitter(TextWriter html, string pane, string orientation, string name) =>
            html.WriteLine($"""<div class="pane-splitter" data-pane="{pane}" role="separator" aria-orientation="{orientation}" aria-label="{Encode(labels.ResizePane)} · {Encode(name)}" tabindex="0" hidden></div>""");

        private void WriteFileHeader(TextWriter html, string sha, string baseline, int fileIndex, Change change)
        {
            if (change.IsBinary)
            {
                html.WriteLine($"""<section class="diff-file binary-file" id="file-{sha}-{fileIndex}"><div class="binary-mask"><span>{Encode(labels.Binary)}</span></div>""");
                return;
            }

            var oldPath = change.PreviousPath == null ? "" : $"""<span class="renamed"><bdi>{Encode(change.PreviousPath)}</bdi> → </span>""";
            var stats = Counts(change.Additions.Value, change.Deletions.Value);
            html.WriteLine($"""<section class="diff-file" id="file-{sha}-{fileIndex}"><header class="file-toolbar"><span class="file-path">{FileStatus(change)} {stats} {oldPath}<bdi>{Encode(change.Path)}</bdi></span>""");
            html.WriteLine($"""<div class="tools" hidden><button type="button" class="previous" title="{Encode(labels.PreviousChange)}" aria-label="{Encode(labels.PreviousChange)}">↑</button><span class="position"></span><button type="button" class="next" title="{Encode(labels.NextChange)}" aria-label="{Encode(labels.NextChange)}">↓</button><button type="button" class="wrap" aria-pressed="false">{Encode(labels.Wrap)}</button></div></header>""");
            var oldVersion = baseline == null ? Encode(labels.EmptyTree) : $"""<code title="{baseline}">{baseline[..10]}</code>""";
            html.WriteLine($"""<div class="versions"><span>{Encode(labels.OldVersion)} · {oldVersion}</span><span>{Encode(labels.NewVersion)} · <code title="{sha}">{sha[..10]}</code></span></div>""");
            html.WriteLine("""<div class="patch" tabindex="0"><div class="patch-lines">""");
        }

        private static void EndFile(TextWriter html, Change change) =>
            html.WriteLine(change.IsBinary ? "</section>" : "</div></div></section>");

        private string Counts(long added, long removed) =>
            $"""<span class="stats"><span class="added" title="{Encode(labels.Added)}">+{added}</span><span class="removed" title="{Encode(labels.Removed)}">−{removed}</span></span>""";

        private static string FileStatus(Change change) =>
            $"""<span class="status kind-{Encode(change.Status[..1])}" title="{Encode(change.Status)}">{Encode(change.Status[..1])}</span>""";

        private static string DirectoryPart(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash < 0 ? "" : path[..slash];
        }

        private static void WritePatchNote(TextWriter html, string note) =>
            html.WriteLine($"""<p class="patch-note">{Encode(note)}</p>""");

        private static void WriteSourceLine(TextWriter html, PatchLine line, string counterpart = null)
        {
            var kind = line.Marker == '+' ? "add" : line.Marker == '-' ? "remove" : "context";
            var text = counterpart == null ? Encode(line.Text) : HighlightChange(line.Text, counterpart);
            html.WriteLine($"""<div class="line {kind}"><span class="number">{line.OldNumber}</span><span class="number">{line.NewNumber}</span><span class="mark" aria-hidden="true">{line.Marker}</span><code class="source">{text}</code></div>""");
        }

        private static string HighlightChange(string text, string counterpart)
        {
            var commonLength = Math.Min(text.Length, counterpart.Length);
            var prefix = 0;
            while (prefix < commonLength && text[prefix] == counterpart[prefix])
                prefix++;
            if (prefix > 0 && char.IsHighSurrogate(text[prefix - 1]))
                prefix--;

            var suffix = 0;
            while (suffix < commonLength - prefix && text[^(suffix + 1)] == counterpart[^(suffix + 1)])
                suffix++;
            if (suffix > 0 && char.IsLowSurrogate(text[^suffix]))
                suffix--;

            var end = text.Length - suffix;
            if (prefix == end || prefix + suffix < 3)
                return Encode(text);
            return $"{Encode(text[..prefix])}<mark class=\"token\">{Encode(text[prefix..end])}</mark>{Encode(text[end..])}";
        }

        private const string Styles = """
            :root {
              color-scheme: light dark;
              --shell:#eef0f2; --paper:#fff; --chrome:#f4f5f7; --ink:#202830; --muted:#52616c;
              --edge:#cdd4da; --link:#176b59; --selection:#a7e1a7; --selected-ink:#162c1b;
              --gutter:#f6f8fa; --hunk:#e5edf4; --hunk-ink:#425d72;
              --add:#e1f2e1; --add-gutter:#cfebd0; --add-token:#a9d8ac; --add-ink:#246333;
              --del:#fce1de; --del-gutter:#f7d1ce; --del-token:#eeaaa4; --del-ink:#9c332e;
              --mono:ui-monospace,"Cascadia Mono",Consolas,monospace;
            }
            @media (prefers-color-scheme:dark) {
              :root {
                --shell:#1c2227; --paper:#20272c; --chrome:#293137; --ink:#edf0f2; --muted:#b5c0c9;
                --edge:#3e4b54; --link:#8cdac6; --selection:#31743a; --selected-ink:#fff;
                --gutter:#263138; --hunk:#2b404f; --hunk-ink:#b6ccdc;
                --add:#233e2c; --add-gutter:#2b5337; --add-token:#407149; --add-ink:#a7e1ad;
                --del:#44292a; --del-gutter:#583133; --del-token:#7f4340; --del-ink:#f4b4ad;
              }
            }
            * {box-sizing:border-box}
            [hidden] {display:none!important}
            body {margin:0;background:var(--shell);color:var(--ink);font:14px/1.45 system-ui,sans-serif}
            main {min-height:100vh;padding:8px;display:grid;grid-template-rows:auto auto minmax(0,1fr)}
            .interactive main {height:100dvh;min-height:415px;grid-template-rows:auto auto 6px minmax(0,1fr)}
            .report-title {margin:0 0 5px;padding:2px 1px;font-size:16px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
            .commit-scroll {
              --header-height:27px;--row-height:25px;
              --default-commit-height:calc(var(--header-height) + 5 * var(--row-height) + 2px);
              height:min(var(--commit-height,var(--default-commit-height)),max(var(--default-commit-height),calc(100dvh - 250px)));
              min-width:0;overflow-x:hidden;overflow-y:scroll;scrollbar-gutter:stable;
              border:1px solid var(--edge);background:var(--paper)
            }
            #commit-picker {width:100%;table-layout:fixed;border-collapse:separate;border-spacing:0;font-size:13px}
            #commit-picker .ordinal {width:clamp(48px,5vw,58px)}
            #commit-picker .branch {width:clamp(64px,12vw,150px)}
            #commit-picker .date {width:clamp(112px,16vw,194px)}
            #commit-picker .author {width:clamp(78px,13vw,174px)}
            #commit-picker .revision {width:clamp(82px,9vw,116px)}
            #commit-picker th,#commit-picker td {padding:0 7px;text-align:left;white-space:nowrap}
            #commit-picker th {
              height:var(--header-height);position:sticky;top:0;z-index:1;
              background:var(--chrome);border-bottom:1px solid var(--edge);font-weight:600;text-align:center
            }
            #commit-picker th:not(:last-child) {border-right:1px solid var(--edge)}
            #commit-picker td:not(:last-child) {border-right:1px solid var(--edge)}
            .column-title {display:block;overflow:hidden;text-overflow:ellipsis;padding-inline:3px}
            .column-handle {position:absolute;top:0;bottom:0;right:-4px;width:10px;z-index:2;cursor:col-resize;touch-action:none}
            .column-handle:hover,.column-handle:focus-visible,.column-handle.dragging {background:var(--link);opacity:.55}
            #commit-picker td {height:var(--row-height);overflow:hidden;text-overflow:ellipsis}
            #commit-picker td.commit-ordinal {text-align:center;font-variant-numeric:tabular-nums}
            #commit-picker td:nth-child(4),#commit-picker td:nth-child(6) {font-variant-numeric:tabular-nums}
            #commit-picker tbody tr {cursor:pointer}
            #commit-picker tbody tr + tr td {box-shadow:inset 0 1px var(--edge)}
            #commit-picker tbody tr:last-child td {border-bottom:1px solid var(--edge)}
            #commit-picker tbody tr:hover {background:var(--chrome)}
            #commit-picker tbody tr.selected {background:var(--selection);color:var(--selected-ink)}
            .commit-link {display:block;color:inherit;overflow:hidden;text-overflow:ellipsis;text-decoration:none}
            .branch-badge,.branch-empty {color:var(--link)}
            .branch-badge {display:inline-block;max-width:100%;overflow:hidden;text-overflow:ellipsis;vertical-align:bottom;
              background:var(--add-gutter);color:var(--add-ink);border-radius:3px;padding:0 4px;font-size:12px;white-space:nowrap}
            .commit-branches .branch-badge {white-space:normal;overflow-wrap:anywhere}
            .filter input {
              color:var(--ink);background:var(--paper);border:1px solid var(--edge);border-radius:4px;
              padding:4px 7px;font:inherit;min-width:0
            }
            code {font-family:var(--mono)}
            a {color:var(--link)}
            a:focus-visible,button:focus-visible,input:focus-visible,summary:focus-visible,.patch:focus-visible,.commit-meta:focus-visible {
              outline:2px solid var(--link);outline-offset:-2px
            }
            .workspace,.commit,.review,.inspector,.files,.diffs,.diff-file {min-width:0;min-height:0}
            .commit {margin-bottom:12px}
            .interactive .commit {height:100%;margin:0}
            .review {height:100%;display:grid;grid-template-columns:clamp(215px,22vw,305px) minmax(0,1fr);gap:6px}
            .interactive .review {grid-template-columns:clamp(160px,var(--inspector-width,clamp(215px,22vw,305px)),calc(100% - 326px)) 6px minmax(0,1fr);gap:0}
            .inspector {display:grid;grid-template-rows:minmax(98px,30%) minmax(0,1fr);gap:6px}
            .interactive .inspector {grid-template-rows:clamp(72px,var(--metadata-height,max(98px,30%)),calc(100% - 112px)) 6px minmax(0,1fr);gap:0}
            .pane-splitter {position:relative;touch-action:none;outline-offset:-1px;cursor:row-resize}
            .pane-splitter::after {content:"";position:absolute;inset:2px 0 auto;height:1px;background:var(--edge)}
            .pane-splitter[aria-orientation=vertical] {cursor:col-resize}
            .pane-splitter[aria-orientation=vertical]::after {inset:0 auto 0 2px;width:1px;height:auto}
            .pane-splitter:hover,.pane-splitter.dragging,.pane-splitter:focus-visible {background:var(--hunk);outline:1px solid var(--link)}
            .commit-meta,.files,.diffs {border:1px solid var(--edge);background:var(--paper)}
            .commit-meta {padding:8px;overflow:auto;font-size:12px}
            .commit-meta dl {display:grid;grid-template-columns:max-content minmax(0,1fr);gap:2px 8px;margin:0 0 7px}
            .commit-meta dt {color:var(--muted)}
            .commit-meta dd {margin:0;overflow-wrap:anywhere}
            .commit-meta dd a {margin-right:4px}
            .sha {font-size:11px}
            .message {padding-top:6px;border-top:1px solid var(--edge);white-space:pre-wrap;overflow-wrap:anywhere}
            .files {display:grid;grid-template-rows:auto minmax(0,1fr) auto}
            .filter {padding:4px;border-bottom:1px solid var(--edge);background:var(--chrome)}
            .filter input {width:100%;font-size:13px}
            .tree {overflow:auto;padding:3px}
            .tree ul {list-style:none;margin:0;padding:0}
            .tree a,.folder summary {padding:3px 5px;overflow-wrap:anywhere;border-radius:3px}
            .tree a {display:flex;gap:6px;align-items:baseline;text-decoration:none;color:var(--ink);font-size:13px}
            .tree a:hover {background:var(--chrome)}
            .tree a[aria-current] {background:var(--selection);color:var(--selected-ink)}
            .folder summary {cursor:pointer;font-size:13px;color:var(--muted)}
            .folder ul {margin-left:9px;padding-left:4px;border-left:1px solid var(--edge)}
            .files-count {border-top:1px solid var(--edge);background:var(--chrome);padding:4px 7px;font-size:12px;color:var(--muted)}
            .status {display:inline-grid;flex:none;place-items:center;width:1.35em;height:1.35em;border-radius:2px;background:var(--chrome);color:var(--muted);font:700 11px var(--mono)}
            .status.kind-A {background:var(--add-gutter);color:var(--add-ink)}
            .status.kind-D {background:var(--del-gutter);color:var(--del-ink)}
            .status.kind-M,.status.kind-R,.status.kind-C,.status.kind-T {background:var(--hunk);color:var(--hunk-ink)}
            .diffs {overflow:hidden}
            .diff-file {display:grid;grid-template-rows:auto auto minmax(0,1fr);margin-bottom:12px}
            .interactive .diff-file {height:100%;margin:0}
            .diff-file.binary-file {grid-template-rows:minmax(180px,1fr)}
            .binary-mask {
              display:grid;place-content:center;text-align:center;padding:12px;overflow-wrap:anywhere;
              background:var(--shell);background:color-mix(in srgb,var(--shell) 80%,var(--paper));
              color:var(--muted);font-size:30px;font-weight:600
            }
            .binary-mask span {opacity:.72}
            .diff-file:target .file-toolbar {box-shadow:inset 0 2px var(--link)}
            .file-toolbar,.versions {
              display:flex;align-items:center;gap:8px;justify-content:space-between;flex-wrap:wrap;
              padding:4px 7px;border-bottom:1px solid var(--edge);background:var(--chrome)
            }
            .file-toolbar {min-height:34px}
            .file-path {min-width:0;flex:1;overflow-wrap:anywhere;font:600 12px/1.45 var(--mono)}
            .renamed {color:var(--muted);font-weight:400}
            .stats {display:inline-flex;gap:4px;white-space:nowrap;margin-right:4px}
            .stats .added {color:var(--add-ink)}
            .stats .removed {color:var(--del-ink)}
            .tools {display:flex;align-items:center;gap:4px}
            .tools button {cursor:pointer;color:var(--ink);background:var(--paper);border:1px solid var(--edge);border-radius:4px;padding:2px 6px;font:12px/1.45 system-ui,sans-serif}
            .tools button:hover,.tools button[aria-pressed=true] {background:var(--hunk)}
            .tools button:disabled {cursor:default;opacity:.53}
            .position {min-width:2.5em;text-align:center;color:var(--muted);font-size:12px;font-variant-numeric:tabular-nums}
            .versions {display:grid;grid-template-columns:repeat(2,minmax(0,1fr));font-size:12px;color:var(--muted)}
            .versions>span {min-width:0;overflow-wrap:anywhere}
            .patch {position:relative;overflow:auto;font:13px/20px var(--mono);tab-size:4;overscroll-behavior:contain}
            .patch-lines {width:max-content;min-width:100%}
            .line {display:grid;grid-template-columns:3.4em 3.4em 1.45em minmax(0,1fr)}
            .number,.mark {user-select:none;color:var(--muted);text-align:right;padding-right:6px;background:var(--gutter);border-right:1px solid var(--edge);font-variant-numeric:tabular-nums}
            .mark {background:transparent;border:0;text-align:center;padding:0;font-weight:700}
            .source {white-space:pre;padding-right:10px;min-width:0}
            .hunk,.patch-note {margin:0;padding:1px 8px;color:var(--hunk-ink);background:var(--hunk);white-space:pre-wrap;overflow-wrap:anywhere}
            .patch-note {background:var(--chrome);color:var(--muted)}
            .line.context {background:var(--paper)}
            .line.add {background:var(--add)}
            .line.remove {background:var(--del)}
            .add .number {background:var(--add-gutter)}
            .remove .number {background:var(--del-gutter)}
            .add .mark {color:var(--add-ink)}
            .remove .mark {color:var(--del-ink)}
            .token {color:inherit;border-radius:2px}
            .add .token {background:var(--add-token)}
            .remove .token {background:var(--del-token)}
            .line[aria-current] {box-shadow:inset 0 1px var(--link)}
            .wrap-lines .patch-lines {width:100%}
            .wrap-lines .source {white-space:pre-wrap;overflow-wrap:anywhere}
            .empty {margin:0;padding:18px;text-align:center;color:var(--muted)}
            @media(max-width:700px) {
              .interactive main {height:auto}
              .review,.interactive .review {grid-template-columns:minmax(0,1fr);gap:6px}
              .interactive .review>.pane-splitter {display:none}
              .inspector,.interactive .inspector {grid-template-rows:auto auto}
              .interactive .inspector {grid-template-rows:minmax(72px,var(--metadata-height,auto)) 6px auto}
              .commit-meta {max-height:var(--metadata-height,10rem)}
              .files {max-height:13rem}
              .interactive .diff-file {height:min(68vh,43rem);min-height:21rem}
              .patch {overscroll-behavior:auto}
              #commit-picker th,#commit-picker td {padding-inline:4px}
              .line {grid-template-columns:2.65em 2.65em 1.3em minmax(0,1fr)}
            }
            """;

        private const string Script = """
            const picker = document.getElementById('commit-picker');
            const commitScroll = picker.parentElement;
            const rows = [...picker.tBodies[0].rows];
            const choices = new Map(rows.map(row => [row.dataset.commit, {row, link:row.querySelector('.commit-link')}]));
            const commits = [...document.querySelectorAll('.commit')];
            const states = new WeakMap();
            const configuredFiles = new WeakSet();
            let activeCommit;
            let selectedChoice;

            function selectChoice(id) {
              const choice = choices.get(id);
              if (choice === selectedChoice) return;
              if (selectedChoice) {
                selectedChoice.row.classList.remove('selected');
                selectedChoice.link.removeAttribute('aria-current');
                selectedChoice.link.tabIndex = -1;
              }
              selectedChoice = choice;
              choice.row.classList.add('selected');
              choice.link.setAttribute('aria-current', 'true');
              choice.link.tabIndex = 0;

              const row = choice.row.getBoundingClientRect();
              const headerBottom = picker.tHead.rows[0].cells[0].getBoundingClientRect().bottom;
              const scrollBottom = commitScroll.getBoundingClientRect().top + commitScroll.clientTop + commitScroll.clientHeight;
              if (row.top < headerBottom) commitScroll.scrollTop += row.top - headerBottom;
              if (row.bottom > scrollBottom) commitScroll.scrollTop += row.bottom - scrollBottom;
            }

            function enableColumnResizing() {
              const columns = [...picker.querySelectorAll('col')];
              const headers = [...picker.tHead.rows[0].cells];
              const measure = () => headers.map(cell => cell.getBoundingClientRect().width);

              for (const handle of picker.querySelectorAll('.column-handle')) {
                const index = Number(handle.dataset.column);
                function resize(widths, delta) {
                  const total = widths.reduce((sum, width) => sum + width, 0);
                  const pair = widths[index] + widths[index + 1];
                  const leftMinimum = Math.min(index === 0 ? 34 : 68, widths[index]);
                  const rightMinimum = Math.min(68, widths[index + 1]);
                  const left = Math.max(leftMinimum, Math.min(pair - rightMinimum, widths[index] + delta));
                  columns.forEach((column, i) => {
                    const width = i === index ? left : i === index + 1 ? pair - left : widths[i];
                    column.style.width = (width * 100 / total) + '%';
                  });
                }
                bindResize(handle, true, measure, resize);
              }
            }

            function bindResize(handle, useX, measure, resize) {
              let drag;
              const coordinate = event => useX ? event.clientX : event.clientY;
              const keys = useX ? ['ArrowLeft', 'ArrowRight'] : ['ArrowUp', 'ArrowDown'];
              handle.hidden = false;
              handle.addEventListener('pointerdown', event => {
                if (event.button !== 0 || drag) return;
                drag = {origin:coordinate(event), size:measure(), pointer:event.pointerId};
                handle.setPointerCapture(event.pointerId);
                handle.classList.add('dragging');
                event.preventDefault();
              });
              handle.addEventListener('pointermove', event => {
                if (drag?.pointer === event.pointerId) resize(drag.size, coordinate(event) - drag.origin);
              });
              handle.addEventListener('lostpointercapture', () => {
                drag = null;
                handle.classList.remove('dragging');
              });
              handle.addEventListener('keydown', event => {
                const direction = keys.indexOf(event.key);
                if (direction < 0) return;
                event.preventDefault();
                resize(measure(), direction === 0 ? -10 : 10);
              });
            }

            const main = document.querySelector('main');
            const workspace = document.querySelector('.workspace');
            function enablePaneResize(handle, useX, pane, property, min, limit) {
              const size = () => useX ? pane.getBoundingClientRect().width : pane.getBoundingClientRect().height;
              bindResize(handle, useX, () => ({ value:size(), max:limit() }), (start, delta) => {
                const adjusted = Math.max(min, Math.min(start.max, start.value + delta));
                main.style.setProperty(property, adjusted + 'px');
              });
            }

            function enableCommitPaneResizing(commit) {
              const review = commit.querySelector('.review');
              const inspector = review.querySelector('.inspector');
              const metadata = inspector.querySelector('.commit-meta');
              enablePaneResize(review.querySelector('[data-pane=inspector]'), true, inspector, '--inspector-width', 160,
                () => review.clientWidth - 326);
              enablePaneResize(inspector.querySelector('[data-pane=metadata]'), false, metadata, '--metadata-height', 72,
                () => matchMedia('(max-width:700px)').matches ? Math.max(160, main.clientHeight / 2) : inspector.clientHeight - 112);
            }

            function fileState(file) {
              if (configuredFiles.has(file)) return;
              configuredFiles.add(file);
              const patch = file.querySelector('.patch');
              if (!patch) return;
              const controls = file.querySelector('.tools');
              const previous = controls.querySelector('.previous');
              const next = controls.querySelector('.next');
              const position = controls.querySelector('.position');
              const wrap = controls.querySelector('.wrap');
              const changeSelector = '.line.add, .line.remove';
              const changes = [...patch.querySelectorAll(changeSelector)]
                .filter(row => !row.previousElementSibling?.matches(changeSelector));
              let index = 0;
              function showPosition() {
                position.textContent = changes.length ? (index + 1) + '/' + changes.length : '';
                previous.disabled = index === 0;
                next.disabled = index + 1 >= changes.length;
              }
              function go(delta) {
                const target = index + delta;
                if (target < 0 || target >= changes.length) return;
                changes[index].removeAttribute('aria-current');
                index = target;
                const row = changes[index];
                row.setAttribute('aria-current', 'true');
                patch.scrollTop = row.offsetTop - 8;
                showPosition();
              }
              previous.addEventListener('click', () => go(-1));
              next.addEventListener('click', () => go(1));
              wrap.addEventListener('click', () => {
                wrap.setAttribute('aria-pressed', file.classList.toggle('wrap-lines'));
              });
              controls.hidden = false;
              showPosition();
            }

            function showFile(state, entry) {
              if (state.selected && state.selected !== entry) {
                state.selected.file.hidden = true;
                state.selected.link.removeAttribute('aria-current');
              }
              state.selected = entry;
              if (entry) {
                entry.file.hidden = false;
                entry.link.setAttribute('aria-current', 'true');
                fileState(entry.file);
              }
              if (state.noMatches) state.noMatches.hidden = !!entry;
            }

            function applyFilter(state) {
              const query = state.input.value.trim().toLocaleLowerCase();
              for (const {line, path} of state.entries)
                line.hidden = !path.includes(query);
              for (const folder of state.folders) {
                folder.hidden = !folder.querySelector('li:not([hidden])');
                if (query && !folder.hidden) folder.open = true;
              }
              if (!state.selected || state.selected.line.hidden)
                showFile(state, state.entries.find(entry => !entry.line.hidden));
            }

            function createState(commit) {
              const filter = commit.querySelector('.filter');
              const input = filter.querySelector('input');
              const entries = [...commit.querySelectorAll('.tree a')].map(link => ({
                link, line:link.parentElement, path:link.title.toLocaleLowerCase(), file:document.getElementById(link.hash.slice(1))
              }));
              const state = { entries, input, folders:[...commit.querySelectorAll('.folder')], noMatches:commit.querySelector('.no-matches') };
              states.set(commit, state);
              enableCommitPaneResizing(commit);
              for (const entry of entries) entry.file.hidden = true;
              filter.hidden = entries.length === 0;
              input.addEventListener('input', () => applyFilter(state));
              return state;
            }

            function reveal(id) {
              const target = document.getElementById(id);
              const commit = target?.closest('.commit') ?? commits[0];
              if (!commit) return;
              if (activeCommit && activeCommit !== commit) activeCommit.hidden = true;
              activeCommit = commit;
              commit.hidden = false;
              selectChoice(commit.id);
              const state = states.get(commit) ?? createState(commit);
              const entry = target?.matches('.diff-file') ? state.entries.find(item => item.file === target) : null;
              if (entry?.line.hidden) {
                state.input.value = '';
                applyFilter(state);
              }
              if (entry) {
                const folder = entry.link.closest('.folder');
                if (folder) folder.open = true;
              }
              showFile(state, entry || state.selected || state.entries.find(item => !item.line.hidden));
            }

            function navigate(id) {
              if (location.hash !== '#' + id) history.pushState(null, '', '#' + id);
              reveal(id);
            }

            enableColumnResizing();
            enablePaneResize(main.querySelector('[data-pane=commits]'), false, commitScroll, '--commit-height', 54,
              () => commitScroll.offsetHeight + workspace.clientHeight - 210);
            for (const {link} of choices.values()) link.tabIndex = -1;
            picker.addEventListener('click', event => {
              if (event.button !== 0 || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
              const row = event.target.closest('tr[data-commit]');
              if (!row) return;
              event.preventDefault();
              navigate(row.dataset.commit);
              choices.get(row.dataset.commit).link.focus({preventScroll:true});
            });
            picker.addEventListener('keydown', event => {
              const row = event.target.closest('tr[data-commit]');
              if (!row) return;
              let index = row.sectionRowIndex;
              if (event.key === 'ArrowUp') index--;
              else if (event.key === 'ArrowDown') index++;
              else if (event.key === 'Home') index = 0;
              else if (event.key === 'End') index = rows.length - 1;
              else return;
              event.preventDefault();
              const next = rows[Math.max(0, Math.min(index, rows.length - 1))];
              navigate(next.dataset.commit);
              choices.get(next.dataset.commit).link.focus({preventScroll:true});
            });
            workspace.addEventListener('click', event => {
              if (event.button !== 0 || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
              const link = event.target.closest('.tree a, .commit-meta a');
              if (!link) return;
              event.preventDefault();
              navigate(link.hash.slice(1));
            });
            for (const commit of commits) commit.hidden = true;
            document.documentElement.classList.add('interactive');
            addEventListener('hashchange', () => reveal(location.hash.slice(1)));
            addEventListener('popstate', () => reveal(location.hash.slice(1)));
            reveal(location.hash.slice(1));
            """;

        private static readonly string NormalizedScript = Script.ReplaceLineEndings("\n");
        private static readonly string ScriptHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizedScript)));
    }
}
