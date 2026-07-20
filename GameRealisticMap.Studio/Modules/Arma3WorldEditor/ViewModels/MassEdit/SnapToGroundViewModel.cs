using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GameRealisticMap.Arma3.Edit;
using GameRealisticMap.Studio.Modules.Reporting;
using Gemini.Framework;

namespace GameRealisticMap.Studio.Modules.Arma3WorldEditor.ViewModels.MassEdit
{
    internal class SnapToGroundViewModel : WindowBase
    {
        private readonly Arma3WorldEditorViewModel worldEditor;

        private string _filter = string.Empty;
        private bool _isPattern = true;
        private double _minDistance = 0.5;
        private bool _includeBuried;

        public SnapToGroundViewModel(Arma3WorldEditorViewModel worldEditor)
        {
            this.worldEditor = worldEditor;
        }

        /// <summary>
        /// Model filter, empty = every object.
        /// </summary>
        public string Filter { get { return _filter; } set { if (value != _filter) { _filter = value; NotifyOfPropertyChange(); } } }

        public bool IsPattern { get { return _isPattern; } set { if (value != _isPattern) { _isPattern = value; NotifyOfPropertyChange(); } } }

        public double MinDistance { get { return _minDistance; } set { if (value != _minDistance) { _minDistance = Math.Clamp(value, 0.01, 100); NotifyOfPropertyChange(); } } }

        public bool IncludeBuried { get { return _includeBuried; } set { if (value != _includeBuried) { _includeBuried = value; NotifyOfPropertyChange(); } } }

        public List<ObjectStatsItem> Models => worldEditor.ObjectStatsItems;

        public void SetPreset(string pattern)
        {
            Filter = pattern;
            IsPattern = true;
        }

        public Task Cancel() => TryCloseAsync(false);

        public Task Process()
        {
            var batch = new WrpMassEditBatch();
            batch.SnapToGround.Add(new WrpSnapToGround(Filter.Trim(), _isPattern, (float)_minDistance, _includeBuried));
            if (ProgressToolHelper.Start(new MassEditTask(batch, worldEditor)))
            {
                return TryCloseAsync(false);
            }
            return Task.CompletedTask;
        }
    }
}
