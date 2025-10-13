using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Weather;
using JetBrains.Annotations;
using YamlDotNet.Serialization;

namespace RandomWeatherPlugin;

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class RandomWeatherConfiguration : IValidateConfiguration<RandomWeatherConfigurationValidator>
{
    public Dictionary<WeatherFxType, float> WeatherWeights { get; init; } = new();

    public Dictionary<WeatherFxType, string> WeatherEmojis { get; init; } = new();

    public int MinWeatherDurationMinutes { get; set; } = 5;
    public int MaxWeatherDurationMinutes { get; set; } = 30;

    public int MinTransitionDurationSeconds { get; set; } = 120;
    public int MaxTransitionDurationSeconds { get; set; } = 600;

    // Weather forecast announcement options
    public bool AnnounceWeatherChanges { get; set; } = true;
    public bool UseNaturalLanguage { get; set; } = true;

    // Weather transition message templates
    public string SevereWeatherTemplate { get; set; } = "Weather alert: {FromWeather} will transition to {ToWeather} in approximately {TransitionTime}.";
    public string ClearingWeatherTemplate { get; set; } = "Good news: {FromWeather} is clearing to {ToWeather} over {TransitionTime}.";
    public string IncomingPrecipitationTemplate { get; set; } = "Weather forecast: {FromWeather} will give way to {ToWeather} over {TransitionTime}.";
    public string PrecipitationChangeTemplate { get; set; } = "Weather update: {FromWeather} will transition to {ToWeather} over {TransitionTime}.";
    public string DefaultWeatherTemplate { get; set; } = "Weather forecast: {FromWeather} will transition to {ToWeather} over {TransitionTime}.";
    public string ContinuingWeatherTemplate { get; set; } = "Weather forecast: {FromWeather} are forecast to continue for at least {DurationTime}.";

    [YamlIgnore] public int MinWeatherDurationMilliseconds => MinWeatherDurationMinutes * 60_000;
    [YamlIgnore] public int MaxWeatherDurationMilliseconds => MaxWeatherDurationMinutes * 60_000;
    [YamlIgnore] public int MinTransitionDurationMilliseconds => MinTransitionDurationSeconds * 1_000;
    [YamlIgnore] public int MaxTransitionDurationMilliseconds => MaxTransitionDurationSeconds * 1_000;
}
