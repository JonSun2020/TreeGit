using Avalonia.Controls;

namespace SourceGit.Views
{
    internal static class MergeConflictMenu
    {
        public static bool AddChoices(ContextMenu menu, ViewModels.MergeConflictEditor model, int index, Models.ConflictPanelType panel)
        {
            if (model == null || !model.CanResolve(index)) return false;
            Add("UseLeftConflict", "UseMine", Models.ConflictResolution.UseOurs);
            Add("UseRightConflict", "UseTheirs", Models.ConflictResolution.UseTheirs);
            Add("LeftThenRightConflict", "LeftThenRight", Models.ConflictResolution.UseBothMineFirst);
            Add("RightThenLeftConflict", "RightThenLeft", Models.ConflictResolution.UseBothTheirsFirst);
            if (model.ConflictRegions[index].IsResolved)
            {
                menu.Items.Add(new Separator());
                Add("RestoreConflict", "Restore", Models.ConflictResolution.None);
            }
            return true;

            void Add(string name, string key, Models.ConflictResolution choice)
            {
                var item = new MenuItem { Name = name, Header = App.Text("MergeConflictEditor." + key) };
                item.Click += (_, e) => { model.ResolveDisplayedRegion(index, choice); e.Handled = true; };
                menu.Items.Add(item);
            }
        }
    }
}
