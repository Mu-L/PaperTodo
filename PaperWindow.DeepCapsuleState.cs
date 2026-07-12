using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    // Semantic placement is the single source of truth for whether an edge slot exists and why
    // it may currently be hidden. Collapse-all and hide transitions are states, not side-band
    // booleans layered over a generic "docked" value.
    private enum DeepCapsuleSlotState
    {
        None,
        CollapsedDocked,
        ExpandedReserved,
        RetractedCollapsed,
        RetractedExpanded,
        RetractingCollapsed,
        RetractingExpanded
    }

    private enum DeepCapsuleVisualState
    {
        Resting,
        Hovered,
        Active
    }

    private enum DeepCapsuleGestureState
    {
        Idle,
        PendingClick,
        DockedReordering,
        FloatingReordering
    }

    private enum DeepCapsuleOpenOrigin
    {
        Normal,
        EdgeSlot
    }

    private readonly record struct DeepCapsuleState(
        DeepCapsuleSlotState Slot,
        DeepCapsuleVisualState Visual,
        DeepCapsuleGestureState Gesture,
        DeepCapsuleOpenOrigin OpenOrigin)
    {
        public static DeepCapsuleState Initial => new(
            DeepCapsuleSlotState.None,
            DeepCapsuleVisualState.Resting,
            DeepCapsuleGestureState.Idle,
            DeepCapsuleOpenOrigin.Normal);
    }

    private sealed class DeepCapsuleDragSession
    {
        public DeepCapsuleDragSession(DeviceScreenPoint pointerDownScreenPosition)
        {
            PointerDownScreenPosition = pointerDownScreenPosition;
            LastScreenPosition = pointerDownScreenPosition;
        }

        public DeviceScreenPoint PointerDownScreenPosition { get; }
        public DeviceScreenPoint LastScreenPosition { get; set; }
        public string StartMonitorDeviceName { get; set; } = "";
        public double DockedPointerOffsetY { get; set; }
        public int PreviewIndex { get; set; } = -1;
    }

    private DeepCapsuleState _deepCapsuleState = DeepCapsuleState.Initial;
    private DeepCapsuleDragSession? _deepCapsuleDragSession;

    private DeepCapsuleSlotState DeepCapsuleSlot => _deepCapsuleState.Slot;
    private DeepCapsuleVisualState DeepCapsuleVisual => _deepCapsuleState.Visual;
    private DeepCapsuleGestureState DeepCapsuleGesture => _deepCapsuleState.Gesture;
    private DeepCapsuleOpenOrigin DeepCapsuleOrigin => _deepCapsuleState.OpenOrigin;

    private bool HasDeepCapsuleSlotPlacement => DeepCapsuleSlot != DeepCapsuleSlotState.None;
    private bool HoldsDeepCapsuleSlotWhileExpanded => DeepCapsuleSlot is
        DeepCapsuleSlotState.ExpandedReserved or
        DeepCapsuleSlotState.RetractedExpanded or
        DeepCapsuleSlotState.RetractingExpanded;
    private bool IsDeepCapsuleSlotRetracting => DeepCapsuleSlot is
        DeepCapsuleSlotState.RetractingCollapsed or
        DeepCapsuleSlotState.RetractingExpanded;
    private bool IsDeepCapsuleHovered => DeepCapsuleVisual == DeepCapsuleVisualState.Hovered;
    private bool IsDeepCapsuleSlotActive => DeepCapsuleVisual == DeepCapsuleVisualState.Active;
    private bool IsDeepCapsuleSlotPendingClick => DeepCapsuleGesture == DeepCapsuleGestureState.PendingClick;
    private bool IsDeepCapsuleDockedReordering => DeepCapsuleGesture == DeepCapsuleGestureState.DockedReordering;
    private bool IsDeepCapsuleFloatingReordering => DeepCapsuleGesture == DeepCapsuleGestureState.FloatingReordering;
    private bool IsDeepCapsuleReordering => IsDeepCapsuleDockedReordering || IsDeepCapsuleFloatingReordering;
    private bool ExpandedFromDeepCapsuleEdge => DeepCapsuleOrigin == DeepCapsuleOpenOrigin.EdgeSlot;
    private bool IsDeepCapsuleRetractedIntoMaster => DeepCapsuleSlot is
        DeepCapsuleSlotState.RetractedCollapsed or
        DeepCapsuleSlotState.RetractedExpanded;

    private void SetDeepCapsuleSlotState(
        DeepCapsuleSlotState state,
        [CallerMemberName] string reason = "")
    {
        Debug.Assert(
            CanTransitionDeepCapsuleSlot(DeepCapsuleSlot, state),
            $"Illegal deep-capsule slot transition {DeepCapsuleSlot} -> {state} ({reason}).");

        _deepCapsuleState = _deepCapsuleState with { Slot = state };
        AssertDeepCapsuleStateInvariants();
    }

    private static bool CanTransitionDeepCapsuleSlot(DeepCapsuleSlotState from, DeepCapsuleSlotState to)
    {
        if (from == to)
        {
            return true;
        }

        return from switch
        {
            DeepCapsuleSlotState.None => to is
                DeepCapsuleSlotState.CollapsedDocked or
                DeepCapsuleSlotState.ExpandedReserved or
                DeepCapsuleSlotState.RetractedCollapsed or
                DeepCapsuleSlotState.RetractedExpanded,
            DeepCapsuleSlotState.CollapsedDocked => to is
                DeepCapsuleSlotState.None or
                DeepCapsuleSlotState.ExpandedReserved or
                DeepCapsuleSlotState.RetractedCollapsed or
                DeepCapsuleSlotState.RetractedExpanded or
                DeepCapsuleSlotState.RetractingCollapsed,
            DeepCapsuleSlotState.ExpandedReserved => to is
                DeepCapsuleSlotState.None or
                DeepCapsuleSlotState.CollapsedDocked or
                DeepCapsuleSlotState.RetractedCollapsed or
                DeepCapsuleSlotState.RetractedExpanded or
                DeepCapsuleSlotState.RetractingExpanded,
            DeepCapsuleSlotState.RetractedCollapsed => to is
                DeepCapsuleSlotState.None or
                DeepCapsuleSlotState.CollapsedDocked or
                DeepCapsuleSlotState.ExpandedReserved or
                DeepCapsuleSlotState.RetractedExpanded or
                DeepCapsuleSlotState.RetractingCollapsed,
            DeepCapsuleSlotState.RetractedExpanded => to is
                DeepCapsuleSlotState.None or
                DeepCapsuleSlotState.ExpandedReserved or
                DeepCapsuleSlotState.CollapsedDocked or
                DeepCapsuleSlotState.RetractedCollapsed or
                DeepCapsuleSlotState.RetractingExpanded,
            DeepCapsuleSlotState.RetractingCollapsed => to is
                DeepCapsuleSlotState.None or
                DeepCapsuleSlotState.CollapsedDocked or
                DeepCapsuleSlotState.RetractedCollapsed or
                DeepCapsuleSlotState.ExpandedReserved,
            DeepCapsuleSlotState.RetractingExpanded => to is
                DeepCapsuleSlotState.None or
                DeepCapsuleSlotState.ExpandedReserved or
                DeepCapsuleSlotState.CollapsedDocked or
                DeepCapsuleSlotState.RetractedExpanded,
            _ => false
        };
    }

    private void BeginDeepCapsuleSlotRetraction()
    {
        SetDeepCapsuleSlotState(_paper.IsCollapsed
            ? DeepCapsuleSlotState.RetractingCollapsed
            : DeepCapsuleSlotState.RetractingExpanded);
    }

    private void SetDeepCapsuleSlotForPaperForm(bool collapsed, bool reserveWhileExpanded)
    {
        var target = IsDeepCapsuleRetractedIntoMaster
            ? collapsed
                ? DeepCapsuleSlotState.RetractedCollapsed
                : DeepCapsuleSlotState.RetractedExpanded
            : collapsed
                ? DeepCapsuleSlotState.CollapsedDocked
                : reserveWhileExpanded
                    ? DeepCapsuleSlotState.ExpandedReserved
                    : DeepCapsuleSlotState.None;
        SetDeepCapsuleSlotState(target);
    }

    private void SetDeepCapsuleVisualState(DeepCapsuleVisualState state)
    {
        _deepCapsuleState = _deepCapsuleState with { Visual = state };
        UpdateDeepCapsuleSlotOutlineState();
        AssertDeepCapsuleStateInvariants();
    }

    private void SetDeepCapsuleGestureState(
        DeepCapsuleGestureState state,
        [CallerMemberName] string reason = "")
    {
        Debug.Assert(
            CanTransitionDeepCapsuleGesture(DeepCapsuleGesture, state),
            $"Illegal deep-capsule gesture transition {DeepCapsuleGesture} -> {state} ({reason}).");

        _deepCapsuleState = _deepCapsuleState with { Gesture = state };
        AssertDeepCapsuleStateInvariants();
    }

    private static bool CanTransitionDeepCapsuleGesture(DeepCapsuleGestureState from, DeepCapsuleGestureState to)
    {
        if (from == to || to == DeepCapsuleGestureState.Idle)
        {
            return true;
        }

        return from switch
        {
            DeepCapsuleGestureState.Idle => to is
                DeepCapsuleGestureState.PendingClick or
                DeepCapsuleGestureState.DockedReordering,
            DeepCapsuleGestureState.PendingClick => to == DeepCapsuleGestureState.DockedReordering,
            DeepCapsuleGestureState.DockedReordering => to == DeepCapsuleGestureState.FloatingReordering,
            DeepCapsuleGestureState.FloatingReordering => false,
            _ => false
        };
    }

    private void SetDeepCapsuleOpenOrigin(DeepCapsuleOpenOrigin origin)
    {
        _deepCapsuleState = _deepCapsuleState with { OpenOrigin = origin };
    }

    private DeepCapsuleDragSession RequireDeepCapsuleDragSession()
    {
        return _deepCapsuleDragSession ?? throw new InvalidOperationException(
            $"Deep-capsule gesture {DeepCapsuleGesture} has no drag session.");
    }

    private void BeginDeepCapsulePointerInteraction(DeviceScreenPoint pointerDownScreenPosition)
    {
        if (DeepCapsuleGesture != DeepCapsuleGestureState.Idle)
        {
            Debug.Fail($"Cannot begin a deep-capsule pointer interaction from {DeepCapsuleGesture}.");
            return;
        }

        _deepCapsuleDragSession = new DeepCapsuleDragSession(pointerDownScreenPosition);
        SetDeepCapsuleGestureState(DeepCapsuleGestureState.PendingClick);
    }

    private void FinishDeepCapsulePointerInteraction()
    {
        _deepCapsuleDragSession = null;
        SetDeepCapsuleGestureState(DeepCapsuleGestureState.Idle);
    }

    [Conditional("DEBUG")]
    private void AssertDeepCapsuleStateInvariants()
    {
        Debug.Assert(
            (DeepCapsuleGesture == DeepCapsuleGestureState.Idle) == (_deepCapsuleDragSession == null),
            "Idle deep-capsule interaction must not retain a drag session.");
        Debug.Assert(
            !IsDeepCapsuleRetractedIntoMaster || DeepCapsuleVisual == DeepCapsuleVisualState.Resting,
            "A capsule retracted into the master must have resting semantics.");
        Debug.Assert(
            DeepCapsuleSlot is not (
                DeepCapsuleSlotState.CollapsedDocked or
                DeepCapsuleSlotState.RetractedCollapsed or
                DeepCapsuleSlotState.RetractingCollapsed) || _paper.IsCollapsed,
            "A collapsed deep-capsule state requires a collapsed paper model.");
        Debug.Assert(
            DeepCapsuleSlot is not (
                DeepCapsuleSlotState.ExpandedReserved or
                DeepCapsuleSlotState.RetractedExpanded or
                DeepCapsuleSlotState.RetractingExpanded) || !_paper.IsCollapsed,
            "An expanded deep-capsule state requires an expanded paper model.");
    }
}
