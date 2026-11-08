using Avalonia.Controls;

namespace SourceGit.Views
{
    internal static class MergeConflictMenu
    {
        public static bool AddChoices(ContextMenu menu, ViewModels.MergeConflictEditor model, int index, Models.ConflictPanelType panel)
        {
            if (model == null || !model.CanResolve(index)) return false;
            if (model.ConflictRegions[index].IsResolved)
                Add("RestoreConflict", "Restore", Models.ConflictResolution.None);
            else
            {
                if (panel != Models.ConflictPanelType.Theirs)
                    Add("UseLeftConflict", "UseMine", Models.ConflictResolution.UseOurs);
                if (panel != Models.ConflictPanelType.Ours)
                    Add("UseRightConflict", "UseTheirs", Models.ConflictResolution.UseTheirs);
                Add("LeftThenRightConflict", "LeftThenRight", Models.ConflictResolution.UseBothMineFirst);
                Add("RightThenLeftConflict", "RightThenLeft", Models.ConflictResolution.UseBothTheirsFirst);
            }
            return true;

            void Add(string name, string key, Models.ConflictResolution choice)
            {
                var item = new MenuItem { Name = name, Header = App.Text("MergeConflictEditor." + key) };
                item.Click += (_, e) => { model.ResolveRegion(index, choice); e.Handled = true; };
                menu.Items.Add(item);
            }
        }
    }
}
