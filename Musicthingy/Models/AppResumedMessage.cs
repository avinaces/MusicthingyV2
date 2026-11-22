namespace Musicthingy.Models;

/// <summary>
/// ROUND 12: broadcast by MainActivity.OnResume after a swipe-out (the
/// activity was recreated while the background playback service kept
/// running). Transient UI state that a gesture owned when the app was
/// swiped away never received its Completed/Canceled event, so anything
/// still holding "a gesture is in progress" state resets it here.
/// WeakReferenceMessenger: dead views drop out on their own, this only
/// needs to reach the LIVE tree.
/// </summary>
public class AppResumedMessage
{
}
