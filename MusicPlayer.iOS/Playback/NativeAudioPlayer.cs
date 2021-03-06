using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AVFoundation;
using CoreFoundation;
using Foundation;
using Metal;
using MusicPlayer.Data;
using MusicPlayer.iOS;
using MusicPlayer.iOS.Playback;
using MusicPlayer.Managers;
using MusicPlayer.Models;
using MusicPlayer.Models.Scrobbling;
using UIKit;
using CoreAnimation;

namespace MusicPlayer.Playback
{
	internal class NativeAudioPlayer : BaseModel
	{
		public static NativeAudioPlayer Shared {get;set;}
		public readonly AudioFadePlayer player;
		readonly AVAudioPlayer silentPlayer;
		IDisposable observer;

		NSTimer timer;
        PlaybackState lastState;
        DateTime lastInterupt;
		public AudioFadePlayer.CustomVideoLayer VideoLayer { get; }
		public NativeAudioPlayer()
		{
			Shared = this;
            timer = NSTimer.CreateRepeatingScheduledTimer(2,CheckPlaybackStatus);
			NSError error;
			#if __IOS__
			AVAudioSession.SharedInstance().SetCategory(AVAudioSession.CategoryPlayback, out error);
			#endif
			LoaderDelegate.Parent = this;

			player = new AudioFadePlayer{ Parent = this };
			VideoLayer = player.VideoLayer;

			#if __IOS__
			silentPlayer = new AVAudioPlayer(NSBundle.MainBundle.GetUrlForResource("empty","mp3"),"mp3", out error) {
				NumberOfLoops = -1,
			};

			PictureInPictureManager.Shared.Setup(VideoLayer);
//			observer = player.AddObserver("rate", NSKeyValueObservingOptions.New, (change) =>
//			{
//				Console.WriteLine("Playback state changed: {0}", player.Rate);
//				if (player.Rate == 0)
//				{
//					State = PlaybackState.Paused;
//					Console.WriteLine("AVPlayer Paused");
//				}
//				else
//				{
//					State = PlaybackState.Playing;
//					Console.WriteLine("AVPlayer Playing");
//				}
//			});
//			NSNotificationCenter.DefaultCenter.AddObserver(AVPlayerItem.DidPlayToEndTimeNotification, (notification) =>
//			{
//				finishedPlaying(currentSong);
//				player.ReplaceCurrentItemWithPlayerItem(new AVPlayerItem(new NSUrl("")));
//			});
			AVAudioSession.Notifications.ObserveInterruption((sender, args) =>
				{
					if (args.InterruptionType == AVAudioSessionInterruptionType.Began)
					{
						lastState = State;
						if(State == PlaybackState.Playing)
							Pause();
						else
							State = PlaybackState.Stopped;
						lastInterupt = DateTime.Now;
					}
					else if(args.InterruptionType == AVAudioSessionInterruptionType.Ended){
						State = lastState;
						if(args.Option == AVAudioSessionInterruptionOptions.ShouldResume && lastState == PlaybackState.Playing)
							Play();
						else
							Pause();
					}
					NotificationManager.Shared.ProcPlaybackStateChanged(State);
					Console.WriteLine("Interupted,: {2} -  {0} , {1}", args.InterruptionType, args.Option,(DateTime.Now - lastInterupt).TotalSeconds);
				});

			AVAudioSession.Notifications.ObserveRouteChange((sender, args) =>
				{
					if(args.Reason == AVAudioSessionRouteChangeReason.OldDeviceUnavailable)
						Pause();
					Console.WriteLine("Route Changed");
				});
			#endif
		}
		double lastDurration;
		double lastSeconds;
		Task checkPlaybackTask;
	   void CheckPlaybackStatus(NSTimer timer)
	    {
	        if (CurrentSong == null || CurrentItem == null)
	            return;
			if(Duration > 0 && Math.Abs (lastDurration) < Double.Epsilon)
			{
				Seek(Settings.CurrentPlaybackPercent);
				lastDurration = Duration;
			}
			if(State == PlaybackState.Paused || State == PlaybackState.Stopped)
				return;
			var time = player.CurrentTimeSeconds();
			if (player.Rate > 0 && Math.Abs (time - lastSeconds) > Double.Epsilon) {
				lastSeconds = time;
				return;
			}
			if(checkPlaybackTask != null && !checkPlaybackTask.IsCompleted)
				return;
			checkPlaybackTask = Task.Run(async ()=> {
				if (player.Rate > 0) {
					await PrepareSong(CurrentSong, isVideo);
					player.Play();
					if(time > 0)
						player.Seek(time);

				}
				else
					Play();
			});
	    }
		public async Task PrepareFirstTrack(Song song, bool isVideo)
		{
			if (!(await PrepareSong (song, isVideo)).Item1)
				return;
			await player.PrepareSong (song, isVideo);
		}


