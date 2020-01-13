using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Portal;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Models;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace PlaylistMap
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private SpotifyWebAPI _spotify; 
        private readonly string _artistsLayerId = @"3d11c2713f5e48099ab4087ef332059a";
        private readonly string _listenersLayerId = @"f364ee167b1241ea809e9d138a8718ee";
        private readonly string _otherPlacesLayerId = @"ad22901d56104adeaa79caf3c63d5221";
        private string _spotifyAcessToken = string.Empty;
        private string _spotifyTestPlaylistId = @"32PFpdBZUi3x1MdeZdCCHb"; 
        private List<TrackInfo> _playlistTrackInfo;
        private FeatureLayer _artistHometownLayer;
        private FeatureLayer _listenerLayer;
        private FeatureLayer _otherLocationsLayer;

        public MainWindow()
        {
            InitializeComponent();
            InitMap();
        }

        private async Task InitMap()
        {
            // Get the artist and listener layers from ArcGIS Online
            ArcGISPortal portal = await ArcGISPortal.CreateAsync();
            PortalItem artistLayerItem = await PortalItem.CreateAsync(portal, _artistsLayerId);
            PortalItem listenerLayerItem = await PortalItem.CreateAsync(portal, _listenersLayerId);
            PortalItem otherLayerItem = await PortalItem.CreateAsync(portal, _otherPlacesLayerId);
            _artistHometownLayer = new FeatureLayer(artistLayerItem, 0);
            _listenerLayer = new FeatureLayer(listenerLayerItem, 0);
            _otherLocationsLayer = new FeatureLayer(otherLayerItem, 0);
            
            // Create the map to show artist locations
            Map artistMap = new Map(Basemap.CreateLightGrayCanvasVector());
            artistMap.OperationalLayers.Add(_artistHometownLayer);
            artistMap.OperationalLayers.Add(_otherLocationsLayer);

            // Create the map to show listener cities
            Map listenersMap = new Map(Basemap.CreateDarkGrayCanvasVector());
            listenersMap.OperationalLayers.Add(_listenerLayer);
            
            // Add the maps to their views
            ArtistMapView.Map = artistMap;
            ListenersMapView.Map = listenersMap;

            // Authorize with Spotify and get an access token
            _spotifyAcessToken = GetClientCredentialsAuthToken();

            // Get the Spotify API object (needs the access token)
            _spotify = new SpotifyWebAPI
            {
                AccessToken = _spotifyAcessToken,
                TokenType = "Bearer"
            };

            // Get the tracks for the provided playlist
            Paging<PlaylistTrack> tracks = _spotify.GetPlaylistTracks(_spotifyTestPlaylistId);

            // Call a function to get a collection of track and artist info
            _playlistTrackInfo = await GetPlaylistArtists(tracks, _artistHometownLayer.FeatureTable);

            ArtistListBox.ItemsSource = _playlistTrackInfo;
            ArtistListBox.SelectedIndex = 0;
        }

        private async Task<List<TrackInfo>> GetPlaylistArtists(Paging<PlaylistTrack> playlistTracks, FeatureTable artistPlaces)
        {
            List<TrackInfo> artists = new List<TrackInfo>();

            foreach (PlaylistTrack plt in playlistTracks.Items)
            {
                SimpleArtist artist = plt.Track.Artists.FirstOrDefault();

                string artistid = artist.Id;
                string trackid = plt.Track.Id;
                string trackname = plt.Track.Name;

                QueryParameters query = new QueryParameters
                {
                    WhereClause = "artistid = '" + artistid + "'"
                };

                FeatureQueryResult queryResult = await artistPlaces.QueryFeaturesAsync(query);
                foreach (Feature f in queryResult)
                {
                    await (f as ArcGISFeature).LoadAsync();
                    string artistname = f.Attributes["artistname"].ToString();
                    string hometown = f.Attributes["placename"].ToString();
                    string bio = f.Attributes["bioshort"].ToString();
                    string imgUrl = f.Attributes["imageurl"].ToString();
                    BitmapImage src = new BitmapImage(new Uri(imgUrl, UriKind.Absolute));

                    TrackInfo thisArtist = new TrackInfo(artistname, artistid, bio, src, hometown, trackname, trackid, f.Geometry as MapPoint);

                    // Add the track info to the list
                    artists.Add(thisArtist);
                }
            }

            return artists;
        }

        #region Utilities
        public string GetClientCredentialsAuthToken()
        {
            FileStream appInfo = System.IO.File.OpenRead(@"C:\Temp\Spotify_PlanetOfSoundAppId.txt");
            TextReader appInfoReader = new StreamReader(appInfo);
            string appInfoText = appInfoReader.ReadToEnd();
            string[] info = appInfoText.Split(new string[] { "\r\n" }, System.StringSplitOptions.RemoveEmptyEntries);

            var spotifyClient = info[0].Split(":".ToCharArray())[1];
            var spotifySecret = info[1].Split(":".ToCharArray())[1];

            using (WebClient webClient = new WebClient())
            {
                var postparams = new NameValueCollection();
                postparams.Add("grant_type", "client_credentials");

                var authHeader = System.Convert.ToBase64String(Encoding.Default.GetBytes($"{spotifyClient}:{spotifySecret}"));
                webClient.Headers.Add(HttpRequestHeader.Authorization, "Basic " + authHeader);

                var tokenResponse = webClient.UploadValues("https://accounts.spotify.com/api/token", postparams);
                var responseString = Encoding.UTF8.GetString(tokenResponse);

                return ExtractFromString(responseString, "\"access_token\":\"", "\",\"");
            }
        }

        private string ExtractFromString(string inputString, string startString, string endString = "&")
        {
            int stringStartPos = inputString.IndexOf(startString, StringComparison.InvariantCulture) + startString.Length;
            int stringEndPos = inputString.IndexOf(endString, stringStartPos, StringComparison.InvariantCulture);
            if (stringEndPos == -1) { stringEndPos = inputString.Length; }

            int stringLength = stringEndPos - stringStartPos;

            return inputString.Substring(stringStartPos, stringLength);
        }
        #endregion

        private void Expander_Expanded(object sender, RoutedEventArgs e)
        {

        }

        private async void ArtistListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Get the selected track info
            TrackInfo ti = ArtistListBox.SelectedItem as TrackInfo;

            // If the track info is good, show it in the info panel
            if (ti == null) { return; }
            ArtistInfoPanel.DataContext = ti;

            // Filter the layers by artist ID
            string artistFilter = "artistid = '" + ti.ArtistId + "'";
            _artistHometownLayer.DefinitionExpression = artistFilter;
            _listenerLayer.DefinitionExpression = artistFilter;
            _otherLocationsLayer.DefinitionExpression = artistFilter;

            // Zoom the main map to the artist hometown
            await ArtistMapView.SetViewpointCenterAsync(ti.HometownLocation, 250000);

            // Zoom the listener map to the extent of features in the listener layer
            QueryParameters query = new QueryParameters
            {
                WhereClause = artistFilter
            };

            FeatureQueryResult listenerQueryResult = await _listenerLayer.FeatureTable.QueryFeaturesAsync(query);
            EnvelopeBuilder extentBuilder = new EnvelopeBuilder(ListenersMapView.SpatialReference);
            foreach(Feature f in listenerQueryResult)
            {
                extentBuilder.UnionOf(f.Geometry.Extent);
            }

            await ListenersMapView.SetViewpointGeometryAsync(extentBuilder.ToGeometry(), 30);
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {

        }
    }

    public class TrackInfo
    {
        public string TrackName { get; set; }
        public string TrackId { get; set; }
        public string ArtistName { get; set; }
        public string ArtistId { get; set; }
        public string Bio { get; set; }
        public ImageSource Image { get; set; }
        public string Hometown { get; set; }
        public MapPoint HometownLocation { get; set; }

        public TrackInfo(string name, string id, string bio, ImageSource thumbnail, string hometown, string trackname, string trackid, MapPoint homelocation) 
        {
            ArtistName = name;
            ArtistId = id;
            Bio = bio;
            Image = thumbnail;
            Hometown = hometown;
            TrackName = trackname;
            TrackId = trackid;
            HometownLocation = homelocation;
        }
    }
}
