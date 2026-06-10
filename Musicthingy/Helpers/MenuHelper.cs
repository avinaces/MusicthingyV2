using Musicthingy.Models;
using Musicthingy.ViewModels;

namespace Musicthingy.Views;

public static class MenuHelper
{

    public static void HideContextMenu(ContextMenuSheet menuSheet)
    {
        if (Services.SheetManager.Host != null)
        {
            _ = Services.SheetManager.Host.HideAsync(menuSheet);
        }
    }
    public static void ShowTrackOptions(PlaylistItem track, MainViewModel vm)
    {
        ContextMenuSheet menuSheet = new ContextMenuSheet
        {
            HasHeader = true,
            HeaderTitle = track.Title,
            HeaderSubtitle = vm.SelectedPlaylist?.Name ?? "Track",
            HeaderImageSource = track.ItemIconSource!
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
                        HideContextMenu(menuSheet);
                        vm.EditTrackCommand.Execute(track);
                    }
                },
                new ContextMenuItem
                {
                    Title = "Play Next",
                    IconSource = "list_video_icon.png",
                    Action = () =>
                    {
                        HideContextMenu(menuSheet);
                        vm.PlayNextInQueue(track);
                    }
                },
                new ContextMenuItem
                {
                    Title = "Add to Queue",
                    IconSource = "list_music_icon.png",
                    Action = () =>
                    {
                        HideContextMenu(menuSheet);
                        vm.AddToPlaybackQueue(track);
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
                        HideContextMenu(menuSheet);
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

    public static void ShowCurrentTrackOptions(PlaylistItem track, MainViewModel vm)
    {
        ContextMenuSheet menuSheet = new ContextMenuSheet
        {
            HasHeader = true,
            HeaderTitle = track.Title,
            HeaderSubtitle = vm.SelectedPlaylist?.Name ?? "Track",
            HeaderImageSource = track.ItemIconSource!
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
                        HideContextMenu(menuSheet);
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
                        HideContextMenu(menuSheet);
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

    public static void ShowPlaylistOptions(Playlist playlist, MainViewModel vm)
    {
        ContextMenuSheet menuSheet = new ContextMenuSheet
        {
            HasHeader = true,
            HeaderTitle = playlist.Name,
            HeaderSubtitle = $"{playlist.ItemCount} tracks • {playlist.DisplayDuration}",
            HeaderImageSource = playlist.IconImageSource!
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
                        HideContextMenu(menuSheet);
                        vm.EditPlaylistCommand.Execute(playlist);
                    }
                },
                new ContextMenuItem
                {
                    Title = "Play Next",
                    IconSource = "list_video_icon.png",
                    Action = () =>
                    {
                        HideContextMenu(menuSheet);
                        vm.PlayNextInQueue(playlist);
                    }
                },
                new ContextMenuItem
                {
                    Title = "Add to Queue",
                    IconSource = "list_music_icon.png",
                    Action = () =>
                    {
                        HideContextMenu(menuSheet);
                        vm.AddToPlaybackQueue(playlist);
                    }
                }
            },
            new ContextMenuGroup
            {
                new ContextMenuItem
                {
                    Title = "Export Playlist",
                    IconSource = "upload_icon.png",
                    Action = () =>
                    {
                        HideContextMenu(menuSheet);
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
                        HideContextMenu(menuSheet);
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

    public static void ShowCurrentPlaylistOptions(Playlist playlist, MainViewModel vm)
    {
        ShowPlaylistOptions(playlist, vm); // For now, the options are the same as regular playlists
    }
}