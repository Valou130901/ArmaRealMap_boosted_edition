using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Caliburn.Micro;
using Gemini.Framework;
using Gemini.Framework.Services;
using Gemini.Modules.Output.Properties;
using Gemini.Modules.Output.Views;

namespace Gemini.Modules.Output.ViewModels
{
    [Export(typeof(IOutput))]
    public class OutputViewModel : Tool, IOutput
    {
        private readonly StringBuilder _stringBuilder;
        private readonly OutputWriter _writer;
        private IOutputView _view;
        private readonly Timer _debounceTimer;
        private readonly Stopwatch _lastUpdate = new Stopwatch();

        private const int MaxLengthTrim = 800_000;
        private const int MaxLength = 1_000_000;
        private const int DebounceInterval = 500; // 0.5 second

        public override PaneLocation PreferredLocation
        {
            get { return PaneLocation.Bottom; }
        }

        public TextWriter Writer
        {
            get { return _writer; }
        }

        public OutputViewModel()
        {
            DisplayName = Resources.OutputDisplayName;
            _stringBuilder = new StringBuilder();
            _writer = new OutputWriter(this);
            _debounceTimer = new Timer(OnDebounceTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Clear()
        {
            if (_view != null)
                Execute.OnUIThread(() => _view.Clear());
            _stringBuilder.Clear();
        }

        public void AppendLine(string text)
        {
            Append(text + Environment.NewLine);
        }

        public void Append(string text)
        {
            _stringBuilder.Append(text);
            if (_stringBuilder.Length > MaxLength)
            {
                _stringBuilder.Remove(0, _stringBuilder.Length - MaxLengthTrim);
            }
            OnTextChanged();
        }

        private void OnDebounceTimerElapsed(object state)
        {
            Execute.OnUIThread(() => _view.SetText(_stringBuilder.ToString()));
        }

        private void OnTextChanged()
        {
            if (_view == null)
            {
                return;
            }
            if (_lastUpdate.IsRunning && _lastUpdate.ElapsedMilliseconds < DebounceInterval)
            {
                // Timer is already running, no need to restart it
                return;
            }
            _debounceTimer.Change(DebounceInterval, Timeout.Infinite); // Update when DebounceInterval has passed
            _lastUpdate.Restart();
        }

        protected override void OnViewLoaded(object view)
        {
            _view = (IOutputView)view;
            _view.SetText(_stringBuilder.ToString());
            _view.ScrollToEnd();
        }
    }
}
