using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Portal;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UI.Controls;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using SpotifyAPI.Web.Enums;
using SpotifyAPI.Web.Models;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlaylistMap
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private SpotifyWebAPI _spotify;
        private readonly string _planetOfSoundClientId = @"2e496ec1fec84a36acb345a446f7761c";
        private readonly string _spotifyPlaybackDevice = @"8fa98ee14fa0031b3b9113bbee54f16887f9838d";
        private readonly string _artistsLayerId = @"3d11c2713f5e48099ab4087ef332059a";
        private readonly string _listenersLayerId = @"f364ee167b1241ea809e9d138a8718ee";
        private readonly string _tourLayerId = @"70e2aab3e47e4a2089237b5d9aab92dc";
        private readonly string _otherPlacesLayerId = @"ad22901d56104adeaa79caf3c63d5221";
        private string _spotifyAcessToken = string.Empty;
        private string _spotifyTestPlaylistId = @"32PFpdBZUi3x1MdeZdCCHb"; 
        private List<TrackInfo> _playlistTrackInfo;
        private FeatureLayer _artistHometownLayer;
        private FeatureLayer _listenerLayer;
        private FeatureLayer _tourLayer;
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
            PortalItem tourLayerItem = await PortalItem.CreateAsync(portal, _tourLayerId);
            _artistHometownLayer = new FeatureLayer(artistLayerItem, 0);
            _listenerLayer = new FeatureLayer(listenerLayerItem, 0);
            _otherLocationsLayer = new FeatureLayer(otherLayerItem, 0);
            _tourLayer = new FeatureLayer(tourLayerItem, 0);
            _artistHometownLayer.IsVisible = false;
            _listenerLayer.IsVisible = false;
            _otherLocationsLayer.IsVisible = false;
            _tourLayer.IsVisible = false;

            // Create the map to show artist locations
            Map artistMap = new Map(Basemap.CreateLightGrayCanvasVector());
            artistMap.OperationalLayers.Add(_artistHometownLayer);
            artistMap.OperationalLayers.Add(_tourLayer);
            artistMap.OperationalLayers.Add(_otherLocationsLayer);

            // Create the map to show listener cities
            Map listenersMap = new Map(Basemap.CreateDarkGrayCanvasVector());
            listenersMap.OperationalLayers.Add(_listenerLayer);
            
            // Add the maps to their views
            ArtistMapView.Map = artistMap;
            ListenersMapView.Map = listenersMap;

            // Authorize with Spotify and get an access token
            //_spotifyAcessToken = GetClientCredentialsAuthToken();
            Task<bool> authTask = ConnectToSpotifyOAuth();
            await authTask;

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
                    bool isOnTour = int.Parse(f.Attributes["isontour"].ToString()) == 1;
                    string imgUrl = f.Attributes["imageurl"].ToString();
                    BitmapImage src = new BitmapImage(new Uri(imgUrl, UriKind.Absolute));

                    TrackInfo thisArtist = new TrackInfo(artistname, artistid, bio, src, hometown, trackname, trackid, f.Geometry as MapPoint, isOnTour);

                    // Add the track info to the list
                    artists.Add(thisArtist);
                }
            }

            return artists;
        }

        #region Utilities
        TaskCompletionSource<bool> _taskComplete;
        public Task<bool> ConnectToSpotifyOAuth()
        {
             _taskComplete = new TaskCompletionSource<bool>();
            ImplicitGrantAuth auth = new ImplicitGrantAuth(
              _planetOfSoundClientId,
              "http://localhost:4002",
              "http://localhost:4002",
              Scope.UserModifyPlaybackState
            );
            auth.AuthReceived += async (sender, payload) =>
            {
                auth.Stop(); // `sender` is also the auth instance

                // Get the Spotify API object (needs the access token)
                _spotify = new SpotifyWebAPI
                {
                    TokenType = payload.TokenType,
                    AccessToken = payload.AccessToken
                };

                _taskComplete.SetResult(true);
            };

            auth.Start(); // Starts an internal HTTP Server
            auth.OpenBrowser();

            return _taskComplete.Task;
        }

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

            // Make sure all layers are visible except tours
            _artistHometownLayer.IsVisible = true;
            _listenerLayer.IsVisible = true;
            _otherLocationsLayer.IsVisible = true;
            _tourLayer.IsVisible = false;

            // Dismiss any tour event callouts
            ArtistMapView.DismissCallout();

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

            Envelope extent = extentBuilder.ToGeometry();
            if (extent.IsEmpty) { return; }

            await ListenersMapView.SetViewpointGeometryAsync(extentBuilder.ToGeometry(), 30);
        }

        private bool _trackIsPlaying;
        private void Button_Click(object sender, RoutedEventArgs e)
        {
            // Get the selected track info
            TrackInfo ti = ArtistListBox.SelectedItem as TrackInfo;

            // If the track info is good, play it (or pause if playing)
            if (ti == null) { return; }

            ErrorResponse err = null;
            PlaybackContext playback = _spotify.GetPlayback();
            if (_trackIsPlaying)
            {
                err = _spotify.PausePlayback();
            }
            else
            {
                List<string> trackUris = new List<string> { "spotify:track:" + ti.TrackId };
                err = _spotify.ResumePlayback(deviceId: _spotifyPlaybackDevice, contextUri: "", uris: trackUris, offset: "", positionMs: 0);
            }

            if (err?.Error?.Status != 0)
            {
                _trackIsPlaying = !_trackIsPlaying;
            }
            else
            {
                // TODO: evaluate error
            }
        }

        private async void ArtistMapViewTapped(object sender, GeoViewInputEventArgs e)
        {
            // Get the user-tapped location
            MapPoint mapLocation = e.Location;

            // Perform an identify across all layers, taking up to 10 results per layer.
            IdentifyLayerResult tourClickResult = await ArtistMapView.IdentifyLayerAsync(_tourLayer, e.Position, 15, false, 1);
            if(tourClickResult.GeoElements.Count == 0) { return; }
            
            // Get the clicked tour event
            GeoElement tourEvent = tourClickResult.GeoElements.FirstOrDefault();
            await (tourEvent as ArcGISFeature).LoadAsync();

            // Format the callout string to show info for this event
            string tourEventDescription = string.Format("Date: {0:D}\nVenue: {1}", tourEvent.Attributes["eventdate"], tourEvent.Attributes["venuename"].ToString());

            // Create a new callout definition using the formatted string
            CalloutDefinition tourEventCalloutDefinition = new CalloutDefinition(tourEvent.Attributes["artistname"].ToString() + " - Let's Rock Tour", tourEventDescription);
            // tourEventCalloutDefinition.Icon = tourImage;
            FrameworkElement tourPanel = Application.Current.FindResource("TourCalloutPanel") as FrameworkElement;
            TextBlock titleTextBlock = FindChild<TextBlock>(tourPanel, "TitleTextBlock");
            TextBlock dateTextBlock = FindChild<TextBlock>(tourPanel, "EventDateTextBlock");
            TextBlock venueTextBlock = FindChild<TextBlock>(tourPanel, "EventVenueTextBlock");
            System.Windows.Controls.Image tourImage = FindChild<System.Windows.Controls.Image>(tourPanel, "TourImage");

            titleTextBlock.Text = tourEvent.Attributes["artistname"].ToString() + " - Let's Rock Tour";
            dateTextBlock.Text = string.Format("{0:D}", tourEvent.Attributes["eventdate"]);
            venueTextBlock.Text = tourEvent.Attributes["venuename"].ToString();

            BitmapImage poster = new BitmapImage();
            poster.BeginInit();
            poster.UriSource = new Uri(@"https://ih1.redbubble.net/image.790993324.9948/flat,128x,075,f-pad,128x128,f8f8f8.u2.jpg");
            poster.EndInit();
            tourImage.Source = poster;

            // Display the callout
            ArtistMapView.ShowCalloutAt(mapLocation, tourPanel);
            //ArtistMapView.ShowCalloutAt(mapLocation, tourEventCalloutDefinition);
        }

        public static T FindChild<T>(DependencyObject parent, string childName)
           where T : DependencyObject
        {
            // Confirm parent and childName are valid. 
            if (parent == null) return null;

            T foundChild = null;

            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                // If the child is not of the request child type child
                T childType = child as T;
                if (childType == null)
                {
                    // recursively drill down the tree
                    foundChild = FindChild<T>(child, childName);

                    // If the child is found, break so we do not overwrite the found child. 
                    if (foundChild != null) break;
                }
                else if (!string.IsNullOrEmpty(childName))
                {
                    var frameworkElement = child as FrameworkElement;
                    // If the child's name is set for search
                    if (frameworkElement != null && frameworkElement.Name == childName)
                    {
                        // if the child's name is of the request name
                        foundChild = (T)child;
                        break;
                    }
                }
                else
                {
                    // child element found.
                    foundChild = (T)child;
                    break;
                }
            }

            return foundChild;
        }

        private async void TourButton_Click(object sender, RoutedEventArgs e)
        {
            // Get the selected track info
            TrackInfo ti = ArtistListBox.SelectedItem as TrackInfo;

            // Filter the tour layer by artist ID
            string artistFilter = "artistid = '" + ti.ArtistId + "'";
            _tourLayer.DefinitionExpression = artistFilter;
            _tourLayer.IsVisible = true;

            // Zoom to the extent of the tour
            QueryParameters query = new QueryParameters
            {
                WhereClause = artistFilter
            };
            FeatureQueryResult tourQueryResult = await _tourLayer.FeatureTable.QueryFeaturesAsync(query);

            // Zoom to the first result (assumed to be the next event?)
            Feature nextEvent = tourQueryResult.FirstOrDefault();
            if(nextEvent == null) { return; }

            // Zoom to the event
            await ArtistMapView.SetViewpointCenterAsync(nextEvent.Geometry as MapPoint);
        }
    }

    public class TrackInfo
    {
        public string TrackName { get; set; }
        public string TrackId { get; set; }
        public string ArtistName { get; set; }
        public string ArtistId { get; set; }
        public bool IsOnTour { get; set; }
        public string Bio { get; set; }
        public ImageSource Image { get; set; }
        public string Hometown { get; set; }
        public MapPoint HometownLocation { get; set; }

        public TrackInfo(string name, string id, string bio, ImageSource thumbnail, string hometown, string trackname, string trackid, MapPoint homelocation, bool isOnTour) 
        {
            ArtistName = name;
            ArtistId = id;
            Bio = bio;
            Image = thumbnail;
            Hometown = hometown;
            TrackName = trackname;
            TrackId = trackid;
            HometownLocation = homelocation;
            IsOnTour = isOnTour;
        }
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            Visibility isVisible = Visibility.Hidden;
            bool ok = bool.TryParse(value.ToString(), out bool itsTrue);
            
            if (ok && itsTrue)
            {
                isVisible = Visibility.Visible;
            }

            return isVisible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
