using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Http.Responses;
using AssettoServer.Utils;
using FluentValidation;
using Newtonsoft.Json;
using Serilog;
using YamlDotNet.Serialization;

namespace AssettoServer.Server.Configuration;

public class ACServerConfiguration
{
    public ServerConfiguration Server { get; }
    public EntryList EntryList { get; }
    public List<SessionConfiguration> Sessions { get; }
    [YamlIgnore] public string FullTrackName { get; }
    [YamlIgnore] public CSPTrackOptions CSPTrackOptions { get; }
    [YamlIgnore] public string WelcomeMessage { get; private set; } = "";
    [YamlIgnore] public string ServerDescription { get; private set; } = "";
    public ACExtraConfiguration Extra { get; private set; } = new();
    [YamlIgnore] public CMContentConfiguration? ContentConfiguration { get; private set; }
    public string ServerVersion { get; }
    [YamlIgnore] public string? CSPExtraOptions { get; }
    [YamlIgnore] public string BaseFolder { get; }
    [YamlIgnore] public bool LoadPluginsFromWorkdir { get; }
    [YamlIgnore] public int RandomSeed { get; } = Random.Shared.Next();
    
    /*
     * Search paths are like this:
     *
     * When no options are set, all config files must be located in "cfg/".
     * WELCOME_MESSAGE path is relative to the working directory of the server.
     *
     * When "preset" is set, all configs must be located in "presets/<preset>/".
     * WELCOME_MESSAGE path must be relative to the preset folder.
     *
     * When "serverCfgPath" is set, server_cfg.ini will be loaded from the specified path.
     * All other configs must be located in the same folder.
     *
     * When "entryListPath" is set, it takes precedence and entry_list.ini will be loaded from the specified path.
     */
    public ACServerConfiguration(string preset, string serverCfgPath, string entryListPath, bool loadPluginsFromWorkdir)
    {
        BaseFolder = string.IsNullOrEmpty(preset) ? "cfg" : Path.Join("presets", preset);
        LoadPluginsFromWorkdir = loadPluginsFromWorkdir;

        if (string.IsNullOrEmpty(entryListPath))
        {
            entryListPath = Path.Join(BaseFolder, "entry_list.ini");
        }

        if (string.IsNullOrEmpty(serverCfgPath))
        {
            serverCfgPath = Path.Join(BaseFolder, "server_cfg.ini");
        }
        else
        {
            BaseFolder = Path.GetDirectoryName(serverCfgPath)!;
        }
        
        Log.Debug("Loading server_cfg.ini from {Path}", serverCfgPath);
        Server = ServerConfiguration.FromFile(serverCfgPath);
        
        Log.Debug("Loading entry_list.ini from {Path}", entryListPath);
        EntryList = EntryList.FromFile(entryListPath);
        
        ServerVersion = ThisAssembly.AssemblyInformationalVersion;
        FullTrackName = string.IsNullOrEmpty(Server.TrackConfig) ? Server.Track : Server.Track + "-" + Server.TrackConfig;
        CSPTrackOptions = CSPTrackOptions.Parse(Server.Track);

        string welcomeMessagePath = string.IsNullOrEmpty(preset) ? Server.WelcomeMessagePath : Path.Join(BaseFolder, Server.WelcomeMessagePath);
        if (File.Exists(welcomeMessagePath))
        {
            WelcomeMessage = File.ReadAllText(welcomeMessagePath);
        }
        else if(!string.IsNullOrEmpty(welcomeMessagePath))
        {
            Log.Warning("Welcome message not found at {Path}", Path.GetFullPath(welcomeMessagePath));
        }

        string cspExtraOptionsPath = Path.Join(BaseFolder, "csp_extra_options.ini"); 
        if (File.Exists(cspExtraOptionsPath))
        {
            CSPExtraOptions = File.ReadAllText(cspExtraOptionsPath);
        }

        var sessions = new List<SessionConfiguration>();

        if (Server.Practice != null)
        {
            Server.Practice.Id = 0;
            Server.Practice.Type = SessionType.Practice;
            sessions.Add(Server.Practice);
        }

        if (Server.Qualify != null)
        {
            Server.Qualify.Id = 1;
            Server.Qualify.Type = SessionType.Qualifying;
            sessions.Add(Server.Qualify);
        }

        if (Server.Race != null)
        {
            Server.Race.Id = 2;
            Server.Race.Type = SessionType.Race;
            sessions.Add(Server.Race);
        }

        Sessions = sessions;

        LoadExtraConfig();

        var validator = new ACServerConfigurationValidator();
        validator.ValidateAndThrow(this);
    }

