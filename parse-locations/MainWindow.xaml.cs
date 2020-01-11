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
using Esri.ArcGISRuntime.Portal;
using Esri.ArcGISRuntime.Data;

namespace ParseLocations
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private SpotifyWebAPI _spotify;
        private string _hometownLayerId = @"3d11c2713f5e48099ab4087ef332059a";
        private string _otherPointsLayerId = @"ad22901d56104adeaa79caf3c63d5221";
        private ServiceFeatureTable _otherPointsTable;
        private ServiceFeatureTable _hometownTable;
        private CRFClassifier _classifier;
        private Regex _locationRx;
        StructuredDataExtractor _artistScraping;
        string _spotifyAcessToken = string.Empty;
        string _spotifyTestPlaylistId = @"32PFpdBZUi3x1MdeZdCCHb";
        string _locatorUrl = @"https://geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer";

        public MainWindow()
        {
            InitializeComponent();
            GetClientCredentialsAuthToken();
            Init();
        }

        private async Task Init()
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

            // Get the hosted feature layers for editing 
            ArcGISPortal portal = await ArcGISPortal.CreateAsync();
            PortalItem hometownLayerItem = await PortalItem.CreateAsync(portal, _hometownLayerId);
            PortalItem otherPointsLayerItem = await PortalItem.CreateAsync(portal, _otherPointsLayerId);
            _hometownTable = new ServiceFeatureTable(hometownLayerItem, 0);
            _otherPointsTable = new ServiceFeatureTable(otherPointsLayerItem, 0);
            await _hometownTable.LoadAsync();
            await _otherPointsTable.LoadAsync();
        }
        
        private Paging<PlaylistTrack> GetPlaylistTracks()
        {
            Paging<PlaylistTrack> tracks = null;

            try
            {
                _spotify = new SpotifyWebAPI
                {
                    AccessToken = _spotifyAcessToken,
                    TokenType = "Bearer"
                };

                tracks = _spotify.GetPlaylistTracks(_spotifyTestPlaylistId);
            } catch (System.Exception) 
            { 

            }

            return tracks;
        }

        private async void ScrapeButton_Click(object sender, RoutedEventArgs e)
        {
            ScrapeButton.IsEnabled = false;

            try
            {
                Paging<PlaylistTrack> tracks = GetPlaylistTracks();
                bool skip = true;
                foreach (PlaylistTrack plt in tracks.Items)
                {
                    SimpleArtist artist = plt.Track.Artists.FirstOrDefault();
                    //// skip the ones that we've processed :\
                    //if(artist.Id == "0z6zRFzl5njXWLVAisXQBz") 
                    //{
                    //    skip = false;
                    //    continue; 
                    //}
                    //if (skip) { continue; }

                    FullArtist fullArtist = _spotify.GetArtist(artist.Id);
                    await ScrapeArtistInfo(fullArtist);
                }
            }
            catch(System.Exception ex)
            {
                ArtistProgressList.Items.Add("** Error: " + ex.Message);
            }
        }

        private async Task ScrapeArtistInfo(FullArtist artist)
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
            // if there's no bio, we can't find locations :(
            if (bio == null)
            {
                return;
            }
            
            string fullBio = bio.ToString() + artistResults["more"].ToString();
            int shortBioEnd = System.Math.Min(fullBio.Length, 255);
            string shortBio = fullBio.Substring(0, shortBioEnd);
            
            Dictionary<string, int> listenerCities = new Dictionary<string, int>();
            for (int i = 0; i < listeners.Count(); i++)
            {
                string listenersString = listeners[i].ToString().Replace("LISTENERS", "").Replace(",", "").Trim();
                int numListeners = int.Parse(listenersString);
                listenerCities.Add(cities[i].ToString(), numListeners);
            }

            string aboutArtistJson = JsonConvert.SerializeObject(fullBio, Newtonsoft.Json.Formatting.Indented);
            string classifiedXml = _classifier.classifyWithInlineXML(aboutArtistJson);
            // HACK: fix "city, state" locations that are split into two: 
            //       "<LOCATION>Logan</LOCATION>, <LOCATION>Utah</LOCATION>" => "<LOCATION>Logan, Utah</LOCATION>"
            classifiedXml = classifiedXml.Replace("</LOCATION>, <LOCATION>", ", ");
            MatchCollection locationMatches = _locationRx.Matches(classifiedXml);
            
            Dictionary<string, Graphic> artistLocations = new Dictionary<string, Graphic>();

            // Build artist locations
            for (int i = 0; i < locationMatches.Count; i++)
            {
                var m = locationMatches[i];
                string loc = m.Groups[1].Value;
                MapPoint geocodedLocation = await GeocodeArtistPlacename(loc);
                if (geocodedLocation == null) { continue; }

                // If the place name was geocoded, create a new feature to store it
                // (first one is considered the hometown) :\
                if (i == 0)
                {
                    Feature newHometownFeature = _hometownTable.CreateFeature();
                    newHometownFeature.Geometry = geocodedLocation;
                    newHometownFeature.Attributes["placename"] = loc;
                    newHometownFeature.Attributes["artistname"] = artist.Name;
                    newHometownFeature.Attributes["artistid"] = artist.Id;
                    newHometownFeature.Attributes["imageurl"] = artist.Images.Last().Url;
                    newHometownFeature.Attributes["bioshort"] = shortBio;

                    await _hometownTable.AddFeatureAsync(newHometownFeature);
                }
                else
                {
                    if (!artistLocations.ContainsKey(loc))
                    {
                        Feature otherFeature = _otherPointsTable.CreateFeature();
                        otherFeature.Geometry = geocodedLocation;
                        otherFeature.Attributes["placename"] = loc;
                        otherFeature.Attributes["artistname"] = artist.Name;
                        otherFeature.Attributes["artistid"] = artist.Id;

                        await _otherPointsTable.AddFeatureAsync(otherFeature);
                    }
                }
            }

            // Apply edits to the hometown table (will apply other edits after adding listener cities)
            await _hometownTable.ApplyEditsAsync();

            // Create points for the listener cities
            int r = 0;
            foreach(var lc in listenerCities)
            {
                r++;
                MapPoint geocodedLocation = await GeocodeArtistPlacename(lc.Key);
                if (geocodedLocation == null) { continue; }
                
                Feature otherFeature = _otherPointsTable.CreateFeature();
                otherFeature.Geometry = geocodedLocation;
                otherFeature.Attributes["placename"] = lc.Key;
                otherFeature.Attributes["artistname"] = artist.Name;
                otherFeature.Attributes["artistid"] = artist.Id;
                otherFeature.Attributes["listenercount"] = lc.Value;
                otherFeature.Attributes["listenerrank"] = r;

                await _otherPointsTable.AddFeatureAsync(otherFeature);
            }

            // Apply edits to the other locations table
            await _otherPointsTable.ApplyEditsAsync();

            ArtistProgressList.Items.Add(artist.Name);
        }

        //see https://developer.spotify.com/web-api/authorization-guide/#client_credentials_flow
        public void GetClientCredentialsAuthToken()
        {
            var spotifyClient = "2e496ec1fec84a36acb345a446f7761c";
            var spotifySecret = "ee2adbe865c345abbedc41c77582f993";

            using (WebClient webClient = new WebClient())
            {
                var postparams = new NameValueCollection();
                postparams.Add("grant_type", "client_credentials");

                var authHeader = System.Convert.ToBase64String(Encoding.Default.GetBytes($"{spotifyClient}:{spotifySecret}"));
                webClient.Headers.Add(HttpRequestHeader.Authorization, "Basic " + authHeader);

                var tokenResponse = webClient.UploadValues("https://accounts.spotify.com/api/token", postparams);
                var responseString = Encoding.UTF8.GetString(tokenResponse);

                _spotifyAcessToken = ExtractFromString(responseString, "\"access_token\":\"", "\",\"");
            }
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