		public AVPlayerItem CurrentItem => player.CurrentItem;

		public double CurrentTime => player.CurrentTimeSeconds();

		public double Duration => player.Duration();

		public float Progress => (float) (Math.Abs(Duration) < Double.Epsilon ? 0 : CurrentTime/Duration);

		public void DisableVideo()
		{
			player.DisableVideo();
		}

		public void EnableVideo()
		{
			player.EnableVideo();
		}
		public void Pause()
		{
			#if __IOS__
			silentPlayer.Pause();
			#endif
			player.Pause();
			State = PlaybackState.Paused;
		}

		public async void Play()
		{
			#if __IOS__
			silentPlayer.Play();
			#endif
			if ((State == PlaybackState.Playing && player.Rate > 0 && player.CurrentItem.Tracks.Length > 0)|| State == PlaybackState.Buffering || CurrentSong == null)
			{
				return;
			}
			if (player.CurrentItem != null && player.CurrentItem.Error == null && player.CurrentItem.Tracks.Length > 0)
			{
				ScrobbleManager.Shared.SetNowPlaying(CurrentSong, Settings.CurrentTrackId);
				player.Play();
				Seek(Settings.CurrentPlaybackPercent);
			}
			else
				await PlaySong(CurrentSong, isVideo);
		}

		public void QueueTrack(Track track)
		{
			player.Queue (track);
		}

		public Song CurrentSong
		{
			get { return currentSong; }
			set { ProcPropertyChanged(ref currentSong, value); }
		}

		public PlaybackState State
		{
			get { return state;	}
			set { 
				ProcPropertyChanged(ref state, value);
					//NotificationManager.Shared.ProcPlaybackStateChanged(state);
			}
		}

		public float[] AudioLevels 
		{
			get{return player.AudioLevels;}
			set{ player.AudioLevels = value; }
		}

		public readonly Dictionary<string, PlaybackData> CurrentData = new Dictionary<string, PlaybackData>();
		public readonly Dictionary<string, string> SongIdTracks = new Dictionary<string, string>();

		public class PlaybackData
		{
			public string SongId { get; set; }
			public SongPlaybackData SongPlaybackData { get; set; }
			public DownloadHelper DownloadHelper { get; set; }
			public CancellationTokenSource CancelTokenSource { get; set; } = new CancellationTokenSource();
		}

		internal PlaybackData GetPlaybackData(string id, bool create = true)
		{
			lock (CurrentData)
			{
				PlaybackData data;
				if (!CurrentData.TryGetValue(id, out data) && create)
					CurrentData[id] = data = new PlaybackData
					{
						SongId = id,
					};
				return data;
			}
		}

		void finishedPlaying(Song song)
		{
			ScrobbleManager.Shared.PlaybackEnded(new PlaybackEndedEvent(song)
			{
				TrackId = Settings.CurrentTrackId,
				Context = Settings.CurrentPlaybackContext,
				Position = this.CurrentTime,
				Reason = ScrobbleManager.PlaybackEndedReason.PlaybackEnded,
			});
			CleanupSong(song);
			State = PlaybackState.Stopped;
#pragma warning disable 4014
			PlaybackManager.Shared.NextTrack();
#pragma warning restore 4014
		}

