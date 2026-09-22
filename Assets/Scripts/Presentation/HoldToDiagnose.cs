using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Fires a callback when a UI element is held down for <see cref="HoldSeconds"/>.
///
/// Used on the vibration row in settings to launch the haptics self-test. Deliberately a long
/// press rather than a visible button: it is a diagnostic for testers, not a feature, and it must
/// not be discoverable enough that an ordinary player triggers it by accident. Anyone who needs it
/// can be told "hold the vibration row for a second".
///
/// The press is cancelled on pointer-up or on the finger leaving the element, so a normal tap
/// still toggles the setting exactly as before.
/// </summary>
public class HoldToDiagnose : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    public const float HoldSeconds = 1.1f;

    public System.Action OnHeld;

    private float _downAt = -1f;
    private bool _fired;

    /// <summary>
    /// True from the moment the hold fires until the next press begins. uGUI's Button raises
    /// onClick on the pointer-up that ends the hold as well, so the row's click handler checks this
    /// and ignores that release — otherwise every diagnostic also toggled the setting it sits on.
    /// </summary>
    public bool FiredThisPress => _fired;

    public void OnPointerDown(PointerEventData e) { _downAt = Time.unscaledTime; _fired = false; }
    public void OnPointerUp(PointerEventData e) { _downAt = -1f; }
    public void OnPointerExit(PointerEventData e) { _downAt = -1f; }

    private void Update()
    {
        if (_downAt < 0f || _fired) return;
        if (Time.unscaledTime - _downAt < HoldSeconds) return;

        _fired = true;      // one shot per press, or a held finger would re-trigger every frame
        if (OnHeld != null) OnHeld();
    }
}
