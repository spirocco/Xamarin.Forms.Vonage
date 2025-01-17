﻿using System;
using System.ComponentModel;
using System.Linq;
using Android;
using Android.Content.PM;
using Android.Runtime;
using Com.Opentok.Android;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using Android.OS;
using AndroidX.Core.Content;
using AndroidX.Core.App;

namespace Xamarin.Forms.Vonage.Android.Services
{
    [Preserve(AllMembers = true)]
    public sealed class PlatformVonageService : BaseVonageService
    {
        public event Action PublisherUpdated;
        public event Action SubscriberUpdated;

        private readonly object _sessionLocker = new();
        private readonly ObservableCollection<string> _subscriberStreamIds = new();
        private readonly Collection<SubscriberKit> _subscribers = new();

        public PlatformVonageService()
        {
            _subscriberStreamIds.CollectionChanged += OnSubscriberStreamIdsCollectionChanged;
            PropertyChanged += OnPropertyChanged;
            StreamIdCollection = new ReadOnlyObservableCollection<string>(_subscriberStreamIds);
            Subscribers = new ReadOnlyCollection<SubscriberKit>(_subscribers);
        }

        public override ReadOnlyObservableCollection<string> StreamIdCollection { get; }
        public ReadOnlyCollection<SubscriberKit> Subscribers { get; }
        public Session Session { get; private set; }
        public PublisherKit PublisherKit { get; private set; }

        public override bool TryStartSession()
        {
            lock (_sessionLocker)
            {
                if (!CheckPermissions() ||
                    string.IsNullOrWhiteSpace(ApiKey) ||
                    string.IsNullOrWhiteSpace(SessionId) ||
                    string.IsNullOrWhiteSpace(UserToken))
                {
                    return false;
                }

                EndSession();
                IsSessionStarted = true;

                PlatformVonage.Activity.RunOnUiThread(() =>
                {
                    using var builder = new Session.Builder(PlatformVonage.Activity.ApplicationContext, ApiKey, SessionId)
                    .SessionOptions(new VonageSessionOptions());
                    Session = builder.Build();
                    Session.ConnectionDestroyed += OnConnectionDestroyed;
                    Session.Connected += OnConnected;
                    Session.StreamReceived += OnStreamReceived;
                    Session.StreamDropped += OnStreamDropped;
                    Session.Error += OnError;
                    Session.Signal += OnSignal;
                    Session.Connect(UserToken);
                });

                return true;
            }
        }

        public override void EndSession()
        {
            lock (_sessionLocker)
            {
                try
                {
                    if (Session == null)
                    {
                        return;
                    }

                    foreach (var subscriberKit in _subscribers)
                    {
                        ClearSubscriber(subscriberKit);
                    }
                    _subscribers.Clear();
                    _subscriberStreamIds.Clear();

                    ClearPublisher();

                    RaisePublisherUpdated().
                        RaiseSubscriberUpdated();

                    if (Session != null)
                    {
                        using (Session)
                        {
                            Session.ConnectionDestroyed -= OnConnectionDestroyed;
                            Session.Connected -= OnConnected;
                            Session.StreamReceived -= OnStreamReceived;
                            Session.StreamDropped -= OnStreamDropped;
                            Session.Error -= OnError;
                            Session.Signal -= OnSignal;
                            Session.Disconnect();
                        }
                        Session = null;
                    }

                }
                finally
                {
                    IsSessionStarted = false;
                    IsPublishingStarted = false;
                }
            }
        }

        public override bool CheckPermissions()
        {
            var permissions = GetPermissions().ToArray();
            var shouldGrantPermissions = permissions.Any(permission => ContextCompat.CheckSelfPermission(PlatformVonage.Activity, permission) != (int)Permission.Granted);
            if (shouldGrantPermissions)
            {
                ActivityCompat.RequestPermissions(PlatformVonage.Activity, permissions, 0);
            }
            return !shouldGrantPermissions;
        }

        public override bool TrySendMessage(string message, string messageType)
        {
            if (Session == null)
            {
                return false;
            }

            Session.SendSignal(messageType ?? string.Empty, message);
            return true;
        }

        public override void CycleCamera() => (PublisherKit as Publisher)?.CycleCamera();

