using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy;

namespace Musicthingy.ViewModels;

public enum ModalBackgroundTapBehavior
{
	Cancel,
	Confirm,
	Ignore
}

public partial class MainViewModel
{
	[ObservableProperty] public partial bool IsModalVisible { get; set; }
	[ObservableProperty] public partial string ModalTitle { get; set; } = string.Empty;
	[ObservableProperty] public partial string ModalMessage { get; set; } = string.Empty;
	[ObservableProperty] public partial bool IsModalMessageVisible { get; set; }
	[ObservableProperty] public partial string ModalInputText { get; set; } = string.Empty;
	[ObservableProperty] public partial bool IsModalInputVisible { get; set; }
	[ObservableProperty] public partial string ModalConfirmText { get; set; } = "OK";
	[ObservableProperty] public partial Color ModalConfirmColor { get; set; } = ThemeResources.Color("PrimaryButtonColor");
	[ObservableProperty] public partial Color ModalConfirmTextColor { get; set; } = ThemeResources.Color("PrimaryButtonText");

	private ModalBackgroundTapBehavior _backgroundTapBehavior;
	private TaskCompletionSource<(bool IsConfirmed, string Input)>? _modalTcs;

	[RelayCommand]
	private void ModalCancel()
	{
		_modalTcs?.TrySetResult((false, ModalInputText));
		IsModalVisible = false;
	}

	[RelayCommand]
	private void ModalConfirm()
	{
		_modalTcs?.TrySetResult((true, ModalInputText));
		IsModalVisible = false;
	}
	[RelayCommand]
	private void ModalBackgroundTapped()
	{
		switch (_backgroundTapBehavior)
		{
			case ModalBackgroundTapBehavior.Cancel:
				ModalCancel();
				break;
			case ModalBackgroundTapBehavior.Confirm:
				ModalConfirm();
				break;
			case ModalBackgroundTapBehavior.Ignore:
				break; // Do nothing
		}
	}

	// --- REUSABLE AWAITABLE HELPERS ---

	public async Task<bool> ShowConfirmAsync(string title, string message, string confirmText = "Confirm", ModalBackgroundTapBehavior bgBehavior = ModalBackgroundTapBehavior.Cancel, bool isDanger = false)
	{
		ModalTitle = title;
		ModalMessage = message;
		IsModalMessageVisible = true;
		IsModalInputVisible = false;
		ModalConfirmText = confirmText;
		ModalConfirmColor = isDanger ? ThemeResources.Color("DangerFill") : ThemeResources.Color("PrimaryButtonColor");
		ModalConfirmTextColor = isDanger ? ThemeResources.Color("SecondaryButtonText") : ThemeResources.Color("PrimaryButtonText");
		_backgroundTapBehavior = bgBehavior;

		IsModalVisible = true;
		_modalTcs = new TaskCompletionSource<(bool, string)>();
		var result = await _modalTcs.Task;
		return result.IsConfirmed;
	}

	public async Task<string?> ShowPromptAsync(string title, string defaultText, string confirmText = "Save", ModalBackgroundTapBehavior bgBehavior = ModalBackgroundTapBehavior.Confirm)
	{
		ModalTitle = title;
		ModalInputText = defaultText;
		IsModalMessageVisible = false;
		IsModalInputVisible = true;
		ModalConfirmText = confirmText;
		ModalConfirmColor = ThemeResources.Color("PrimaryButtonColor");
		_backgroundTapBehavior = bgBehavior;

		IsModalVisible = true;
		_modalTcs = new TaskCompletionSource<(bool, string)>();
		var result = await _modalTcs.Task;

		return result.IsConfirmed ? result.Input : null;
	}
}
