using MediaServiceReference;
using System.ServiceModel.Channels;
using System.ServiceModel;
using MediaServiceReference1;
using System;
using PtzServiceReference;

namespace OnvifLib
{
  public class MediaService2 : MediaService
  {
    private Media2Client? _mediaClient2;
    private List<MediaProfile> _profiles = new();
    private readonly Dictionary<string, string> _streamUriCache = new();

    public MediaService2(
      string url,
      CustomBinding binding,
      string username,
      string password,
      string profile,
      Func<SecurityToken>? tokenFactory = null,
      IOnvifLogger? logger = null) : base(url, binding, username, password, profile, tokenFactory, logger)
    {
    }

    protected override async Task InitializeAsync()
    {
      await base.InitializeAsync();

      _mediaClient2 = _onvifClientFactory.CreateClient<Media2Client, MediaServiceReference.Media2>(
        new EndpointAddress(_url),
        _binding,
        _username,
        _password);
      await _mediaClient2.OpenAsync();

      var request = new MediaServiceReference.GetProfilesRequest { Type = ["All"] };
      var profilesResponse = await _mediaClient2.GetProfilesAsync(request);

      _profiles = profilesResponse.Profiles.ToList();
    }

    public override async Task<string> GetStreamUri(string profile_token)
    {
      if (_streamUriCache.TryGetValue(profile_token, out var cached))
        return cached;

      if (_mediaClient2 == null)
        return string.Empty;

      var profile = _profiles.FirstOrDefault(p => p.token == profile_token);
      if (profile == null)
        return string.Empty;

      var streamUriRequest = new GetStreamUriRequest
      {
        // Media2 GetStreamUri.Protocol is a transport value ("RtspUnicast", "RtspMulticast",
        // "RTSP", "RtspOverHttp") — not the Media1 StreamType enum ("RTPUnicast").
        Protocol = "RtspUnicast",
        ProfileToken = profile.token
      };
      var streamResponse = await _mediaClient2.GetStreamUriAsync(streamUriRequest);
      var uri = streamResponse.Uri ?? string.Empty;
      if (!string.IsNullOrEmpty(uri))
        _streamUriCache[profile_token] = uri;
      return uri;
    }
    public override List<OnvifProfileInfo> GetProfiles()
    {
      return _profiles.Select(p => new OnvifProfileInfo(
        Token: p.token,
        Name: p.Name ?? p.token,
        Width: p.Configurations?.VideoEncoder?.Resolution?.Width ?? 0,
        Height: p.Configurations?.VideoEncoder?.Resolution?.Height ?? 0,
        Encoding: p.Configurations?.VideoEncoder?.Encoding ?? string.Empty
      )).ToList();
    }
    public override async Task<List<OnvifVideoEncoderConfig>> GetVideoEncoderConfigsAsync()
    {
      if (_mediaClient2 == null) return [];
      var resp = await _mediaClient2.GetVideoEncoderConfigurationsAsync(
        new MediaServiceReference.GetVideoEncoderConfigurationsRequest());
      return (resp.Configurations ?? []).Select(c => new OnvifVideoEncoderConfig(
        Token:          c.token         ?? string.Empty,
        Name:           c.Name          ?? string.Empty,
        Encoding:       c.Encoding      ?? string.Empty,
        Width:          c.Resolution?.Width  ?? 0,
        Height:         c.Resolution?.Height ?? 0,
        FrameRateLimit: (int)Math.Round(c.RateControl?.FrameRateLimit ?? 0),
        BitrateLimit:   c.RateControl?.BitrateLimit ?? 0,
        GovLength:      c.GovLengthSpecified ? c.GovLength : 0,
        H264Profile:    c.Profile ?? string.Empty,
        Quality:        c.Quality
      )).ToList();
    }

    public override async Task<List<OnvifVideoEncoderOptions>> GetVideoEncoderConfigOptionsAsync(string configToken)
    {
      if (_mediaClient2 == null) return [];
      var resp = await _mediaClient2.GetVideoEncoderConfigurationOptionsAsync(
        new MediaServiceReference.GetVideoEncoderConfigurationOptionsRequest
        {
          ConfigurationToken = configToken,
        });
      return (resp.Options ?? []).Select(o => {
        var resolutions = (o.ResolutionsAvailable ?? [])
          .Select(r => new OnvifResolutionOption(r.Width, r.Height)).ToList();
        // GovLengthRange is int[] — [min, max]
        var govRange   = o.GovLengthRange ?? [];
        var rates      = o.FrameRatesSupported ?? [];
        return new OnvifVideoEncoderOptions(
          Encoding:     o.Encoding ?? string.Empty,
          Resolutions:  resolutions,
          MinFrameRate: rates.Length > 0 ? (int)Math.Round(rates.Min()) : 0,
          MaxFrameRate: rates.Length > 0 ? (int)Math.Round(rates.Max()) : 0,
          MinBitrate:   o.BitrateRange?.Min ?? 0,
          MaxBitrate:   o.BitrateRange?.Max ?? 0,
          MinGovLength: govRange.Length > 0 ? govRange.Min() : 0,
          MaxGovLength: govRange.Length > 0 ? govRange.Max() : 0,
          H264Profiles: (o.ProfilesSupported ?? []).ToList()
        );
      }).ToList();
    }

