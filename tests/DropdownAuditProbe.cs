using System;
using System.Linq;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;

namespace SourceGit.Automation
{
    internal static class DropdownAuditProbe
    {
        private static readonly Pointer Pointer = new(18, PointerType.Mouse, true);

        public static object Execute(Window window, JsonElement args)
        {
            if (Environment.GetEnvironmentVariable("CI") != "true" ||
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" &&
                (!OperatingSystem.IsWindows() || !LocalAuditHost.IsEnabled))
                throw new InvalidOperationException("Dropdown checks require an owned test application.");

            var pickers = window.GetVisualDescendants().OfType<Control>()
                .Where(control => control.IsEffectivelyVisible && control is ComboBox or Views.BranchSelector).ToArray();
            var action = args.GetProperty("text").GetString();
            if (action.StartsWith("fixture_", StringComparison.Ordinal))
                return OpenFixture(window, action);
            if (action == "list")
                return new { pickers = pickers.Select((picker, index) => new
                {
                    index,
                    picker.Name,
                    view = picker.GetVisualAncestors().OfType<UserControl>().FirstOrDefault()?.GetType().Name ?? window.GetType().Name,
                    type = picker.GetType().Name,
                    enabled = picker.IsEffectivelyEnabled,
                    count = picker is ComboBox combo ? combo.ItemCount : ((Views.BranchSelector)picker).VisibleBranches?.Count ?? 0,
                    customCaption = (picker as ComboBox)?.SelectionBoxItemTemplate != null,
                }).ToArray() };

            var control = pickers[int.Parse(args.GetProperty("button").GetString(), System.Globalization.CultureInfo.InvariantCulture)];
            var option = args.TryGetProperty("value", out var value) ? value.GetInt32() : 0;
            return control is ComboBox selector ? InspectCombo(selector, action, option) : InspectBranch((Views.BranchSelector)control, action, option);
        }

        private static object OpenFixture(Window window, string action)
        {
            var page = ((ViewModels.Launcher)window.DataContext).ActivePage;
            var repo = page.Data as ViewModels.Repository ?? throw new InvalidOperationException("Open the disposable test repository first.");
            switch (action)
            {
                case "fixture_conventional":
                    _ = new Views.ConventionalCommitMessageBuilder { DataContext = new ViewModels.ConventionalCommitMessageBuilder(null, _ => { }) }.ShowDialog(window);
                    break;
                case "fixture_revision_encoding":
                    var content = new Models.RevisionTextFile(repo.FullPath, "calibration.txt", new ArraySegment<byte>("isolated revision\n"u8.ToArray()));
                    _ = new Window { Width = 720, Height = 480, Content = new Views.RevisionFileContentViewer { Content = content } }.ShowDialog(window);
                    break;
                case "fixture_statistics":
                    _ = new Views.Statistics { DataContext = new ViewModels.Statistics(repo.FullPath) }.ShowDialog(window);
                    break;
                case "fixture_ssh":
                    var ssh = new ViewModels.SSHKeyHelper(System.IO.Path.Combine(repo.GitDir, "dropdown-test-keys"));
                    ssh.OpenGenerator();
                    _ = new Views.SSHKeyHelper { DataContext = ssh }.ShowDialog(window);
                    break;
                case "fixture_ai":
                    var service = new AI.Service { Server = "intentionally invalid endpoint", CachedApiKey = "fixture", Model = "demo-a",
                        CachedAvailableModels = ["demo-a", "demo-b", "demo-c"] };
                    _ = new Views.AIAssistant { DataContext = new ViewModels.AIAssistant(repo, service, []) }.ShowDialog(window);
                    break;
                case "fixture_merge_editor":
                    var editor = new ViewModels.MergeConflictEditor(repo, repo.Histories.Commits.First(commit => commit.IsCurrentHead), "sensor.txt");
                    _ = new Views.MergeConflictEditor { DataContext = editor }.ShowDialog(window);
                    break;
                case "fixture_apply_whitespace":
                    ((ViewModels.Apply)page.Popup).IgnoreWhiteSpace = false;
                    break;
                default:
                    if (!repo.CanCreatePopup()) throw new InvalidOperationException("Dismiss the previous fixture first.");
                    var locals = repo.Branches.Where(branch => branch.IsLocal && !branch.IsDetachedHead).ToList();
                    var remotes = repo.Branches.Where(branch => !branch.IsLocal).ToList();
                    var popup = action switch
                    {
                        "fixture_lfs_fetch" => (ViewModels.Popup)new ViewModels.LFSFetch(repo),
                        "fixture_lfs_pull" => new ViewModels.LFSPull(repo),
                        "fixture_lfs_push" => new ViewModels.LFSPush(repo),
                        "fixture_push_tag" => new ViewModels.PushTag(repo, repo.Tags.First()),
                        "fixture_upstream" => new ViewModels.SetUpstream(repo, repo.CurrentBranch, remotes),
                        "fixture_git_flow" => new ViewModels.GitFlowStart(repo, Models.GitFlowBranchType.Feature),
                        "fixture_merge_multiple" => new ViewModels.MergeMultiple(repo, locals.Where(branch => branch != repo.CurrentBranch).ToList()),
                        "fixture_ignore" => new ViewModels.AddToIgnore(repo, "demo.ignore"),
                        "fixture_submodule" => new ViewModels.UpdateSubmodules(repo, null) { UpdateAll = false },
                        "fixture_cherry_pick" => new ViewModels.CherryPick(repo, repo.Histories.Commits[0], repo.Histories.Commits.Take(2).ToList()),
                        "fixture_custom_action" => new ViewModels.ExecuteCustomAction(repo, new Models.CustomAction
                        {
                            Name = "Dropdown demo",
                            Controls = [new() { Type = Models.CustomActionControlType.ComboBox, Label = "Demo", StringValue = "One|Two|Three" },
                                new() { Type = Models.CustomActionControlType.LocalBranchSelector, Label = "Local branch" },
                                new() { Type = Models.CustomActionControlType.RemoteBranchSelector, Label = "Remote branch" }],
                        }, null),
                        _ => throw new ArgumentException("Unknown dropdown fixture."),
                    };
                    repo.ShowPopup(popup);
                    break;
            }