		void TryPlayAgain(string songId)
		{
			var song = Database.Main.GetObject<Song, TempSong>(songId);
			CleanupSong(song);
			PlaySong(song,isVideo);
		}

		public void CleanupSong(Song song)
		{
			lastSeconds = -1;
			if (string.IsNullOrWhiteSpace(song?.Id))
				return;
			var data = GetPlaybackData(song.Id, false);
			if (data == null)
				return;
		
			data.CancelTokenSource.Cancel();
			data = null;
			CurrentData.Remove(song.Id);
			var key = SongIdTracks.FirstOrDefault(x => x.Value == song.Id);
			if (key.Key != null)
				SongIdTracks.Remove(key.Key);
		}
		AVPlayerItem currentPlayerItem;
        bool isVideo;
		async Task<Tuple<bool,AVPlayerItem>> prepareSong(Song song, bool playVideo = false)
		{
			try
			{
				isVideo = playVideo;
				LogManager.Shared.Log("Preparing Song", song);
				var data = GetPlaybackData(song.Id);
				var playbackData = await MusicManager.Shared.GetPlaybackData(song, playVideo);
				if (playbackData == null)
					return new Tuple<bool, AVPlayerItem>(false,null);
				if (data.CancelTokenSource.IsCancellationRequested)
					return new Tuple<bool, AVPlayerItem>(false,null);
			
				AVPlayerItem playerItem = null;

				if (song == CurrentSong)
				{
					Settings.CurrentTrackId = playbackData.CurrentTrack.Id;
					isVideo = playbackData.CurrentTrack.MediaType == MediaType.Video;
					Settings.CurrentPlaybackIsVideo = isVideo;
					NotificationManager.Shared.ProcVideoPlaybackChanged(isVideo);
				}
				if (playbackData.IsLocal || playbackData.CurrentTrack.ServiceType == MusicPlayer.Api.ServiceType.iPod)
				{
					if(playbackData.Uri == null)
						return new Tuple<bool, AVPlayerItem>(false,null);
					LogManager.Shared.Log("Local track found",song);
					var url = string.IsNullOrWhiteSpace(playbackData?.CurrentTrack?.FileLocation) ? new NSUrl(playbackData.Uri.AbsoluteUri) : NSUrl.FromFilename(playbackData.CurrentTrack.FileLocation);
					playerItem = AVPlayerItem.FromUrl(url);
					await playerItem.WaitStatus();
					NotificationManager.Shared.ProcSongDownloadPulsed(song.Id, 1f);
				}
				else
				{
					data.SongPlaybackData = playbackData;
					data.DownloadHelper = await DownloadManager.Shared.DownloadNow(playbackData.CurrentTrack.Id, playbackData.Uri);
					if (data.CancelTokenSource.IsCancellationRequested)
						return new Tuple<bool, AVPlayerItem>(false,null);
					LogManager.Shared.Log("Loading online Track", data.SongPlaybackData.CurrentTrack);
					SongIdTracks[data.SongPlaybackData.CurrentTrack.Id] = song.Id;
					NSUrlComponents comp =
						new NSUrlComponents(
							NSUrl.FromString(
								$"http://localhost/{playbackData.CurrentTrack.Id}.{data.SongPlaybackData.CurrentTrack.FileExtension}"), false);
					comp.Scheme = "streaming";
					if (comp.Url != null)
					{
						var asset = new AVUrlAsset(comp.Url, new NSDictionary());
						asset.ResourceLoader.SetDelegate(LoaderDelegate, DispatchQueue.MainQueue);
						playerItem = new AVPlayerItem(asset);
					}
					if (data.CancelTokenSource.IsCancellationRequested)
						return new Tuple<bool, AVPlayerItem>(false,null);

					await playerItem.WaitStatus();
				}
				lastSeconds = -1;
				var success =  !data.CancelTokenSource.IsCancellationRequested;

				return new Tuple<bool, AVPlayerItem>(true,playerItem);
			}
			catch(Exception ex)
			{
				LogManager.Shared.Report(ex);
				return new Tuple<bool, AVPlayerItem>(false,null);
			}
		}

