using System.Collections.ObjectModel;

namespace Musicthingy.TestModels;

public class UltimateViewModel
{
    public ObservableCollection<UltimatePlaylist> Playlists { get; }

    public UltimatePlaylist SelectedPlaylist { get; set; }

    public UltimateViewModel()
    {
        Playlists = new ObservableCollection<UltimatePlaylist>();

        for (int i = 0; i < 10; i++)
        {
            var p = new UltimatePlaylist
            {
                Name = $"Playlist {i}",
                CreatedAt = DateTime.Now.AddDays(-i)
            };

            for (int j = 0; j < 5; j++)
            {
                p.Items.Add(new UltimateItem
                {
                    Title = $"Track {j}",
                    Tags = new() { "yt", "music", "test" }
                });
            }

            Playlists.Add(p);
        }

        SelectedPlaylist = Playlists.First();
    }
}