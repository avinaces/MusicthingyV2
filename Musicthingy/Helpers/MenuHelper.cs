using Microsoft.Maui.Controls;
using Musicthingy.Models;
using Musicthingy.ViewModels;
using System.Collections.Generic;

namespace Musicthingy.Views;

public static class MenuHelper
{
    public static void ShowTrackOptions(PlaylistItem track, MainViewModel vm)
    {
        ContextMenuSheet menuSheet = new ContextMenuSheet
        {
            HasHeader = true,
            HeaderTitle = track.Title,
            HeaderSubtitle = vm.SelectedPlaylist?.Name ?? "Track",
            HeaderImageSource = track.ItemIconSource
        };

        // Switch to MenuGroups to separate the destructive action into its own card
        menuSheet.MenuGroups = new List<ContextMenuGroup>
        {
            // First group: Safe actions
            new ContextMenuGroup
            {
                new ContextMenuItem
                {
                    Title = "Edit Track",
                    IconSource = "pencil_icon.png",
                    Action = () =>
                    {
                        _ = Services.SheetManager.Host!.HideAsync(menuSheet);
                        vm.EditTrackCommand.Execute(track);
                    }
                }
            },
            // Second group: Destructive actions isolated
            new ContextMenuGroup
            {
                new ContextMenuItem
                {
                    Title = "Delete from Playlist",
                    IconSource = "trash_2_icon.png",
                    IsDestructive = true,
                    Action = () =>
                    {
                        _ = Services.SheetManager.Host!.HideAsync(menuSheet);
                        vm.DeleteTrackCommand.Execute(track);
                    }
                }
            }
        };

        if (Services.SheetManager.Host != null)
        {
            _ = Services.SheetManager.Host.ShowAsync(menuSheet);
        }
    }

    /// <summary>
    /// Separate method for the currently playing track's options.
    /// Currently mirrors ShowTrackOptions, but allows divergence later.
    /// </summary>
    public static void ShowCurrentTrackOptions(PlaylistItem track, MainViewModel vm)
    {
        ShowTrackOptions(track, vm);
    }

    public static void ShowPlaylistOptions(Playlist playlist, MainViewModel vm)
    {
        ContextMenuSheet menuSheet = new ContextMenuSheet
        {
            HasHeader = true,
            HeaderTitle = playlist.Name,
            HeaderSubtitle = $"{playlist.ItemCount} tracks • {playlist.DisplayDuration}",
            HeaderImageSource = playlist.IconImageSource
        };

        menuSheet.MenuGroups = new List<ContextMenuGroup>
        {
            // First group: Safe actions
            new ContextMenuGroup
            {
                new ContextMenuItem
                {
                    Title = "Edit Playlist",
                    IconSource = "pencil_icon.png",
                    Action = () =>
                    {
                        _ = Services.SheetManager.Host!.HideAsync(menuSheet);
                        vm.EditPlaylistCommand.Execute(playlist);
                    }
                },
                new ContextMenuItem
                {
                    Title = "Export Playlist",
                    IconSource = "upload_icon.png",
                    Action = () =>
                    {
                        _ = Services.SheetManager.Host!.HideAsync(menuSheet);
                        vm.ExportPlaylistCommand.Execute(playlist);
                    }
                }
            },
            // Second group: Destructive actions isolated
            new ContextMenuGroup
            {
                new ContextMenuItem
                {
                    Title = "Delete Playlist",
                    IconSource = "trash_2_icon.png",
                    IsDestructive = true,
                    Action = () =>
                    {
                        _ = Services.SheetManager.Host!.HideAsync(menuSheet);
                        vm.DeletePlaylistCommand.Execute(playlist);
                    }
                }
            }
        };

        if (Services.SheetManager.Host != null)
        {
            _ = Services.SheetManager.Host.ShowAsync(menuSheet);
        }
    }

    /// <summary>
    /// Separate method for Current Playlist options. Currently mirrors ShowPlaylistOptions, 
    /// but allows divergence later (e.g. preventing deletion of the currently playing playlist).
    /// </summary>
    public static void ShowCurrentPlaylistOptions(Playlist playlist, MainViewModel vm)
    {
        ShowPlaylistOptions(playlist, vm);
    }
}