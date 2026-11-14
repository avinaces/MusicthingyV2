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
        bool isReadOnly = track.Playlist != null && track.Playlist.IsSystemPlaylist;
        bool isCachedTracks = isReadOnly && track.Playlist?.Name == "Cached Tracks";

        ContextMenuSheet menuSheet = new ContextMenuSheet
        {
            HasHeader = true,
            HeaderTitle = track.Title,
            HeaderSubtitle = track.SourcePlaylist?.Name ?? vm.SelectedPlaylist?.Name ?? "Track",
            HeaderImageSource = track.ItemIconSource!
        };

        ContextMenuGroup safeGroup = new ContextMenuGroup();
        ContextMenuGroup dangerGroup = new ContextMenuGroup();

        if (!isReadOnly)
        {
            safeGroup.Add(new ContextMenuItem
            {
                Title = "Edit Track",
                IconSource = "pencil_icon.png",
                Action = () => { HideContextMenu(menuSheet); vm.EditTrackCommand.Execute(track); }
            });
        }

        safeGroup.Add(new ContextMenuItem
        {
            Title = "Play Next",
            IconSource = "list_video_icon.png",
            Action = () => { HideContextMenu(menuSheet); vm.PlayNextInQueue(track); }
        });

        safeGroup.Add(new ContextMenuItem
        {
            Title = "Add to Queue",
            IconSource = "list_music_icon.png",
            Action = () => { HideContextMenu(menuSheet); vm.AddToPlaybackQueue(track); }
        });

        if (!isReadOnly)
        {
            dangerGroup.Add(new ContextMenuItem
            {
                Title = "Delete from Playlist",
                IconSource = "trash_2_icon.png",
                IsDestructive = true,
                Action = () => { HideContextMenu(menuSheet); vm.DeleteTrackCommand.Execute(track); }
            });
        }
        else if (isCachedTracks)
        {
            // System playlists are read-only, but the Cached Tracks rows are a
            // view over the on-disk cache - deleting the file is the only edit
            // that makes sense there. The row disappears on CacheUpdated.
            dangerGroup.Add(new ContextMenuItem
            {
                Title = "Delete from Cache",
                IconSource = "hard_drive_icon.png",
                IsDestructive = true,
                Action = () => { HideContextMenu(menuSheet); vm.RemoveTrackFromCache(track); }
            });
        }

        List<ContextMenuGroup> groups = new List<ContextMenuGroup> { safeGroup };
        if (dangerGroup.Count > 0)
        {
            groups.Add(dangerGroup);
        }

        menuSheet.MenuGroups = groups;

        if (Services.SheetManager.Host != null)
        {
            _ = Services.SheetManager.Host.ShowAsync(menuSheet);
        }
    }

    public static void ShowCurrentTrackOptions(PlaylistItem track, MainViewModel vm)
        => ShowTrackOptions(track, vm);

    public static void ShowPlaylistOptions(Playlist playlist, MainViewModel vm)
    {
        ContextMenuSheet menuSheet = new ContextMenuSheet
        {
            HasHeader = true,
            HeaderTitle = playlist.Name,
            HeaderSubtitle = $"{playlist.ItemCount} tracks • {playlist.DisplayDuration}",
            HeaderImageSource = playlist.IconImageSource!
        };

        ContextMenuGroup safeGroup = new ContextMenuGroup();
        ContextMenuGroup exportGroup = new ContextMenuGroup();
        ContextMenuGroup dangerGroup = new ContextMenuGroup();

        if (!playlist.IsSystemPlaylist)
        {
            safeGroup.Add(new ContextMenuItem
            {
                Title = "Edit Playlist",
                IconSource = "pencil_icon.png",
                Action = () => { HideContextMenu(menuSheet); vm.EditPlaylistCommand.Execute(playlist); }
            });
        }

        safeGroup.Add(new ContextMenuItem
        {
            Title = "Play Next",
            IconSource = "list_video_icon.png",
            Action = () => { HideContextMenu(menuSheet); vm.PlayNextInQueue(playlist); }
        });

        safeGroup.Add(new ContextMenuItem
        {
            Title = "Add to Queue",
            IconSource = "list_music_icon.png",
            Action = () => { HideContextMenu(menuSheet); vm.AddToPlaybackQueue(playlist); }
        });

        if (!playlist.IsSystemPlaylist)
        {
            exportGroup.Add(new ContextMenuItem
            {
                Title = "Export Playlist",
                IconSource = "upload_icon.png",
                Action = () => { HideContextMenu(menuSheet); vm.ExportPlaylistCommand.Execute(playlist); }
            });

            dangerGroup.Add(new ContextMenuItem
            {
                Title = "Delete Playlist",
                IconSource = "trash_2_icon.png",
                IsDestructive = true,
                Action = () => { HideContextMenu(menuSheet); vm.DeletePlaylistCommand.Execute(playlist); }
            });
        }

        List<ContextMenuGroup> groups = new List<ContextMenuGroup> { safeGroup };
        if (exportGroup.Count > 0)
        {
            groups.Add(exportGroup);
        }
        if (dangerGroup.Count > 0)
        {
            groups.Add(dangerGroup);
        }

        menuSheet.MenuGroups = groups;

        if (Services.SheetManager.Host != null)
        {
            _ = Services.SheetManager.Host.ShowAsync(menuSheet);
        }
    }

    public static void ShowCurrentPlaylistOptions(Playlist playlist, MainViewModel vm)
        => ShowPlaylistOptions(playlist, vm);
}