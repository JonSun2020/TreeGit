using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public sealed class RecentCommitMessages : ObservableObject
    {
        public List<Models.RecentCommitMessage> Messages { get; private set; } = [];
        public bool IsLoading { get; private set; } = true;
        public bool HasError { get; private set; }
        public bool HasMessages => !IsLoading && !HasError && Messages.Count > 0;
        public bool IsEmpty => !IsLoading && !HasError && Messages.Count == 0;
        public void Complete(List<Models.RecentCommitMessage> messages, bool failed)
        {
            Messages = messages;
            HasError = failed;
            IsLoading = false;
            OnPropertyChanged(string.Empty);
        }
    }
}