		Dictionary<Tuple<string,bool>,Task<Tuple<bool,AVPlayerItem>>> prepareTasks = new Dictionary<Tuple<string, bool>, Task<Tuple<bool,AVPlayerItem>>>();
		public async Task<Tuple<bool,AVPlayerItem>> PrepareSong(Song song, bool playVideo = false)
		{
			var tuple = new Tuple<string,bool>(song.Id, playVideo);
			Task<Tuple<bool,AVPlayerItem>> task;
			lock (prepareTasks) {
				if (!prepareTasks.TryGetValue (tuple, out task) || task.IsCompleted)
					prepareTasks [tuple] = task = prepareSong (song, playVideo);
			}
			var result = await task;
			lock (prepareTasks) {
				prepareTasks.Remove (tuple);
			}
			return result;
        }

		public async Task PlaySong(Song song, bool playVideo = false)
		{
			#if __IOS__
			if(!playVideo)
				PictureInPictureManager.Shared.StopPictureInPicture();
			#endif
			player.Pause();
			CleanupSong(CurrentSong);
			CurrentSong = song;
			Settings.CurrentTrackId = "";
			Settings.CurrentPlaybackIsVideo = false;
			NotificationManager.Shared.ProcCurrentTrackPositionChanged(new TrackPosition
			{
				CurrentTime = 0,
				Duration = 0,
			});
			if (song == null)
			{
				State = PlaybackState.Stopped;
				return;
			}
			State = PlaybackState.Buffering;
			#if __IOS__
			silentPlayer.Play();
			#endif

			var success = await player.PlaySong(song, playVideo);
			ScrobbleManager.Shared.SetNowPlaying(song, Settings.CurrentTrackId);
			if (CurrentSong != song)
				return;
			if (!success)
			{
				this.State = PlaybackState.Stopped;
				PlaybackManager.Shared.NextTrack();
			}
		}

		readonly MyResourceLoaderDelegate LoaderDelegate = new MyResourceLoaderDelegate();
		PlaybackState state;
		Song currentSong;

		public bool ShouldWaitForLoadingOfRequestedResource(AVAssetResourceLoader resourceLoader,
			AVAssetResourceLoadingRequest loadingRequest)
		{
			try
			{
				var id = loadingRequest.Request.Url.Path.Trim('/');
				if (id.Contains('.'))
					id = id.Substring(0, id.IndexOf('.'));
				var songId = SongIdTracks[id];
				var data = GetPlaybackData(songId, false);
				Task.Run(() => ProcessesRequest(resourceLoader, loadingRequest, data));
				return true;
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex);
			}
			return false;
		}

		public void Seek(float percent)
		{
			var seconds = percent*Duration;
			if(double.IsNaN(seconds))
				seconds = 0;
			player.Seek(seconds);

			NotificationManager.Shared.ProcCurrentTrackPositionChanged(new TrackPosition
			{
				CurrentTime = seconds,
				Duration = Duration,
			});
		}

		public void DidCancelLoadingRequest(AVAssetResourceLoader resourceLoader, AVAssetResourceLoadingRequest loadingRequest)
		{
		}