    public override async Task SetVideoEncoderConfigAsync(OnvifVideoEncoderConfig config)
    {
      if (_mediaClient2 == null) return;

      // Fetch existing to preserve unmanaged fields
      var resp = await _mediaClient2.GetVideoEncoderConfigurationsAsync(
        new MediaServiceReference.GetVideoEncoderConfigurationsRequest());
      var existing = (resp.Configurations ?? []).FirstOrDefault(c => c.token == config.Token)
        ?? throw new InvalidOperationException($"VideoEncoderConfiguration '{config.Token}' not found on camera");

      existing.Resolution = new MediaServiceReference.VideoResolution2 { Width = config.Width, Height = config.Height };

      if (existing.RateControl != null)
      {
        existing.RateControl.FrameRateLimit = config.FrameRateLimit;
        existing.RateControl.BitrateLimit   = config.BitrateLimit;
      }

      if (config.GovLength > 0)
      {
        existing.GovLength          = config.GovLength;
        existing.GovLengthSpecified = true;
      }

      if (!string.IsNullOrEmpty(config.H264Profile))
        existing.Profile = config.H264Profile;

      await _mediaClient2.SetVideoEncoderConfigurationAsync(
        new MediaServiceReference.SetVideoEncoderConfigurationRequest { Configuration = existing });
    }

    public override async Task<List<OnvifAudioEncoderConfig>> GetAudioEncoderConfigsAsync()
    {
      if (_mediaClient2 == null) return [];
      var resp = await _mediaClient2.GetAudioEncoderConfigurationsAsync(
        new MediaServiceReference.GetAudioEncoderConfigurationsRequest());
      return (resp.Configurations ?? []).Select(c => new OnvifAudioEncoderConfig(
        Token:      c.token      ?? string.Empty,
        Name:       c.Name       ?? string.Empty,
        Encoding:   c.Encoding   ?? string.Empty,
        Bitrate:    c.Bitrate,
        SampleRate: c.SampleRate
      )).ToList();
    }

    public override async Task<List<OnvifAudioEncoderOption>> GetAudioEncoderConfigOptionsAsync(string configToken)
    {
      if (_mediaClient2 == null) return [];
      var resp = await _mediaClient2.GetAudioEncoderConfigurationOptionsAsync(
        new MediaServiceReference.GetAudioEncoderConfigurationOptionsRequest
        {
          ConfigurationToken = configToken,
        });
      return (resp.Options ?? []).Select(o => new OnvifAudioEncoderOption(
        Encoding:    o.Encoding    ?? string.Empty,
        Bitrates:    (o.BitrateList    ?? []).ToList(),
        SampleRates: (o.SampleRateList ?? []).ToList()
      )).ToList();
    }

    public override async Task SetAudioEncoderConfigAsync(OnvifAudioEncoderConfig config)
    {
      if (_mediaClient2 == null) return;
      var resp = await _mediaClient2.GetAudioEncoderConfigurationsAsync(
        new MediaServiceReference.GetAudioEncoderConfigurationsRequest());
      var existing = (resp.Configurations ?? []).FirstOrDefault(c => c.token == config.Token)
        ?? throw new InvalidOperationException($"AudioEncoderConfiguration '{config.Token}' not found on camera");
      existing.Encoding   = config.Encoding;
      existing.Bitrate    = config.Bitrate;
      existing.SampleRate = config.SampleRate;
      await _mediaClient2.SetAudioEncoderConfigurationAsync(
        new MediaServiceReference.SetAudioEncoderConfigurationRequest { Configuration = existing });
    }

    public override async Task<ImageResult?> GetImage()
    {
      var profile = _profiles.FirstOrDefault();
      if (profile == null || _mediaClient2 == null)
        return null;

      var snapShotUriRequest = new GetSnapshotUriRequest
      {
        ProfileToken = profile.token
      };

      try
      {
        var snapShotUriResponse = await _mediaClient2.GetSnapshotUriAsync(snapShotUriRequest);
        var data = await DownloadImageAsync(snapShotUriResponse.Uri, _username, _password, _logger);
        return data;
      }
      catch (Exception ex)
      {
        _logger?.Error($"ONVIF GetImage (media2) failed for {_url}: {ex}");
        return null;
      }
    }
  }
}
