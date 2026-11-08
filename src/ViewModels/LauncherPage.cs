using System;
using System.Threading.Tasks;
using System.Linq;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class LauncherPage : ObservableObject
    {
        public RepositoryNode Node
        {
            get => _node;
            set => SetProperty(ref _node, value);
        }

        public object Data
        {
            get => _data;
            set
            {
                if (SetProperty(ref _data, value))
                    OnPropertyChanged(nameof(LoadedRepository));
            }
        }

        public Repository LoadedRepository => _data as Repository;

        public Models.DirtyState DirtyState
        {
            get => _dirtyState;
            private set => SetProperty(ref _dirtyState, value);
        }

        public Popup Popup
        {
            get => _popup;
            set => SetProperty(ref _popup, value);
        }

        public AvaloniaList<Models.Notification> Notifications
        {
            get;
            set;
        } = new AvaloniaList<Models.Notification>();

        public LauncherPage()
        {
            _node = new RepositoryNode() { Id = Guid.NewGuid().ToString() };
            _data = Welcome.Instance;

            // New welcome page will clear the search filter before.
            Welcome.Instance.ClearSearchFilter();
        }

        public LauncherPage(RepositoryNode node, object data)
        {
            _node = node;
            _data = data;
        }

        public void ClearNotifications()
        {
            Notifications.Clear();
        }

        public void ChangeDirtyState(Models.DirtyState flag, bool remove)
        {
            var state = _dirtyState;
            if (remove)
            {
                if (state.HasFlag(flag))
                    state -= flag;
            }
            else
            {
                state |= flag;
            }

            DirtyState = state;
        }

        public bool CanCreatePopup()
        {
            return _popup is not { InProgress: true };
        }

        public bool HasRunningOperation => _popup?.InProgress == true || _data is Repository repo &&
            (repo.IsAutoFetching || repo.WorkingCopy is { IsCommitting: true } or { IsStaging: true } or { IsUnstaging: true } || repo.Logs.Any(log => !log.IsComplete));

        public long PopupAttemptVersion { get; private set; }

        public async Task ProcessPopupAsync()
        {
            if (_popup is { InProgress: false } dump)
            {
                PopupAttemptVersion++;
                if (!dump.Check())
                    return;

                dump.InProgress = true;

                try
                {
                    var finished = await dump.Sure();
                    if (finished)
                    {
                        dump.Cleanup();
                        Popup = null;
                    }
                }
                catch (Exception e)
                {
                    Native.OS.LogException(e);
                }
                finally
                {
                    dump.InProgress = false;
                    // Disabling background refresh must not hide the result of an explicit action.
                    if (_data is Repository repo && !repo.Settings.AutoRefreshEnabled)
                        repo.RefreshAll();
                }
            }
        }

        public void CancelPopup()
        {
            if (_popup == null || _popup.InProgress)
                return;

            _popup?.Cleanup();
            Popup = null;
        }

        public void TerminatePopup()
        {
            if (_popup is not { CanTerminate: true, InProgress: true })
                return;

            _popup.Terminate();
        }

        private RepositoryNode _node = null;
        private object _data = null;
        private Models.DirtyState _dirtyState = Models.DirtyState.None;
        private Popup _popup = null;
    }
}
