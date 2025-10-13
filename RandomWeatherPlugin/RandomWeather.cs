using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Server.Plugin;
using AssettoServer.Server.Weather;
using AssettoServer.Shared.Network.Packets.Shared;
using AssettoServer.Shared.Services;
using AssettoServer.Shared.Weather;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace RandomWeatherPlugin;

public class RandomWeather : CriticalBackgroundService, IAssettoServerAutostart
{
    private struct WeatherWeight
    {
        internal WeatherFxType Weather { get; init; }
        internal float PrefixSum { get; init; }
    }

    private readonly WeatherManager _weatherManager;
    private readonly IWeatherTypeProvider _weatherTypeProvider;
    private readonly EntryCarManager _entryCarManager;
    private readonly RandomWeatherConfiguration _configuration;
    private readonly List<WeatherWeight> _weathers = new();

    // Weather display name mapping for natural language
    private static readonly Dictionary<WeatherFxType, string> WeatherDisplayNames = new()
    {
        { WeatherFxType.LightThunderstorm, "light thunderstorms" },
        { WeatherFxType.Thunderstorm, "thunderstorms" },
        { WeatherFxType.HeavyThunderstorm, "heavy thunderstorms" },
        { WeatherFxType.LightDrizzle, "light drizzle" },
        { WeatherFxType.Drizzle, "drizzle" },
        { WeatherFxType.HeavyDrizzle, "heavy drizzle" },
        { WeatherFxType.LightRain, "light rain" },
        { WeatherFxType.Rain, "rain" },
        { WeatherFxType.HeavyRain, "heavy rain" },
        { WeatherFxType.LightSnow, "light snow" },
        { WeatherFxType.Snow, "snow" },
        { WeatherFxType.HeavySnow, "heavy snow" },
        { WeatherFxType.LightSleet, "light sleet" },
        { WeatherFxType.Sleet, "sleet" },
        { WeatherFxType.HeavySleet, "heavy sleet" },
        { WeatherFxType.Clear, "clear skies" },
        { WeatherFxType.FewClouds, "few clouds" },
        { WeatherFxType.ScatteredClouds, "scattered clouds" },
        { WeatherFxType.BrokenClouds, "broken clouds" },
        { WeatherFxType.OvercastClouds, "overcast skies" },
        { WeatherFxType.Fog, "fog" },
        { WeatherFxType.Mist, "mist" },
        { WeatherFxType.Smoke, "smoke" },
        { WeatherFxType.Haze, "haze" },
        { WeatherFxType.Sand, "sand" },
        { WeatherFxType.Dust, "dust" },
        { WeatherFxType.Squalls, "squalls" },
        { WeatherFxType.Tornado, "tornado conditions" },
        { WeatherFxType.Hurricane, "hurricane conditions" },
        { WeatherFxType.Cold, "cold weather" },
        { WeatherFxType.Hot, "hot weather" },
        { WeatherFxType.Windy, "windy conditions" },
        { WeatherFxType.Hail, "hail" }
    };

    public RandomWeather(RandomWeatherConfiguration configuration, WeatherManager weatherManager, IWeatherTypeProvider weatherTypeProvider, EntryCarManager entryCarManager, IHostApplicationLifetime applicationLifetime) : base(applicationLifetime)
    {
        _configuration = configuration;
        _weatherManager = weatherManager;
        _weatherTypeProvider = weatherTypeProvider;
        _entryCarManager = entryCarManager;

        foreach (var weather in Enum.GetValues<WeatherFxType>())
        {
            _configuration.WeatherWeights.TryAdd(weather, 1.0f);
            _configuration.WeatherEmojis.TryAdd(weather, "🌤️"); // Default emoji
        }

        _configuration.WeatherWeights[WeatherFxType.None] = 0;

        float weightSum = _configuration.WeatherWeights
            .Select(w => w.Value)
            .Sum();

        float prefixSum = 0.0f;
        foreach (var (weather, weight) in _configuration.WeatherWeights)
        {
            if (weight > 0)
            {
                prefixSum += weight / weightSum;
                _weathers.Add(new WeatherWeight
                {
                    Weather = weather,
                    PrefixSum = prefixSum,
                });
            }
        }

        _weathers.Sort((a, b) =>
        {
            if (a.PrefixSum < b.PrefixSum)
                return -1;
            if (a.PrefixSum > b.PrefixSum)
                return 1;
            return 0;
        });
    }

    private WeatherFxType PickRandom()
    {
        float rng = Random.Shared.NextSingle();
        WeatherFxType weather = WeatherFxType.None;

        int begin = 0, end = _weathers.Count;
        while (begin <= end)
        {
            int i = (begin + end) / 2;

            if (_weathers[i].PrefixSum <= rng)
            {
                begin = i + 1;
            }
            else
            {
                end = i - 1;
                weather = _weathers[i].Weather;
            }
        }

        return weather;
    }

    private static bool IsPrecipitation(WeatherFxType weather)
    {
        return weather switch
        {
            WeatherFxType.LightThunderstorm or WeatherFxType.Thunderstorm or WeatherFxType.HeavyThunderstorm or
            WeatherFxType.LightDrizzle or WeatherFxType.Drizzle or WeatherFxType.HeavyDrizzle or
            WeatherFxType.LightRain or WeatherFxType.Rain or WeatherFxType.HeavyRain or
            WeatherFxType.LightSnow or WeatherFxType.Snow or WeatherFxType.HeavySnow or
            WeatherFxType.LightSleet or WeatherFxType.Sleet or WeatherFxType.HeavySleet or
            WeatherFxType.Hail => true,
            _ => false
        };
    }