    private void LoadExtraConfig() {
        var extraCfgPath = Path.Join(BaseFolder, "extra_cfg.yml");
        Log.Debug("Loading extra_cfg.yml from {Path}", extraCfgPath);
        
        if (!File.Exists(extraCfgPath))
        {
            var cfg = new ACExtraConfiguration();
            cfg.ToFile(extraCfgPath);
        }
        
        Extra = ACExtraConfiguration.FromFile(extraCfgPath);

        if (!string.IsNullOrEmpty(Extra.ServerDescriptionPath))
        {
            string serverDescriptionPath = Path.GetFullPath(Extra.ServerDescriptionPath);
            if (File.Exists(serverDescriptionPath))
            {
                ServerDescription = File.ReadAllText(serverDescriptionPath);
                Log.Debug("Loaded server description from {Path}", serverDescriptionPath);
            }
            else
            {
                Log.Warning("Server description file not found at {Path}", serverDescriptionPath);
            }
        }

        if (Regex.IsMatch(Server.Name, @"x:\w+$"))
        {
            const string errorMsg =
                "Server details are configured via ID in server name. This interferes with native AssettoServer server details. More info: https://assettoserver.org/docs/common-configuration-errors#wrong-server-details";
            if (Extra.IgnoreConfigurationErrors.WrongServerDetails)
            {
                Log.Warning(errorMsg);
            }
            else
            {
                throw new ConfigurationException(errorMsg) { HelpLink = "https://assettoserver.org/docs/common-configuration-errors#wrong-server-details" };
            }
        }

        if (Extra.EnableServerDetails)
        {
            string cmContentPath = Path.Join(BaseFolder, "cm_content/content.json");
            if (File.Exists(cmContentPath))
            {
                ContentConfiguration = JsonConvert.DeserializeObject<CMContentConfiguration>(File.ReadAllText(cmContentPath));
            }
        }
    }

    private (PropertyInfo? Property, object Parent) GetNestedProperty(string key)
    {
        string[] path = key.Split('.');
            
        object parent = this;
        PropertyInfo? propertyInfo = null;

        foreach (string property in path)
        {
            propertyInfo = parent.GetType().GetProperty(property);
            if (propertyInfo == null) continue;
                
            var propertyType = propertyInfo.PropertyType;
            if (!propertyType.IsPrimitive && !propertyType.IsEnum && propertyType != typeof(string))
            {
                parent = propertyInfo.GetValue(parent)!;
            }
        }

        return (propertyInfo, parent);
    }

    /// <summary>
    /// Updates the server description content. This is used by the ServerDescriptionWatcher
    /// to dynamically reload the server description from the file.
    /// </summary>
    /// <param name="newServerDescription">The new server description content</param>
    public void UpdateServerDescription(string newServerDescription)
    {
        ServerDescription = newServerDescription;
        Log.Debug("Server description updated, new length: {Length} characters", newServerDescription.Length);
    }

    /// <summary>
    /// Updates the welcome message content. This is used by the WelcomeMessageWatcher
    /// to dynamically reload the welcome message from the file.
    /// </summary>
    /// <param name="newWelcomeMessage">The new welcome message content</param>
    public void UpdateWelcomeMessage(string newWelcomeMessage)
    {
        WelcomeMessage = newWelcomeMessage;
        Log.Debug("Welcome message updated, new length: {Length} characters", newWelcomeMessage.Length);
    }

    public bool SetProperty(string key, string value)
    {
        (var propertyInfo, object? parent) = GetNestedProperty(key);

        if (propertyInfo == null)
            throw new ConfigurationException($"Could not find property {key}");

        bool ret = false;
        try
        {
            ret = propertyInfo.SetValueFromString(parent, value);
        }
        catch (TargetInvocationException) { }

        return ret;
    }
}
