using System;
using System.Collections.Generic;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class CommandLog : ObservableObject, Models.ICommandLog
    {
        public string Name
        {
            get;
            private set;
        }

        public DateTime StartTime
        {
            get;
        } = DateTime.Now;

        public DateTime EndTime
        {
            get;
            private set;
        } = DateTime.Now;

        public bool IsComplete
        {
            get;
            private set;
        } = false;

        public string Content
        {
            get
            {
                var text = IsComplete ? _content : _buffer.ToString();
                return (_truncated || _buffer?.WasTruncated == true) ? App.Text("Log.Truncated") + "\n" + text : text;
            }
        }

        public CommandLog(string name)
        {
            Name = name;
        }

        public void Subscribe(Models.ICommandLogReceiver receiver)
        {
            _receivers.Add(receiver);
        }

        public void Unsubscribe(Models.ICommandLogReceiver receiver)
        {
            _receivers.Remove(receiver);
        }

        public void AppendLine(string line = null)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Invoke(() => AppendLine(line));
            }
            else
            {
                var newline = line ?? string.Empty;
                if (_buffer != null)
                    _buffer.AppendLine(newline);
                else
                {
                    var tail = new Models.BoundedTextBuffer(MaximumLength);
                    tail.Append(_content);
                    tail.AppendLine(newline);
                    _content = tail.ToString();
                    _truncated |= tail.WasTruncated;
                }

                foreach (var receiver in _receivers)
                    receiver.OnReceiveCommandLog(newline);
            }
        }

        public void Complete()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Invoke(Complete);
                return;
            }

            if (IsComplete) return;
            IsComplete = true;
            EndTime = DateTime.Now;

            _content = _buffer.ToString();
            _truncated |= _buffer.WasTruncated;
            _receivers.Clear();
            _buffer = null;

            OnPropertyChanged(nameof(IsComplete));
        }

        private string _content = string.Empty;
        private const int MaximumLength = 512 * 1024;
        private Models.BoundedTextBuffer _buffer = new(MaximumLength);
        private bool _truncated;
        private List<Models.ICommandLogReceiver> _receivers = new List<Models.ICommandLogReceiver>();
    }
}
