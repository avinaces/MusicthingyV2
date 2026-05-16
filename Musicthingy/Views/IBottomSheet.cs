namespace Musicthingy.Views;

public interface IBottomSheet
{
    Action<object?>? CloseAction { get; set; }
}