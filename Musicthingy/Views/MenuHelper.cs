using Microsoft.Maui.Controls;
using Musicthingy.Models;
using Musicthingy.ViewModels;
using System.Collections.Generic;

namespace Musicthingy.Views;

public static class MenuHelper
{
    public static void ShowCurrentTrackOptions(PlaylistItem track, MainViewModel vm)
    {
        ContextMenuSheet menuSheet = new ContextMenuSheet
        {
            HasHeader = true,
            HeaderTitle = track.Title,
            HeaderSubtitle = vm.SelectedPlaylist?.Name ?? "Track",
            HeaderImageSource = track.ItemIconSource
        };

        menuSheet.MenuItems = new List<ContextMenuItem>
        {
            new ContextMenuItem
            {
                Title = "Edit Track",
                IconSource = "pencil_icon.png",
                Action = async () =>
                {
                    _ = Services.SheetManager.Host!.HideAsync(menuSheet);
                    vm.EditTrackCommand.Execute(track);
                }
            },
            new ContextMenuItem
            {
                Title = "Delete from Playlist",
                IconSource = "trash_2_icon.png",
                IsDestructive = true,
                Action = async () =>
                {
                    _ = Services.SheetManager.Host!.HideAsync(menuSheet);
                    vm.DeleteTrackCommand.Execute(track);
                }
            }
        };

        if (Services.SheetManager.Host != null)
        {
            _ = Services.SheetManager.Host.ShowAsync(menuSheet);
        }
    }
}