        private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(PublisherName):
                case nameof(PublisherVideoType):
                case nameof(PublisherCameraResolution):
                    OnConnected(this, new Session.ConnectedEventArgs(Session));
                    return;
                case nameof(IsVideoPublishingEnabled):
                    UpdatePublisherProperty(p => p.PublishVideo = IsVideoPublishingEnabled);
                    return;
                case nameof(IsAudioPublishingEnabled):
                    UpdatePublisherProperty(p => p.PublishAudio = IsAudioPublishingEnabled);
                    return;
                case nameof(PublisherVideoScaleStyle):
                    UpdatePublisherProperty(SetVideoScaleStyle);
                    return;
                case nameof(IsVideoSubscriptionEnabled):
                    UpdateSubscriberProperty(s => s.SubscribeToVideo = IsVideoSubscriptionEnabled);
                    return;
                case nameof(IsAudioSubscriptionEnabled):
                    UpdateSubscriberProperty(s => s.SubscribeToAudio = IsAudioSubscriptionEnabled);
                    return;
                case nameof(SubscriberVideoScaleStyle):
                    UpdateSubscriberProperty(SetVideoScaleStyle);
                    return;
            }
        }

        private void UpdatePublisherProperty(Action<PublisherKit> updateAction)
        {
            if (PublisherKit == null)
            {
                return;
            }
            updateAction?.Invoke(PublisherKit);
        }

        private void UpdateSubscriberProperty(Action<SubscriberKit> updateAction)
        {
            foreach (var subscriberKit in _subscribers)
            {
                updateAction?.Invoke(subscriberKit);
            }
        }

        private IEnumerable<string> GetPermissions()
        {
            if (Permissions.HasFlag(VonagePermission.Camera))
            {
                yield return Manifest.Permission.Camera;
            }

            if ((int)Build.VERSION.SdkInt < 33)
            {
                if (Permissions.HasFlag(VonagePermission.WriteExternalStorage))
                {
                    yield return Manifest.Permission.WriteExternalStorage;
                }
            }

            if (Permissions.HasFlag(VonagePermission.RecordAudio))
            {
                yield return Manifest.Permission.RecordAudio;
            }

            if (Permissions.HasFlag(VonagePermission.ModifyAudioSettings))
            {
                yield return Manifest.Permission.ModifyAudioSettings;
            }

            if (Permissions.HasFlag(VonagePermission.Bluetooth))
            {
                yield return Manifest.Permission.Bluetooth;
            }

            if ((int)Build.VERSION.SdkInt >= 33)
            {
                if (Permissions.HasFlag(VonagePermission.ReadMediaAudio))
                {
                    yield return Manifest.Permission.ReadMediaAudio;
                }

                if (Permissions.HasFlag(VonagePermission.ReadMediaImages))
                {
                    yield return Manifest.Permission.ReadMediaImages;
                }

                if (Permissions.HasFlag(VonagePermission.ReadMediaVideo))
                {
                    yield return Manifest.Permission.ReadMediaVideo;
                }
            }

            yield return Manifest.Permission.Internet;

            yield return Manifest.Permission.AccessNetworkState;
        }

        private void OnConnectionDestroyed(object sender, Session.ConnectionDestroyedEventArgs e)
            => RaiseSubscriberUpdated();

        private void OnConnected(object sender, Session.ConnectedEventArgs e)
        {
            if (Session == null)
            {
                return;
            }

            ClearPublisher();

            using var builder = new Publisher.Builder(PlatformVonage.Activity.ApplicationContext)
                .Resolution(GetResolution())
                .VideoTrack(Permissions.HasFlag(VonagePermission.Camera))
                .AudioTrack(Permissions.HasFlag(VonagePermission.RecordAudio))
                .Name(PublisherName);
            PublisherKit = builder.Build();
            PublisherKit.PublishVideo = IsVideoPublishingEnabled;
            PublisherKit.PublishAudio = IsAudioPublishingEnabled;
            SetVideoScaleStyle(PublisherKit);

            PublisherKit.StreamCreated += OnPublisherStreamCreated;
            PublisherKit.AudioFallbackEnabled = PublisherVideoType == VonagePublisherVideoType.Camera;
            PublisherKit.PublisherVideoType = PublisherVideoType == VonagePublisherVideoType.Camera
                ? PublisherKit.PublisherKitVideoType.PublisherKitVideoTypeCamera
                : PublisherKit.PublisherKitVideoType.PublisherKitVideoTypeScreen;
            Session.Publish(PublisherKit);
            RaisePublisherUpdated();
        }

        private void OnStreamReceived(object sender, Session.StreamReceivedEventArgs e)
        {
            if (Session == null)
            {
                return;
            }

            DropStream(e.P1?.StreamId);

            using var builder = new Subscriber.Builder(PlatformVonage.Activity.ApplicationContext, e.P1);
            var subscriberKit = builder.Build();
            subscriberKit.SubscribeToAudio = IsAudioSubscriptionEnabled;
            subscriberKit.SubscribeToVideo = IsVideoSubscriptionEnabled;
            SetVideoScaleStyle(subscriberKit);

            subscriberKit.Connected += OnSubscriberConnected;
            subscriberKit.StreamDisconnected += OnStreamDisconnected;
            subscriberKit.SubscriberDisconnected += OnSubscriberDisconnected;
            subscriberKit.VideoDataReceived += OnSubscriberVideoDataReceived;
            subscriberKit.VideoDisabled += OnSubscriberVideoDisabled;
            subscriberKit.VideoEnabled += OnSubscriberVideoEnabled;

            Session.Subscribe(subscriberKit);
            var streamId = e.P1.StreamId;
            _subscribers.Add(subscriberKit);
            _subscriberStreamIds.Add(streamId);
        }

        private void OnStreamDropped(object sender, Session.StreamDroppedEventArgs e)
            => DropStream(e.P1?.StreamId);

        private void OnError(object sender, Session.ErrorEventArgs e)
        {
            RaiseErrorOccurred(e.P1?.Message);
            EndSession();
        }

        private void OnSubscriberVideoDisabled(object sender, SubscriberKit.VideoDisabledEventArgs e)
            => RaiseSubscriberUpdated();

        private void OnSubscriberVideoDataReceived(object sender, SubscriberKit.VideoDataReceivedEventArgs e)
            => RaiseSubscriberUpdated();

        private void OnSubscriberVideoEnabled(object sender, SubscriberKit.VideoEnabledEventArgs e)
            => RaiseSubscriberUpdated();

        private void OnSubscriberConnected(object sender, SubscriberKit.ConnectedEventArgs e)
            => RaisePublisherUpdated().RaiseSubscriberUpdated();

        private void OnSubscriberDisconnected(object sender, SubscriberKit.SubscriberListenerDisconnectedEventArgs e)
            => RaisePublisherUpdated().RaiseSubscriberUpdated();

        private void OnStreamDisconnected(object sender, SubscriberKit.StreamListenerDisconnectedEventArgs e)
            => RaisePublisherUpdated().RaiseSubscriberUpdated();

        private void DropStream(string streamId)
        {
            var subscriberKit = _subscribers.FirstOrDefault(x => x.Stream?.StreamId == streamId);
            if (subscriberKit != null)
            {
                ClearSubscriber(subscriberKit);
                _subscribers.Remove(subscriberKit);
            }
            _subscriberStreamIds.Remove(streamId);
            RaiseSubscriberUpdated();
        }

        private PlatformVonageService RaiseSubscriberUpdated()
        {
            SubscriberUpdated?.Invoke();
            return this;
        }

        private PlatformVonageService RaisePublisherUpdated()
        {
            PublisherUpdated?.Invoke();
            return this;
        }

        private void OnPublisherStreamCreated(object sender, PublisherKit.StreamCreatedEventArgs e)
            => IsPublishingStarted = true;

        private void OnSignal(object sender, Session.SignalEventArgs e)
        {
            if (!(IgnoreSentMessages && e.P3.ConnectionId == Session.Connection.ConnectionId))
            {
                RaiseMessageReceived(e.P2, e.P1);
            }
        }

        private void ClearSubscriber(SubscriberKit subscriberKit)
        {
            using (subscriberKit)
            {
                subscriberKit.SubscribeToAudio = false;
                subscriberKit.SubscribeToVideo = false;
                subscriberKit.Connected -= OnSubscriberConnected;
                subscriberKit.StreamDisconnected -= OnStreamDisconnected;
                subscriberKit.SubscriberDisconnected -= OnSubscriberDisconnected;
                subscriberKit.VideoDataReceived -= OnSubscriberVideoDataReceived;
                subscriberKit.VideoDisabled -= OnSubscriberVideoDisabled;
                subscriberKit.VideoEnabled -= OnSubscriberVideoEnabled;
                Session.Unsubscribe(subscriberKit);
            }
        }

        private void ClearPublisher()
        {
            if (PublisherKit == null)
            {
                return;
            }

            using (PublisherKit)
            {
                PublisherKit.PublishAudio = false;
                PublisherKit.PublishVideo = false;
                PublisherKit.StreamCreated -= OnPublisherStreamCreated;
                Session.Unpublish(PublisherKit);
            }
            PublisherKit = null;
        }

        private void SetVideoScaleStyle(PublisherKit publisherKit)
            => publisherKit.SetStyle(BaseVideoRenderer.StyleVideoScale, MapVideoScaleStyle(PublisherVideoScaleStyle));

        private void SetVideoScaleStyle(SubscriberKit subscriberKit)
            => subscriberKit.SetStyle(BaseVideoRenderer.StyleVideoScale, MapVideoScaleStyle(SubscriberVideoScaleStyle));

        private string MapVideoScaleStyle(VonageVideoScaleStyle scaleStyle)
            => scaleStyle == VonageVideoScaleStyle.Fill
                ? BaseVideoRenderer.StyleVideoFill
                : BaseVideoRenderer.StyleVideoFit;

        private Publisher.CameraCaptureResolution GetResolution()
        {
            switch (PublisherCameraResolution)
            {
                case VonagePublisherCameraResolution.High: return Publisher.CameraCaptureResolution.High;
                case VonagePublisherCameraResolution.Medium: return Publisher.CameraCaptureResolution.Medium;
                case VonagePublisherCameraResolution.Low: return Publisher.CameraCaptureResolution.Low;
                default: return Publisher.CameraCaptureResolution.High;
            }
        }
    }
}
