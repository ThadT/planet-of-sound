using edu.stanford.nlp.ie.crf;
using Newtonsoft.Json;
using OpenScraping;
using OpenScraping.Config;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using SpotifyAPI.Web.Enums;
using SpotifyAPI.Web.Models;
using SpotifyAPI.Web;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Tasks.Geocoding;
using Esri.ArcGISRuntime.UI;

namespace ParseLocations
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private CRFClassifier _classifier;
        private Regex _locationRx;
        StructuredDataExtractor _artistScraping;
        StructuredDataExtractor _listenerScraping;
        string _spotifyAcessToken = string.Empty;
        string _spotifyTestPlaylistId = @"32PFpdBZUi3x1MdeZdCCHb";
        string _locatorUrl = @"https://geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer";

        public MainWindow()
        {
            InitializeComponent();
            GetClientCredentialsAuthToken();
            Init();
        }

        private void Init()
        {
            // Path to the folder with classifiers models
            var jarRoot = @"C:\stanford-ner-2018-10-16";
            var classifiersDirecrory = jarRoot + @"\classifiers";

            // Loading 3 class classifier model
            _classifier = CRFClassifier.getClassifierNoExceptions(
                classifiersDirecrory + @"\english.all.3class.distsim.crf.ser.gz");

            // Define a regular expression for finding the location element
            _locationRx = new Regex(@"<LOCATION\b[^>]*>(.*?)</LOCATION>",
              RegexOptions.Compiled | RegexOptions.IgnoreCase);
 
            // Define configurations for parsing artist and listener info
            var configArtistInfoJson = @"
            {
                'artist': '//h1[contains(@class, \'view-header\')]',
                'about': '//div[contains(@class, \'bio-primary\')]',
                'more': '//div[contains(@class, \'bio-secondary\')]',
                'listeners-city': '//span[contains(@class, \'horizontal-list__item__title\')]',
                'listeners': '//span[contains(@class, \'horizontal-list__item__subtitle\')]'
            }"; 

            ConfigSection configArtist = StructuredDataConfig.ParseJsonString(configArtistInfoJson);

            _artistScraping = new StructuredDataExtractor(configArtist);
        }
        
        private Paging<PlaylistTrack> GetPlaylistTracks()
        {
            Paging<PlaylistTrack> tracks = null;

            try
            {
                SpotifyWebAPI api = new SpotifyWebAPI
                {
                    AccessToken = _spotifyAcessToken,
                    TokenType = "Bearer"
                };

                tracks = api.GetPlaylistTracks(_spotifyTestPlaylistId);
            } catch (System.Exception) 
            { 

            }

            return tracks;
        }

        private async void ScrapeButton_Click(object sender, RoutedEventArgs e)
        {
            Paging<PlaylistTrack> tracks = GetPlaylistTracks();
            foreach(PlaylistTrack plt in tracks.Items)
            {
                SimpleArtist artist = plt.Track.Artists.FirstOrDefault();
                ArtistLocationInfo locations = await ScrapeArtistInfo(artist);
            }
        }

        private async Task<ArtistLocationInfo> ScrapeArtistInfo(SimpleArtist artist)
        {
            string bioUrl = $"https://open.spotify.com/artist/{artist.Id}/about";
            string pageContent = "";

            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "C# console program");

                pageContent = await client.GetStringAsync(bioUrl);
            }

            var artistResults = _artistScraping.Extract(pageContent);
            var cities = artistResults["listeners-city"];
            var listeners = artistResults["listeners"];
            var bio = artistResults["about"];
            var fullBio = bio.ToString() + artistResults["more"].ToString();

            Dictionary<string, long> listenerCities = new Dictionary<string, long>();
            for (int i = 0; i < listeners.Count(); i++)
            {
                string listenersString = listeners[i].ToString().Replace("LISTENERS", "").Replace(",", "").Trim();
                long numListeners = long.Parse(listenersString);
                listenerCities.Add(cities[i].ToString(), numListeners);
            }

            string aboutArtistJson = JsonConvert.SerializeObject(fullBio, Newtonsoft.Json.Formatting.Indented);

            //ArtistLocationInfo locations = await FindLocations(aboutArtistJson, listenerCities);

            //return locations;
            string classifiedXml = _classifier.classifyWithInlineXML(aboutArtistJson);

            MatchCollection locationMatches = _locationRx.Matches(classifiedXml);
            Dictionary<string, Graphic> artistLocations = new Dictionary<string, Graphic>();

            for (int i = 0; i < locationMatches.Count; i++)
            {
                var m = locationMatches[i];
                string loc = m.Groups[1].Value;
                MapPoint geocodedLocation = await GeocodeArtistPlacename(loc);
                if (geocodedLocation == null) { continue; }

                Graphic locationGraphic = new Graphic(geocodedLocation);
                locationGraphic.Attributes.Add("Name", loc);
                locationGraphic.Attributes.Add("ArtistName", artist.Name);
                locationGraphic.Attributes.Add("ArtistId", artist.Id);
                if (i == 0)
                {
                    locationGraphic.Attributes.Add("IsHometown", true);
                }

                if (!artistLocations.ContainsKey(loc))
                {
                    artistLocations.Add(loc, locationGraphic);
                }
            }

            // Create points for the listener cities
            int r = 0;
            foreach(var lc in listenerCities)
            {
                r++;
                MapPoint geocodedLocation = await GeocodeArtistPlacename(lc.Key);
                if (geocodedLocation == null) { continue; }

                Graphic locationGraphic = new Graphic(geocodedLocation);
                locationGraphic.Attributes.Add("Name", lc.Key);
                locationGraphic.Attributes.Add("ArtistName", artist.Name);
                locationGraphic.Attributes.Add("ArtistId", artist.Id);
                locationGraphic.Attributes.Add("Listeners", lc.Value);
                locationGraphic.Attributes.Add("ListenerRank", r);

                if (!artistLocations.ContainsKey(lc.Key))
                {
                    artistLocations.Add(lc.Key, locationGraphic);
                }
            }

            ArtistLocationInfo artistInfo = new ArtistLocationInfo(artistLocations.Values.ToList());

            return artistInfo;
        }

        //private async Task<ArtistLocationInfo> FindLocations(string about, Dictionary<string, long> listenerInfo)
        //{
        //    string classifiedXml = _classifier.classifyWithInlineXML(about);

        //    MatchCollection locationMatches = _locationRx.Matches(classifiedXml);
        //    Graphic hometownGraphic = new Graphic();
        //    List<Graphic> otherLocations = new List<Graphic>();

        //    for(int i=0; i < locationMatches.Count; i++)
        //    {
        //        var m = locationMatches[i];
        //        string loc = m.Groups[1].Value;
        //        MapPoint geocodedLocation = await GeocodeArtistPlacename(loc);
        //        if(geocodedLocation == null) { continue; }

        //        if (i == 0)
        //        {
        //            hometownGraphic.Geometry = geocodedLocation;
        //            hometownGraphic.Attributes.Add("Name", loc);

        //            continue;
        //        }

        //        if (!otherLocations.ContainsKey(loc))
        //        {
        //            otherLocations.Add(loc, geocodedLocation);
        //        }
        //    }

        //    LocationsList.ItemsSource = otherLocations.Keys;
        //    ResultTextBlock.Text = classifiedXml;

        //    ArtistLocationInfo artistInfo = new ArtistLocationInfo(primaryLocation, otherLocations.Keys.ToList(), listenerInfo);

        //    return artistInfo;
        //}

        //see https://developer.spotify.com/web-api/authorization-guide/#client_credentials_flow
        public void GetClientCredentialsAuthToken()
        {
            var spotifyClient = "093838caadba48c9a5ffcc7039b9de23";
            var spotifySecret = "c8a5c9aac5994b42a5e7c02a1f40e6ee";

            var webClient = new WebClient();

            var postparams = new NameValueCollection();
            postparams.Add("grant_type", "client_credentials");

            var authHeader = System.Convert.ToBase64String(Encoding.Default.GetBytes($"{spotifyClient}:{spotifySecret}"));
            webClient.Headers.Add(HttpRequestHeader.Authorization, "Basic " + authHeader);

            var tokenResponse = webClient.UploadValues("https://accounts.spotify.com/api/token", postparams);

            var responseString = Encoding.UTF8.GetString(tokenResponse);

            _spotifyAcessToken = ExtractFromString(responseString, "\"access_token\":\"", "\",\"");
        }

        private string ExtractFromString(string inputString, string startString, string endString = "&")
        {
            int stringStartPos = inputString.IndexOf(startString, System.StringComparison.InvariantCulture) + startString.Length;
            int stringEndPos = inputString.IndexOf(endString, stringStartPos, System.StringComparison.InvariantCulture);
            if (stringEndPos == -1) { stringEndPos = inputString.Length; }

            int stringLength = stringEndPos - stringStartPos;

            return inputString.Substring(stringStartPos, stringLength);
        }

        private async Task<MapPoint> GeocodeArtistPlacename(string placeName)
        {
            MapPoint matchedPoint = null;
            LocatorTask locatorTask = new LocatorTask(new System.Uri(_locatorUrl));
            GeocodeParameters p = new GeocodeParameters
            {
                MaxResults = 1,
                MinScore = 85
            };
            IReadOnlyList<GeocodeResult> placeMatches = await locatorTask.GeocodeAsync(placeName, p);

            if (placeMatches.Count > 0)
            {
                GeocodeResult place = placeMatches.FirstOrDefault();
                matchedPoint = place.DisplayLocation;
            }

            return matchedPoint;
        }
    }

    public class ArtistLocationInfo
    {
        public List<Graphic> ArtistCities { get; set; }

        public ArtistLocationInfo(List<Graphic> artistCities)
        {
            ArtistCities = artistCities;
        }
    }
}
