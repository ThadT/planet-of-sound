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
        string _spotifyTestPlaylistId = "32PFpdBZUi3x1MdeZdCCHb";

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
                string bioUrl = $"https://open.spotify.com/artist/{artist.Id}/about";
                ArtistLocationInfo locations = await ScrapeArtistInfo(bioUrl);
            }
        }

        private async Task<ArtistLocationInfo> ScrapeArtistInfo(string aboutArtistUrl)
        {
            string pageContent = "";

            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "C# console program");

                pageContent = await client.GetStringAsync(aboutArtistUrl);
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

            ArtistLocationInfo locations = FindLocations(aboutArtistJson, listenerCities);

            return locations;
        }

        private ArtistLocationInfo FindLocations(string about, Dictionary<string, long> listenerInfo)
        {
            string classifiedXml = _classifier.classifyWithInlineXML(about);

            MatchCollection locationMatches = _locationRx.Matches(classifiedXml);
            string primaryLocation = string.Empty;
            Dictionary<string, string> otherLocations = new Dictionary<string, string>();
            for(int i=0; i < locationMatches.Count; i++)
            {
                var m = locationMatches[i];
                var loc = m.Groups[1];
                if (i == 0)
                {
                    primaryLocation = loc.Value;
                    continue;
                }

                if (!otherLocations.ContainsKey(loc.Value))
                {
                    otherLocations.Add(loc.Value, loc.Value);
                }
            }

            LocationsList.ItemsSource = otherLocations.Keys;
            ResultTextBlock.Text = classifiedXml;

            ArtistLocationInfo artistInfo = new ArtistLocationInfo(primaryLocation, otherLocations.Keys.ToList(), listenerInfo);

            return artistInfo;
        }

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
    }

    public class ArtistLocationInfo
    {
        public string PrimaryLocation { get; set; }
        public List<string> OtherLocations { get; set; }
        public Dictionary<string, long> ListenersFrom { get; set; }

        public ArtistLocationInfo(string primaryLocation, List<string> secondaryLocations, Dictionary<string, long> listenerInfo)
        {
            PrimaryLocation = primaryLocation;
            OtherLocations = secondaryLocations;
            ListenersFrom = listenerInfo;
        }
    }
}
