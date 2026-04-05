using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Caliburn.Micro;
using GameRealisticMap.Studio.Modules.MapConfigEditor.Nominatim;

namespace GameRealisticMap.Studio.Modules.MapConfigEditor.ViewModels
{
    public class NominatimSearchViewModel : Screen
    {
        private string _searchText;
        private bool _isSearching;
        private List<NominatimResult> _results;
        private NominatimResult _selectedResult;

        public NominatimSearchViewModel()
        {
            DisplayName = "Search OSM Boundary (Nominatim)";
        }

        public string SearchText
        {
            get { return _searchText; }
            set
            {
                _searchText = value;
                NotifyOfPropertyChange();
                NotifyOfPropertyChange(nameof(CanSearch));
            }
        }

        public bool IsSearching
        {
            get { return _isSearching; }
            set
            {
                _isSearching = value;
                NotifyOfPropertyChange();
                NotifyOfPropertyChange(nameof(CanSearch));
            }
        }

        public List<NominatimResult> Results
        {
            get { return _results; }
            set
            {
                _results = value;
                NotifyOfPropertyChange();
            }
        }

        public NominatimResult SelectedResult
        {
            get { return _selectedResult; }
            set
            {
                _selectedResult = value;
                NotifyOfPropertyChange();
                NotifyOfPropertyChange(nameof(CanAccept));
            }
        }

        public bool CanSearch => !string.IsNullOrWhiteSpace(SearchText) && !IsSearching;

        public async Task Search()
        {
            IsSearching = true;
            try
            {
                var allResults = await NominatimClient.SearchAsync(SearchText);
                // We mainly want relations (type boundary, etc.)
                Results = allResults.Where(r => r.OsmType == "relation").ToList();
            }
            finally
            {
                IsSearching = false;
            }
        }

        public bool CanAccept => SelectedResult != null;

        public Task Accept()
        {
            return TryCloseAsync(true);
        }

        public Task Cancel()
        {
            return TryCloseAsync(false);
        }
    }
}
