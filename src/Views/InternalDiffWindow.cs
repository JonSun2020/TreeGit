using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;

namespace SourceGit.Views
{
    public sealed class InternalDiffWindow : OwnedNativeWindow
    {
        public InternalDiffWindow(ViewModels.DiffContext context, Window owner, ViewModels.LauncherPage repositoryPage = null)
        {
            _repositoryPage = repositoryPage;
            _repository = repositoryPage?.LoadedRepository;
            _isUnstaged = context.Option.IsUnstaged;
            _path = context.Option.Path;
            Title = context.Title + " - TreeGit";
            Width = 1200;
            Height = 760;
            MinWidth = 640;
            MinHeight = 360;
            SystemDecorations = SystemDecorations.Full;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
            if (screen != null)
            {
                MaxWidth = Math.Max(1, screen.WorkingArea.Width / screen.Scaling - 32);
                MaxHeight = Math.Max(1, screen.WorkingArea.Height / screen.Scaling - 64);
                MinWidth = Math.Min(MinWidth, MaxWidth);
                MinHeight = Math.Min(MinHeight, MaxHeight);
                Width = Math.Min(Width, MaxWidth);
                Height = Math.Min(Height, MaxHeight);
            }
            _diff = new DiffView { DataContext = context };
            Content = _diff;
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };

            if (_repository != null)
            {
                _repository.WorkingCopyRefreshed += OnWorkingCopyRefreshed;
                _repositoryPage.PropertyChanged += OnRepositoryPageChanged;
            }
            Closed += OnClosed;
        }

        internal ViewModels.Repository ChunkRepository =>
            !_waitingForRefresh && _repository?.WorkingCopyStatusKnown == true &&
            ((ViewModels.DiffContext)_diff.DataContext).LoadCompletion.IsCompleted ? _repository : null;

        internal void WaitForWorkingCopyRefresh() => _waitingForRefresh = true;

        private void OnWorkingCopyRefreshed()
        {
            var current = (ViewModels.DiffContext)_diff.DataContext;
            var workingCopy = _repository.WorkingCopy;
            var changes = _isUnstaged ? workingCopy.Unstaged : workingCopy.Staged;
            var change = changes.Find(entry => entry.Path == _path);
            var conflicted = workingCopy.Unstaged.Exists(entry => entry.Path == _path && entry.IsConflicted);
            var option = new Models.DiffOption(change ?? new Models.Change { Path = _path }, _isUnstaged);
            if (conflicted) option = option.AsDisplayOnly();

            if (current.Option.IsLocalChange == option.IsLocalChange && current.Option.ToString() == option.ToString())
                current.Reload();
            else
            {
                current.CancelLoad();
                _diff.DataContext = new ViewModels.DiffContext(_repository.FullPath, option, standalone: true);
                Title = ((ViewModels.DiffContext)_diff.DataContext).Title + " - TreeGit";
            }
            _waitingForRefresh = false;
        }

        private void OnRepositoryPageChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.LauncherPage.LoadedRepository) && _repositoryPage.LoadedRepository != _repository)
                Close();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_repository != null)
            {
                _repository.WorkingCopyRefreshed -= OnWorkingCopyRefreshed;
                _repositoryPage.PropertyChanged -= OnRepositoryPageChanged;
            }
            ((ViewModels.DiffContext)_diff.DataContext).CancelLoad();
        }

        private readonly DiffView _diff;
        private readonly ViewModels.Repository _repository;
        private readonly ViewModels.LauncherPage _repositoryPage;
        private readonly string _path;
        private readonly bool _isUnstaged;
        private bool _waitingForRefresh;
    }
}