    private static bool IsClearWeather(WeatherFxType weather)
    {
        return weather switch
        {
            WeatherFxType.Clear or WeatherFxType.FewClouds or WeatherFxType.ScatteredClouds or WeatherFxType.BrokenClouds => true,
            _ => false
        };
    }

    private static bool IsSevereWeather(WeatherFxType weather)
    {
        return weather switch
        {
            WeatherFxType.HeavyThunderstorm or WeatherFxType.HeavyRain or WeatherFxType.HeavySnow or
            WeatherFxType.Tornado or WeatherFxType.Hurricane or WeatherFxType.Squalls => true,
            _ => false
        };
    }

    private string GetForecastMessage(WeatherFxType from, WeatherFxType to, int transitionSeconds, int weatherDurationSeconds)
    {
        if (!_configuration.UseNaturalLanguage)
        {
            // Simple template similar to log output, no timing
            return $"Weather transitioning to {to}";
        }

        var fromEmoji = _configuration.WeatherEmojis[from];
        var toEmoji = _configuration.WeatherEmojis[to];
        var fromName = WeatherDisplayNames[from];
        var toName = WeatherDisplayNames[to];

        // Handle same weather transitions with duration-based messaging
        if (from == to)
        {
            var durationMinutes = Math.Round(weatherDurationSeconds / 60.0);
            var durationTime = durationMinutes == 1 ? "a minute" : $"{durationMinutes} minutes";
            var continuingMessage = _configuration.ContinuingWeatherTemplate
                .Replace("{FromWeather}", fromName)
                .Replace("{DurationTime}", durationTime);
            return $"{fromEmoji} {continuingMessage}";
        }

        var transitionMinutes = Math.Round(transitionSeconds / 60.0);

        // Fix grammar: "over the next a minute" vs "over the next 3 minutes"
        var transitionTimePhrase = transitionMinutes == 1 ? "a minute" : $"{transitionMinutes} minutes";
        var transitionTimeWithPreposition = transitionMinutes == 1 ? "a minute" : $"the next {transitionMinutes} minutes";

        string message;

        // Intelligent grammar templates based on weather characteristics
        if (IsSevereWeather(to))
        {
            message = _configuration.SevereWeatherTemplate
                .Replace("{FromWeather}", fromName)
                .Replace("{ToWeather}", toName)
                .Replace("{TransitionTime}", transitionTimePhrase);
        }
        else if (IsPrecipitation(from) && IsClearWeather(to))
        {
            message = _configuration.ClearingWeatherTemplate
                .Replace("{FromWeather}", fromName)
                .Replace("{ToWeather}", toName)
                .Replace("{TransitionTime}", transitionTimeWithPreposition);
        }
        else if (IsClearWeather(from) && IsPrecipitation(to))
        {
            message = _configuration.IncomingPrecipitationTemplate
                .Replace("{FromWeather}", fromName)
                .Replace("{ToWeather}", toName)
                .Replace("{TransitionTime}", transitionTimeWithPreposition);
        }
        else if (IsPrecipitation(from) && IsPrecipitation(to))
        {
            message = _configuration.PrecipitationChangeTemplate
                .Replace("{FromWeather}", fromName)
                .Replace("{ToWeather}", toName)
                .Replace("{TransitionTime}", transitionTimeWithPreposition);
        }
        else
        {
            // Default template for all other combinations
            message = _configuration.DefaultWeatherTemplate
                .Replace("{FromWeather}", fromName)
                .Replace("{ToWeather}", toName)
                .Replace("{TransitionTime}", transitionTimeWithPreposition);
        }

        return $"{toEmoji} {message}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int weatherDuration = 1000;
        int transitionDuration = 1000;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                weatherDuration = Random.Shared.Next(_configuration.MinWeatherDurationMilliseconds, _configuration.MaxWeatherDurationMilliseconds);
                transitionDuration = Random.Shared.Next(_configuration.MinTransitionDurationMilliseconds, _configuration.MaxTransitionDurationMilliseconds);

                WeatherType nextWeatherType = _weatherTypeProvider.GetWeatherType(PickRandom());

                var last = _weatherManager.CurrentWeather;

                Log.Information("Random weather transitioning to {WeatherType}, transition duration {TransitionDuration} seconds, weather duration {WeatherDuration} minutes",
                    nextWeatherType.WeatherFxType,
                    Math.Round(transitionDuration / 1000.0f),
                    Math.Round(weatherDuration / 60_000.0f, 1));

                // Announce weather change to server chat
                if (_configuration.AnnounceWeatherChanges && last.Type.WeatherFxType != WeatherFxType.None)
                {
                    var forecastMessage = GetForecastMessage(last.Type.WeatherFxType, nextWeatherType.WeatherFxType, transitionDuration / 1000, weatherDuration / 1000);
                    _entryCarManager.BroadcastPacket(new ChatMessage { SessionId = 255, Message = forecastMessage });
                }
                
                _weatherManager.SetWeather(new WeatherData(last.Type, nextWeatherType)
                {
                    TransitionDuration = transitionDuration,
                    TemperatureAmbient = last.TemperatureAmbient,
                    TemperatureRoad = (float)WeatherUtils.GetRoadTemperature(_weatherManager.CurrentDateTime.TimeOfDay.TickOfDay / 10_000_000.0, last.TemperatureAmbient,
                        nextWeatherType.TemperatureCoefficient),
                    Pressure = last.Pressure,
                    Humidity = nextWeatherType.Humidity,
                    WindSpeed = last.WindSpeed,
                    WindDirection = last.WindDirection,
                    RainIntensity = last.RainIntensity,
                    RainWetness = last.RainWetness,
                    RainWater = last.RainWater,
                    TrackGrip = last.TrackGrip
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during random weather update");
            }
            finally
            {
                await Task.Delay(transitionDuration + weatherDuration, stoppingToken);
            }
        }
    }
}