		async void ProcessesRequest(AVAssetResourceLoader resourceLoader,
			AVAssetResourceLoadingRequest loadingRequest, PlaybackData data)
		{
			if (data == null)
			{
				loadingRequest.FinishLoading();
				return;
			}
			try
			{
				var currentDownloadHelper = data.DownloadHelper;
				var content = loadingRequest.ContentInformationRequest;
				if (content != null)
				{
					content.ByteRangeAccessSupported = true;

					if (string.IsNullOrWhiteSpace(currentDownloadHelper.MimeType))
					{
						var success = await currentDownloadHelper.WaitForMimeType();
					}
					content.ContentType = currentDownloadHelper.MimeType;
					content.ContentLength = currentDownloadHelper.TotalLength;
				}

				var dataRequest = loadingRequest.DataRequest;

				Console.WriteLine(
					$"Data Request: {dataRequest.RequestedOffset} - {dataRequest.RequestedLength} : {dataRequest.CurrentOffset} - {currentDownloadHelper.TotalLength}");
				var allData = Device.IsIos9 && dataRequest.RequestsAllDataToEndOfResource;
                long exspected = allData ? Math.Max(dataRequest.RequestedLength,currentDownloadHelper.TotalLength) : dataRequest.RequestedLength;
				int sent = 0;
				var bufer = new byte[exspected];
				lock (currentDownloadHelper)
				{
					while (sent < exspected)
					{
						if (data.CancelTokenSource.IsCancellationRequested)
						{
							loadingRequest.FinishLoading();
							return;
						}
						var startOffset = dataRequest.CurrentOffset != 0 ? dataRequest.CurrentOffset : dataRequest.RequestedOffset;
						var remaining = exspected - sent;
						currentDownloadHelper.Position = startOffset;
						if (loadingRequest.IsCancelled)
							break;
						var read = currentDownloadHelper.Read(bufer, 0, (int) remaining);
						var sendBuffer = bufer.Take(read).ToArray();
						dataRequest.Respond(NSData.FromArray(sendBuffer));
						sent += read;
						if(sent + startOffset >= currentDownloadHelper.TotalLength)
							break;
					}
				}
				if (!loadingRequest.IsCancelled){
					loadingRequest.FinishLoading();
					if(NativeAudioPlayer.Shared.State == PlaybackState.Buffering || NativeAudioPlayer.Shared.State == PlaybackState.Playing)
						NativeAudioPlayer.Shared.player.CurrentPlayer.Play();
				}
			}
			catch (Exception ex)
			{
				loadingRequest.FinishLoadingWithError(new NSError((NSString) ex.Message, 0));
				TryPlayAgain(data.SongId);
				Console.WriteLine("***************** ERROR ******************");
				Console.WriteLine("Error in Resouce Loader. Trying Again\r\n {0}", ex);
				Console.WriteLine("*******************************************");
			}
		}

		class MyResourceLoaderDelegate : AVAssetResourceLoaderDelegate
		{
			public NativeAudioPlayer Parent { get; set; }

			public override bool ShouldWaitForLoadingOfRequestedResource(AVAssetResourceLoader resourceLoader,
				AVAssetResourceLoadingRequest loadingRequest)
			{
				return Parent.ShouldWaitForLoadingOfRequestedResource(resourceLoader, loadingRequest);
			}

			public override void DidCancelLoadingRequest(AVAssetResourceLoader resourceLoader,
				AVAssetResourceLoadingRequest loadingRequest)
			{
				Parent.DidCancelLoadingRequest(resourceLoader, loadingRequest);
				// base.DidCancelLoadingRequest(resourceLoader, loadingRequest);
			}

			public override bool ShouldWaitForRenewalOfRequestedResource(AVAssetResourceLoader resourceLoader,
				AVAssetResourceRenewalRequest renewalRequest)
			{
				return base.ShouldWaitForRenewalOfRequestedResource(resourceLoader, renewalRequest);
			}

			public override bool ShouldWaitForResponseToAuthenticationChallenge(AVAssetResourceLoader resourceLoader,
				NSUrlAuthenticationChallenge authenticationChallenge)
			{
				return base.ShouldWaitForResponseToAuthenticationChallenge(resourceLoader, authenticationChallenge);
			}
		}

		public static async Task<bool> VerifyMp3(string path, bool deleteBadFile = false)
		{
			if (!File.Exists (path)) {
				LogManager.Shared.Log("File does not exist");
				return false;
			}
			var asset = AVAsset.FromUrl(NSUrl.FromFilename(path));
			await asset.LoadValuesTaskAsync(new[]{ "duration" ,"tracks"});
			if (asset.Duration.Seconds > 0)
			{
				asset.Dispose();
				return true;
			}
			LogManager.Shared.Log("File is too short",key:"File Size",value:new FileInfo(path).Length.ToString());
			if (deleteBadFile)
				System.IO.File.Delete(path);
			return false;
		}
	}
}