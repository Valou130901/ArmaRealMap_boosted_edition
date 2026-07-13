using System;
using System.Linq;
using Caliburn.Micro;
using GameRealisticMap.Arma3.Edit;
using NLog.Targets;

namespace GameRealisticMap.Studio.Modules.Arma3WorldEditor.ViewModels.MassEdit
{
    public class ReduceItem : PropertyChangedBase
    {
        private ReduceViewModel? owner;

        private string _source = string.Empty;

        private double _factor = 0.5;

        private bool _isPattern;

        public string Source { get { return _source; } set { if (value != _source ) { _source = value; NotifyOfPropertyChange(); NotifyOfPropertyChange(nameof(Count)); NotifyOfPropertyChange(nameof(TargetCount)); } } }

        public double Factor { get { return _factor; } set { if (value != _factor) { _factor = Math.Clamp(value, 0, 1); NotifyOfPropertyChange(); NotifyOfPropertyChange(nameof(Count)); NotifyOfPropertyChange(nameof(TargetCount)); } } }

        /// <summary>
        /// When true, <see cref="Source"/> is matched as a substring against every model path,
        /// so a whole category is reduced at once (e.g. "tree" hits all tree models).
        /// </summary>
        public bool IsPattern { get { return _isPattern; } set { if (value != _isPattern) { _isPattern = value; NotifyOfPropertyChange(); NotifyOfPropertyChange(nameof(Count)); NotifyOfPropertyChange(nameof(TargetCount)); } } }

        public int Count
        {
            get
            {
                if (string.IsNullOrEmpty(_source))
                {
                    return 0;
                }
                var objects = owner?.ParentEditor?.World?.Objects;
                if (objects == null)
                {
                    return 0;
                }
                if (_isPattern)
                {
                    return objects.Count(o => !string.IsNullOrEmpty(o.Model) && o.Model.Contains(_source, StringComparison.OrdinalIgnoreCase));
                }
                return objects.Count(o => string.Equals(o.Model, _source, StringComparison.OrdinalIgnoreCase));
            }
        }

        public int TargetCount => (int)(Count * (1.0 - Factor));

        internal void Attach(ReduceViewModel replaceViewModel)
        {
            if (owner == null)
            {
                owner = replaceViewModel;
                NotifyOfPropertyChange(nameof(Count));
                NotifyOfPropertyChange(nameof(TargetCount));
            }
        }

        internal WrpMassReduce ToOperation()
        {
            return new WrpMassReduce(Source, Factor, _isPattern);
        }
    }
}