            return new { opened = action };
        }

        private static object InspectCombo(ComboBox combo, string action, int option)
        {
            var popup = combo.GetVisualDescendants().OfType<Popup>().FirstOrDefault(item => item.Name == "PART_Popup");
            string[] expected = [];
            var ready = true;
            switch (action)
            {
                case "open": combo.IsDropDownOpen = true; break;
                case "close": combo.IsDropDownOpen = false; break;
                case "key":
                    var target = combo.IsDropDownOpen && combo.ContainerFromIndex(combo.SelectedIndex) is Control selected ? selected : combo;
                    target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = (Key)option });
                    break;
                case "click":
                    if (!combo.IsDropDownOpen) throw new InvalidOperationException("Open the dropdown before choosing an item.");
                    if (combo.ContainerFromIndex(option) is ComboBoxItem item && item.IsEffectivelyVisible && item.Bounds.Width > 0)
                    {
                        expected = Labels(item);
                        ready = expected.Length > 0 && Click(item, true);
                    }
                    else
                    {
                        combo.ScrollIntoView(option);
                        ready = false;
                    }
                    break;
                case "inspect": break;
                default: throw new ArgumentException("Unknown dropdown operation.");
            }

            var closed = combo.GetVisualDescendants().OfType<ContentControl>().FirstOrDefault(content => content.Name == "ContentPresenter");
            return new
            {
                ready,
                open = combo.IsDropDownOpen,
                selected = combo.SelectedIndex,
                caption = Labels(closed),
                expected,
                count = combo.ItemCount,
                width = combo.Bounds.Width,
                modelSelection = (combo.DataContext as ViewModels.SearchCommitContext)?.Method,
                options = combo.IsDropDownOpen && popup?.Child != null
                    ? Enumerable.Range(0, combo.ItemCount).Select(index => Labels(combo.ContainerFromIndex(index))).ToArray() : [],
                keyboard = new { f4 = (int)Key.F4, down = (int)Key.Down, enter = (int)Key.Enter, escape = (int)Key.Escape },
            };
        }

        private static object InspectBranch(Views.BranchSelector branch, string action, int option)
        {
            var popup = branch.GetVisualDescendants().OfType<Popup>().FirstOrDefault(item => item.Name == "PART_Popup");
            var list = popup?.Child?.GetVisualDescendants().OfType<ListBox>().FirstOrDefault();
            var ready = true;
            string[] expected = [];
            switch (action)
            {
                case "open": branch.IsDropDownOpened = true; break;
                case "close": branch.IsDropDownOpened = false; break;
                case "click":
                    if (!branch.IsDropDownOpened) throw new InvalidOperationException("Open the branch dropdown before choosing an item.");
                    if (list?.ContainerFromIndex(option)?.GetVisualDescendants().OfType<Views.BranchSelectorChoice>().FirstOrDefault() is { } choice &&
                        choice.GetVisualParent() is Control row && choice.IsEffectivelyVisible && row.Bounds.Width > 0)
                    {
                        expected = new[] { choice.Text };
                        ready = Click(row, false);
                    }
                    else
                    {
                        list?.ScrollIntoView(option);
                        ready = false;
                    }
                    break;
                case "inspect": break;
                default: throw new ArgumentException("Unknown branch dropdown operation.");
            }

            var closed = branch.GetVisualDescendants().OfType<Views.BranchSelectorChoice>()
                .FirstOrDefault(choice => choice.FindAncestorOfType<ListBoxItem>() == null);
            return new
            {
                ready,
                open = branch.IsDropDownOpened,
                selected = branch.VisibleBranches?.IndexOf(branch.SelectedBranch) ?? -1,
                caption = closed?.Text is { Length: > 0 } text ? new[] { text } : [],
                expected,
                count = branch.VisibleBranches?.Count ?? 0,
                width = branch.Bounds.Width,
            };
        }

        private static string[] Labels(Visual root) => root == null ? [] : root.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(text.Text) && text.Opacity > 0 &&
                text.GetVisualAncestors().TakeWhile(ancestor => ancestor != root).All(ancestor => ancestor.Opacity > 0))
            .Select(text => text.Text).ToArray();

        private static bool Click(Control target, bool release)
        {
            var top = TopLevel.GetTopLevel(target);
            if (top == null || target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), top) is not { } center)
                return false;

            var timestamp = (ulong)Environment.TickCount64;
            try
            {
                target.RaiseEvent(new PointerPressedEventArgs(target, Pointer, top, center, timestamp,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
                if (release)
                    target.RaiseEvent(new PointerReleasedEventArgs(target, Pointer, top, center, timestamp,
                        new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                return true;
            }
            finally
            {
                Pointer.Capture(null);
            }
        }
    }
}
