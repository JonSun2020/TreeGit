using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.Models
{
    public abstract class LocalizedOption : ObservableObject
    {
        public void RefreshLocalizedText() => OnPropertyChanged(string.Empty);

        internal static void RefreshAll()
        {
            foreach (var option in ResetMode.Supported) option.RefreshLocalizedText();
            foreach (var option in MergeMode.Supported) option.RefreshLocalizedText();
            foreach (var option in MergeStrategy.ForMultiple) option.RefreshLocalizedText();
            foreach (var option in CRLFMode.Supported) option.RefreshLocalizedText();
            foreach (var option in ApplyWhiteSpaceMode.Supported) option.RefreshLocalizedText();
            foreach (var option in DealWithChangesAfterStashing.Supported) option.RefreshLocalizedText();
            foreach (var option in ExternalMerger.Supported) option.RefreshLocalizedText();
        }
    }
}
