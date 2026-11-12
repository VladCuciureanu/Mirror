using CommunityToolkit.Mvvm.ComponentModel;

using Mirror.Framework.Models;

namespace Mirror.ViewModels.Settings;

public partial class ConnectionsSettingsViewModel : ViewModelBase
{
    /* Off unless turned on: a viewer's backend has no reason to announce itself on Discord. */
    [ObservableProperty] private bool _useDiscordRichPresence = false;
    [ObservableProperty] private bool _syncFModel = false;

    partial void OnUseDiscordRichPresenceChanged(bool value)
    {
        if (value)
        {
            Discord.Initialize();
        }
        else
        {
            Discord.Deinitialize();
        }
    }
}
